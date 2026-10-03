using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NLua;

/// <summary>
/// Thrown when net.* arguments cannot be serialized (unsupported type, NaN, budget exceeded)
/// or when an incoming payload is malformed. Encode-side messages are creator-facing: they
/// surface in the calling Lua script through the NetInvoke path. Decode-side messages name the
/// malformed structure; the router logs them and drops the packet (untrusted bytes, clinerules 1.3).
/// </summary>
public sealed class NetSerializationException : Exception
{
	public NetSerializationException(string message) : base(message) { }
}

/// <summary>
/// Binary encoder/decoder for net.* call arguments (design doc v4 §7 / A6). Deliberately
/// Godot-free so it stays testable outside the engine and below the API-surface layer.
///
/// Wire format (little-endian throughout):
///   args payload: [argCount:u16] then argCount tagged values (nil holes are written as TagNil).
///   table value:  [TagTable][arrayCount:u16][array values][pairCount:u16][(key, value) pairs].
///   pair key:     [KeyString][i32 len + UTF-8] or [KeyInt][i64].
///
/// Encoding rules (settled in the design doc, do not loosen without revisiting it):
///   - Numbers are always written as 8-byte IEEE doubles. NLua hands us Lua integers as Int64
///     and floats as Double (spike B); every CLR numeric primitive is normalized here.
///     NaN is rejected (fail-fast, never diverges online/offline); ±infinity round-trips.
///   - Only the contiguous integer run 1..n forms the array section; nil holes terminate it and
///     any remaining integer keys go into the pair section, so {1, nil, 3} round-trips faithfully.
///   - Pair keys must be strings or integers; everything else is rejected.
///   - Cycles and metatables are rejected by the Lua-side validation pre-pass BEFORE this encoder
///     runs (wrapper identity is unreliable for that — LuaBase.GetHashCode is per-wrapper). The
///     depth/node/byte budgets here are the defensive backstop, enforced incrementally so an
///     over-budget encode aborts before a single byte is handed to the router.
///   - Every LuaTable/LuaFunction wrapper materialized during an encode is tracked and disposed
///     in a finally (NLua wrappers pin Lua registry references and leak otherwise). The wrapper
///     passed in by the caller is NOT disposed here; ownership stays with the caller.
/// </summary>
public static class NetSerializer
{
	private const byte TagNil = 0;
	private const byte TagBool = 1;
	private const byte TagNumber = 2;
	private const byte TagString = 3;
	private const byte TagTable = 4;

	private const byte KeyString = 1;
	private const byte KeyInt = 2;

	// Budgets (design doc v2 §7, v4 A6). Enforced on encode (creator feedback) and again on
	// decode (hostile peers can hand us arbitrary bytes once §7 networking lands).
	public const int MaxPayloadBytes = 16 * 1024;
	public const int MaxStringBytes = 8 * 1024;
	public const int MaxDepth = 8;
	public const int MaxPairsPerTable = 256;
	public const int MaxArgs = 16;
	public const int MaxNodes = 4096;

	/// <summary>
	/// Encodes a packed argument table ({ n = count, ... }) into a wire payload. Slots 1..argCount
	/// are read explicitly, so nil holes survive (NLua params marshaling is never trusted with
	/// trailing/embedded nils — design doc v2 §10b). Throws NetSerializationException on any
	/// violation; nothing is returned in that case, so nothing can be transmitted half-encoded.
	/// </summary>
	public static byte[] EncodeArgs(LuaTable args, int argCount)
	{
		if (args == null) throw new ArgumentNullException(nameof(args));
		if (argCount < 0 || argCount > MaxArgs)
			throw new NetSerializationException("net: too many arguments (max " + MaxArgs + ")");
		Encoder encoder = new Encoder();
		return encoder.Encode(args, argCount);
	}

	/// <summary>
	/// Decodes one args payload produced by <see cref="EncodeArgs"/> and pushes the resulting
	/// { n = count, ... } table onto the KeraLua stack as a real Lua table (no NLua wrappers are
	/// retained — ownership of the pushed value passes straight to the Lua GC, design doc v2 §10c).
	/// Pushes exactly one value on success. Returns the offset just past the payload. Throws
	/// NetSerializationException on malformed or over-budget bytes; on that failure path the
	/// stack is restored to its incoming top before the exception propagates, so a rejected
	/// packet never leaves residue behind (asserted by NetSelfTest.TestStackHygiene).
	/// </summary>
	public static int DecodeArgs(KeraLua.Lua state, byte[] data, int offset)
	{
		if (state == null) throw new ArgumentNullException(nameof(state));
		if (data == null) throw new ArgumentNullException(nameof(data));
		int top = state.GetTop();
		try
		{
			Decoder decoder = new Decoder(state, data, offset);
			return decoder.DecodeArgsTable();
		}
		catch
		{
			state.SetTop(top);
			throw;
		}
	}

	// --- Encoder ---

	private sealed class Encoder
	{
		private readonly MemoryStream _stream = new MemoryStream(256);
		private readonly BinaryWriter _writer;
		private readonly List<IDisposable> _materialized = new List<IDisposable>();
		private int _nodes;

		public Encoder()
		{
			_writer = new BinaryWriter(_stream, Encoding.UTF8);
		}

		public byte[] Encode(LuaTable args, int argCount)
		{
			try
			{
				_writer.Write((ushort)argCount);
				for (int i = 1; i <= argCount; i++)
				{
					object value = args[i];
					Track(value);
					WriteValue(value, 1, "argument " + i);
				}
				return _stream.ToArray();
			}
			finally
			{
				for (int i = 0; i < _materialized.Count; i++)
					_materialized[i].Dispose();
			}
		}

		private void Track(object value)
		{
			// LuaBase wrappers (tables AND functions) each pin a Lua registry reference.
			if (value is LuaBase wrapper)
				_materialized.Add(wrapper);
		}

		private void CountNode()
		{
			_nodes++;
			if (_nodes > MaxNodes)
				throw new NetSerializationException("net: too many values (node budget " + MaxNodes + " exceeded)");
		}

		private void CheckBytes()
		{
			if (_stream.Position > MaxPayloadBytes)
				throw new NetSerializationException("net: payload too large (max " + MaxPayloadBytes + " bytes)");
		}

		private void WriteValue(object value, int depth, string where)
		{
			CountNode();
			WriteValueBody(value, depth, where);
		}

		// The node-counted entry point above versus this raw body exists because array values are
		// node-counted during the probe loop (so a giant array trips the budget before a giant
		// List is built) and must not be counted twice when written here.
		private void WriteValueBody(object value, int depth, string where)
		{
			if (value == null)
			{
				_writer.Write(TagNil);
			}
			else if (value is bool boolean)
			{
				_writer.Write(TagBool);
				_writer.Write(boolean);
			}
			else if (value is string text)
			{
				_writer.Write(TagString);
				WriteStringBody(text);
			}
			else if (value is LuaTable table)
			{
				WriteTable(table, depth);
			}
			else if (IsNumber(value, out double number))
			{
				if (double.IsNaN(number))
					throw new NetSerializationException("net: cannot send NaN (" + where + ")");
				_writer.Write(TagNumber);
				_writer.Write(number);
			}
			else
			{
				throw new NetSerializationException("net: cannot send " + where + " (" + TypeName(value) + ")");
			}
			CheckBytes();
		}

		private void WriteTable(LuaTable table, int depth)
		{
			if (depth > MaxDepth)
				throw new NetSerializationException("net: table exceeds depth limit (" + MaxDepth + ")");
			_writer.Write(TagTable);

			// Contiguous array run 1..n. Lua's # is undefined for tables with holes, so the run is
			// probed explicitly and nodes are counted HERE (a huge array must trip the node budget
			// during the probe, not after a giant List has been built).
			List<object> arrayValues = new List<object>();
			while (true)
			{
				object value = table[arrayValues.Count + 1];
				if (value == null) break;
				CountNode();
				Track(value);
				arrayValues.Add(value);
			}
			_writer.Write((ushort)arrayValues.Count);
			for (int i = 0; i < arrayValues.Count; i++)
				WriteValueBody(arrayValues[i], depth + 1, "table value");

			// Pair section: every remaining key, which must be a string or an integer. Integer
			// keys inside the array run are skipped (already written above).
			List<KeyValuePair<object, object>> pairs = new List<KeyValuePair<object, object>>();
			System.Collections.IDictionaryEnumerator enumerator = table.GetEnumerator();
			while (enumerator.MoveNext())
			{
				object key = enumerator.Key;
				object value = enumerator.Value;
				if (key is long longKey && longKey >= 1 && longKey <= arrayValues.Count)
					continue;
				Track(value);
				pairs.Add(new KeyValuePair<object, object>(key, value));
			}
			if (pairs.Count > MaxPairsPerTable)
				throw new NetSerializationException("net: table too large (max " + MaxPairsPerTable + " pairs)");
			_writer.Write((ushort)pairs.Count);
			for (int i = 0; i < pairs.Count; i++)
			{
				WriteKey(pairs[i].Key);
				WriteValue(pairs[i].Value, depth + 1, "table value");
			}
		}

		private void WriteKey(object key)
		{
			if (key is string text)
			{
				_writer.Write(KeyString);
				WriteStringBody(text);
				return;
			}
			if (IsInteger(key, out long integer))
			{
				_writer.Write(KeyInt);
				_writer.Write(integer);
				return;
			}
			throw new NetSerializationException("net: table keys must be strings or integers (" + TypeName(key) + " key)");
		}

		private void WriteStringBody(string text)
		{
			int byteCount = Encoding.UTF8.GetByteCount(text);
			if (byteCount > MaxStringBytes)
				throw new NetSerializationException("net: string too long (max " + MaxStringBytes + " bytes)");
			_writer.Write(byteCount);
			_writer.Write(Encoding.UTF8.GetBytes(text));
		}

		private static bool IsInteger(object value, out long result)
		{
			switch (value)
			{
				case long l: result = l; return true;
				case int i: result = i; return true;
				case short s: result = s; return true;
				case byte b: result = b; return true;
				case sbyte sb: result = sb; return true;
				case ushort us: result = us; return true;
				case uint ui: result = ui; return true;
				default: result = 0; return false;
			}
		}

		private static bool IsNumber(object value, out double result)
		{
			if (IsInteger(value, out long integer))
			{
				result = integer;
				return true;
			}
			switch (value)
			{
				case double d: result = d; return true;
				case float f: result = f; return true;
				case ulong ul: result = ul; return true; // precision loss past 2^53: documented wire limitation
				case decimal m: result = (double)m; return true;
				default: result = 0; return false;
			}
		}

		private static string TypeName(object value)
		{
			if (value is LuaFunction) return "function";
			if (value is LuaTable) return "table";
			return value.GetType().Name;
		}
	}

	// --- Decoder ---

	private sealed class Decoder
	{
		private readonly KeraLua.Lua _state;
		private readonly byte[] _data;
		private int _offset;
		private int _nodes;

		public Decoder(KeraLua.Lua state, byte[] data, int offset)
		{
			_state = state;
			_data = data;
			_offset = offset;
		}

		public int DecodeArgsTable()
		{
			int argCount = ReadU16();
			if (argCount > MaxArgs)
				throw Malformed("argument count " + argCount + " over limit " + MaxArgs);
			// Declared counts are validated against the bytes actually remaining BEFORE the loop
			// below touches anything: each arg needs at least a 1-byte tag, so a count beyond the
			// remaining length is crafted data, rejected up front (same check in ReadTable).
			if (argCount > _data.Length - _offset)
				throw Malformed("argument count " + argCount + " exceeds remaining bytes");
			_state.NewTable();
			int tableIndex = _state.GetTop();
			for (int i = 1; i <= argCount; i++)
			{
				if (ReadValue(1))
					_state.SetInteger(tableIndex, i);
				else
					_state.Pop(1); // nil: leave a hole, exactly like the sender's packed table
			}
			_state.PushInteger(argCount);
			_state.SetField(tableIndex, "n");
			return _offset;
		}

		/// <summary>Pushes exactly one value. Returns false when the value was nil.</summary>
		private bool ReadValue(int depth)
		{
			_nodes++;
			if (_nodes > MaxNodes)
				throw Malformed("node budget " + MaxNodes + " exceeded");
			byte tag = ReadByte();
			switch (tag)
			{
				case TagNil:
					_state.PushNil();
					return false;
				case TagBool:
					_state.PushBoolean(ReadByte() != 0);
					return true;
				case TagNumber:
					double number = ReadDouble();
					if (double.IsNaN(number))
						throw Malformed("NaN on the wire");
					_state.PushNumber(number);
					return true;
				case TagString:
					_state.PushString(ReadString());
					return true;
				case TagTable:
					ReadTable(depth);
					return true;
				default:
					throw Malformed("unknown value tag " + tag);
			}
		}

		private void ReadTable(int depth)
		{
			if (depth > MaxDepth)
				throw Malformed("table depth over limit " + MaxDepth);
			_state.NewTable();
			int tableIndex = _state.GetTop();

			int arrayCount = ReadU16();
			if (arrayCount > _data.Length - _offset)
				throw Malformed("array count " + arrayCount + " exceeds remaining bytes");
			for (int i = 1; i <= arrayCount; i++)
			{
				// The encoder never writes nil inside an array section (holes terminate the run),
				// so a nil here means crafted bytes.
				if (!ReadValue(depth + 1))
					throw Malformed("nil inside array section");
				_state.SetInteger(tableIndex, i);
			}

			int pairCount = ReadU16();
			if (pairCount > MaxPairsPerTable)
				throw Malformed("pair count " + pairCount + " over limit " + MaxPairsPerTable);
			// Smallest legal pair is 2 bytes (key tag + 1 payload byte), so this is conservative.
			if (pairCount * 2 > _data.Length - _offset)
				throw Malformed("pair count " + pairCount + " exceeds remaining bytes");
			for (int p = 0; p < pairCount; p++)
			{
				byte keyTag = ReadByte();
				if (keyTag == KeyString)
				{
					string key = ReadString();
					if (!ReadValue(depth + 1))
						throw Malformed("nil pair value");
					_state.SetField(tableIndex, key);
				}
				else if (keyTag == KeyInt)
				{
					long key = ReadInt64();
					if (!ReadValue(depth + 1))
						throw Malformed("nil pair value");
					_state.SetInteger(tableIndex, key);
				}
				else
				{
					throw Malformed("unknown key tag " + keyTag);
				}
			}
		}

		private byte ReadByte()
		{
			if (_offset >= _data.Length)
				throw Malformed("truncated");
			return _data[_offset++];
		}

		private int ReadU16()
		{
			return ReadByte() | (ReadByte() << 8);
		}

		private int ReadInt32()
		{
			return ReadByte() | (ReadByte() << 8) | (ReadByte() << 16) | (ReadByte() << 24);
		}

		private long ReadInt64()
		{
			long value = 0;
			for (int i = 0; i < 8; i++)
				value |= (long)ReadByte() << (8 * i);
			return value;
		}

		private double ReadDouble()
		{
			return BitConverter.Int64BitsToDouble(ReadInt64());
		}

		private string ReadString()
		{
			int length = ReadInt32();
			if (length < 0 || length > MaxStringBytes)
				throw Malformed("string length " + length);
			if (_offset + length > _data.Length)
				throw Malformed("truncated string");
			string text = Encoding.UTF8.GetString(_data, _offset, length);
			_offset += length;
			return text;
		}

		private static NetSerializationException Malformed(string what)
		{
			return new NetSerializationException("net: malformed payload (" + what + ")");
		}
	}
}
