using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using NLua;

/// <summary>
/// Self-test for the net.* machinery (Milestone 3.2), same pattern as GizmoSelfTest: no test
/// framework in this project, so this runs from BuilderScene._Ready() under #if DEBUG and
/// prints pass/fail lines to the Godot console. Step 1 covers NetSerializer, step 2 covers
/// NetRouter + LoopbackTransport against a fake dispatch target (the Lua bootstrap lands in
/// step 3); further suites are appended as those implementation steps land. Uses a private
/// NLua state — never the lot's VM — so a failing test cannot corrupt lot state.
/// </summary>
public static class NetSelfTest
{
	private static int _failures;
	private static int _checks;
	private static Lua _lua;

	public static int Run(bool heavy = false)
	{
		_failures = 0;
		_checks = 0;
		// Deliberately provoked messages (trips, OOM, suspension) get an "[expected]" prefix.
		LuaManager.Instance.SelfTestMode = true;
		Stopwatch fastClock = Stopwatch.StartNew();
		try
		{
			_lua = new Lua();
			TestScalarRoundTrip();
			TestIntegerAndFloatMarshaling();
			TestMixedTableRoundTrip();
			TestHoleyNestedTable();
			TestEmptyTable();
			TestInfinity();
			TestUtf8RoundTrip();
			TestDepthLimits();
			TestNodeBudget();
			TestStringCap();
			TestPairsCap();
			TestArgsCap();
			TestNaNRejected();
			TestFunctionRejected();
			TestBadKeysRejected();
			TestPayloadCap();
			TestMalformedDecode();
			TestStackHygiene();
			TestDeclaredCountValidation();
			TestWrapperHygiene();
			TestRouterLoopbackOrdering();
			TestRouterRecursionBounded();
			TestRouterQueueOverflow();
			TestRouterRateLimit();
			TestRouterByteRateLimit();
			TestRouterSenderOriginOverwrite();
			TestRouterNegativeHandle();
			TestRouterUndefinedHandler();
			TestUnknownHandlerThrottle();
			TestRouterMalformedEnvelope();
			TestRouterHostBroadcast();
			TestRouterClientPaths();
			TestRouterRedeclarationWarning();
			TestRouterTransitiveIdentity();
			TestRouterOriginPaths();
			TestSandboxHardening();
			TestWatchdog();
			TestNetDispatch();
			TestScriptRuntime();
			TestClearRegistriesOnReload();
			TestCircuitBreakerScriptAttribution();
			TestRebuildNullBridgeWindow();
			TestCrossEntityCalls();
			TestScriptFileAccess();
			TestGetHandleByName();
		}
		catch (Exception ex)
		{
			_failures++;
			GD.PrintErr("[NetSelfTest] FAIL  suite setup/teardown: " + ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			if (_lua != null) _lua.Dispose();
			_lua = null;
		}
		fastClock.Stop();

		// Fast-set summary + wall clock (always on; grep "SelfTest] <n> checks" still matches).
		GD.Print("[NetSelfTest] " + _checks + " checks, " + _failures + " failure(s) in " +
			fastClock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms (fast set).");

		// Heavy set: opt-in resource tests, private Lua states only.
		if (heavy)
		{
			int baseChecks = _checks;
			int baseFailures = _failures;
			Stopwatch heavyClock = Stopwatch.StartNew();
			RunHeavyTests();
			heavyClock.Stop();
			GD.Print("[NetSelfTest] heavy suite: " + (_checks - baseChecks) + " checks, " +
				(_failures - baseFailures) + " failure(s) in " + heavyClock.Elapsed.TotalMilliseconds.ToString("0.0") + " ms.");
		}

		LuaManager.Instance.SelfTestMode = false;
		return _failures;
	}

	/// <summary>
	/// Heavy resource tests (opt-in via OPENLOT_HEAVY_TESTS). All run on private Lua states; the
	/// 8 MB out-of-memory case deliberately poisons its own VM, never the lot's.
	/// </summary>
	private static void RunHeavyTests()
	{
		TestHeavyEncodeLoop();
		TestAllocatorCap();
		TestHeavyAllocations();
	}

	// --- Assertions (GizmoSelfTest style: got/want embedded in the name) ---

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[NetSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[NetSelfTest] FAIL  " + name);
		}
	}

	// --- Helpers ---

	/// <summary>Evaluates a Lua chunk that must return a packed args table ({ n = count, ... }).</summary>
	private static LuaTable TableFromChunk(string chunk)
	{
		object[] results = _lua.DoString(chunk, "netselftest");
		return (LuaTable)results[0];
	}

	private static LuaTable Table(string expression)
	{
		return TableFromChunk("return " + expression);
	}

	private static int ArgCount(LuaTable table)
	{
		return Convert.ToInt32(table["n"]);
	}

	private static byte[] Encode(string expression)
	{
		return EncodeChunk("return " + expression);
	}

	private static byte[] EncodeChunk(string chunk)
	{
		LuaTable table = TableFromChunk(chunk);
		try
		{
			return NetSerializer.EncodeArgs(table, ArgCount(table));
		}
		finally
		{
			table.Dispose();
		}
	}

	/// <summary>Decodes into the test VM's __d global, then evaluates a Lua assertion on it.</summary>
	private static bool DecodeCheck(byte[] payload, string luaAssertion)
	{
		KeraLua.Lua state = _lua.State;
		int top = state.GetTop();
		try
		{
			int end = NetSerializer.DecodeArgs(state, payload, 0);
			if (end != payload.Length) return false;
			state.SetGlobal("__d");
			object[] results = _lua.DoString("return (" + luaAssertion + ")", "netselftest");
			return results.Length > 0 && results[0] is bool ok && ok;
		}
		finally
		{
			state.SetTop(top);
		}
	}

	private static void CheckEncodeThrows(string name, string chunk, string messagePart)
	{
		LuaTable table = TableFromChunk(chunk);
		try
		{
			NetSerializer.EncodeArgs(table, ArgCount(table));
			Check(name + " (no exception thrown)", false);
		}
		catch (NetSerializationException ex)
		{
			Check(name + " (got: " + ex.Message + ")", ex.Message.Contains(messagePart));
		}
		finally
		{
			table.Dispose();
		}
	}

	// --- Serializer tests ---

	private static void TestScalarRoundTrip()
	{
		byte[] payload = Encode("{ n = 3, 1, nil, 3 }");
		// Exact wire bytes: u16 count + (tag+double) + nil tag + (tag+double) = 2+9+1+9.
		Check("scalar payload is 21 bytes (got " + payload.Length + ")", payload.Length == 21);
		Check("scalars and nil hole round-trip",
			DecodeCheck(payload, "__d.n == 3 and __d[1] == 1 and __d[2] == nil and __d[3] == 3"));
		// Locks the documented rule: numbers always cross as doubles, so the receiver sees floats.
		Check("decoded numbers are float subtype",
			DecodeCheck(payload, "math.type(__d[1]) == 'float'"));
	}

	private static void TestIntegerAndFloatMarshaling()
	{
		// NLua boundary contract the serializer relies on (spike B): Lua integers arrive as
		// Int64, floats as Double. If an NLua upgrade changes this, this check fails first.
		LuaTable table = Table("{ n = 2, 1, 1.5 }");
		try
		{
			object integer = table[1];
			object real = table[2];
			Check("lua integer arrives as Int64 (got " + (integer == null ? "null" : integer.GetType().Name) + ")",
				integer is long);
			Check("lua float arrives as Double (got " + (real == null ? "null" : real.GetType().Name) + ")",
				real is double);
		}
		finally
		{
			table.Dispose();
		}
	}

	private static void TestMixedTableRoundTrip()
	{
		byte[] payload = Encode("{ n = 1, { pos = {10, 20}, name = 'door' } }");
		Check("mixed-key nested table round-trips",
			DecodeCheck(payload, "__d[1].pos[1] == 10 and __d[1].pos[2] == 20 and __d[1].name == 'door'"));
	}

	private static void TestHoleyNestedTable()
	{
		byte[] payload = Encode("{ n = 1, {1, nil, 3} }");
		Check("array with hole round-trips faithfully",
			DecodeCheck(payload, "__d[1][1] == 1 and __d[1][2] == nil and __d[1][3] == 3"));
	}

	private static void TestEmptyTable()
	{
		byte[] payload = Encode("{ n = 1, {} }");
		Check("empty table round-trips as empty table",
			DecodeCheck(payload, "type(__d[1]) == 'table' and next(__d[1]) == nil"));
	}

	private static void TestInfinity()
	{
		byte[] payload = Encode("{ n = 2, math.huge, -math.huge }");
		Check("plus/minus infinity round-trips",
			DecodeCheck(payload, "__d[1] == math.huge and __d[2] == -math.huge"));
	}

	private static void TestUtf8RoundTrip()
	{
		byte[] payload = Encode("{ n = 1, 'héllo→世界' }");
		Check("utf-8 string round-trips",
			DecodeCheck(payload, "__d[1] == 'héllo→世界'"));
	}

	private static void TestDepthLimits()
	{
		// k wraps = k nested tables; the innermost table's depth equals k (the arg itself is
		// depth 1). 8 wraps: innermost at depth 8 — allowed. 9 wraps: depth 9 — rejected.
		byte[] ok = EncodeChunk("local t = 1 for i = 1, 8 do t = {t} end return { n = 1, t }");
		Check("8-deep nesting accepted", ok.Length > 0);
		CheckEncodeThrows("9-deep nesting rejected",
			"local t = 1 for i = 1, 9 do t = {t} end return { n = 1, t }", "depth limit");
	}

	private static void TestNodeBudget()
	{
		CheckEncodeThrows("5000-entry array trips node budget",
			"local t = {} for i = 1, 5000 do t[i] = i end return { n = 1, t }", "node budget");
	}

	private static void TestStringCap()
	{
		byte[] ok = Encode("{ n = 1, string.rep('x', " + NetSerializer.MaxStringBytes + ") }");
		Check("max-size string accepted", ok.Length > 0);
		CheckEncodeThrows("over-size string rejected",
			"return { n = 1, string.rep('x', " + (NetSerializer.MaxStringBytes + 1) + ") }", "string too long");
	}

	private static void TestPairsCap()
	{
		byte[] ok = EncodeChunk("local t = {} for i = 1, " + NetSerializer.MaxPairsPerTable + " do t['k'..i] = i end return { n = 1, t }");
		Check("max-pairs table accepted", ok.Length > 0);
		CheckEncodeThrows("over-pairs table rejected",
			"local t = {} for i = 1, " + (NetSerializer.MaxPairsPerTable + 1) + " do t['k'..i] = i end return { n = 1, t }", "table too large");
	}

	private static void TestArgsCap()
	{
		CheckEncodeThrows("17 arguments rejected", "return { n = 17 }", "too many arguments");
	}

	private static void TestNaNRejected()
	{
		CheckEncodeThrows("NaN rejected", "return { n = 1, 0/0 }", "NaN");
	}

	private static void TestFunctionRejected()
	{
		CheckEncodeThrows("function argument rejected", "return { n = 1, function() end }", "cannot send");
	}

	private static void TestBadKeysRejected()
	{
		CheckEncodeThrows("float key rejected", "return { n = 1, { [1.5] = 'x' } }", "keys");
		CheckEncodeThrows("boolean key rejected", "return { n = 1, { [true] = 'x' } }", "keys");
		CheckEncodeThrows("table key rejected", "return { n = 1, { [{}] = 'x' } }", "keys");
	}

	private static void TestPayloadCap()
	{
		// Three 6 KB strings stay under the per-string cap but exceed the 16 KB payload budget.
		CheckEncodeThrows("over-16KB payload rejected",
			"return { n = 3, string.rep('x', 6000), string.rep('y', 6000), string.rep('z', 6000) }",
			"payload too large");
	}

	private static void TestMalformedDecode()
	{
		byte[] payload = Encode("{ n = 2, 'hello', 42 }");

		byte[] truncated = new byte[payload.Length - 2];
		Array.Copy(payload, truncated, truncated.Length);
		CheckDecodeThrows("truncated payload rejected", truncated, "malformed");

		byte[] corrupted = (byte[])payload.Clone();
		corrupted[2] = 99; // first value's tag byte
		CheckDecodeThrows("unknown tag rejected", corrupted, "malformed");
	}

	private static void CheckDecodeThrows(string name, byte[] payload, string messagePart)
	{
		KeraLua.Lua state = _lua.State;
		int top = state.GetTop();
		try
		{
			NetSerializer.DecodeArgs(state, payload, 0);
			Check(name + " (no exception thrown)", false);
		}
		catch (NetSerializationException ex)
		{
			Check(name + " (got: " + ex.Message + ")", ex.Message.Contains(messagePart));
		}
		finally
		{
			state.SetTop(top);
		}
	}

	private static void TestWrapperHygiene()
	{
		// Functional half of the wrapper-disposal rule (design doc v3 §6): encodes of a nested
		// table must not leak Lua registry references. Heuristic: undisposed wrappers pin one
		// registry entry each, which shows up as LINEAR heap growth — so compare the growth over
		// the first 2,000 encodes against the next 18,000. Flat = fine, linear = leak. The
		// disposal path itself is verified by code review (finally in Encoder.Encode).
		_lua.DoString("collectgarbage('collect') __kb0 = collectgarbage('count')", "netselftest");
		for (int i = 0; i < 2000; i++)
			Encode("{ n = 2, { a = {1, 2, 3}, b = 'x' }, { { { 1 } } } }");
		_lua.DoString("collectgarbage('collect') __kb1 = collectgarbage('count')", "netselftest");
		for (int i = 0; i < 18000; i++)
			Encode("{ n = 2, { a = {1, 2, 3}, b = 'x' }, { { { 1 } } } }");
		_lua.DoString("collectgarbage('collect') __kb2 = collectgarbage('count')", "netselftest");
		double growth2k = Convert.ToDouble(_lua["__kb1"]) - Convert.ToDouble(_lua["__kb0"]);
		double growth18k = Convert.ToDouble(_lua["__kb2"]) - Convert.ToDouble(_lua["__kb1"]);
		// A linear leak at the 2k-measured rate would produce ~9x that growth over 18k; allow a
		// flat 1 MB of GC-noise headroom instead. Both numbers are reported either way.
		Check("wrapper disposal flat: " + growth2k.ToString("0") + " KB over 2k encodes, " +
			growth18k.ToString("0") + " KB over the next 18k (want < 1024)",
			growth18k < 1024.0);
	}

	// --- Step-1 follow-ups: decoder stack hygiene + declared-count pre-validation ---

	private static void TestStackHygiene()
	{
		// DecodeArgs must restore the KeraLua stack to its incoming top on EVERY rejection path
		// (it self-restores now; these checks pin that contract). Crafted per rejection path:
		byte[] valid = Encode("{ n = 2, 'hello', 42 }");

		byte[] truncated = new byte[valid.Length - 2];
		Array.Copy(valid, truncated, truncated.Length);

		byte[] unknownTag = (byte[])valid.Clone();
		unknownTag[2] = 99;

		byte[] nanOnWire = new byte[11];
		nanOnWire[0] = 1; // argCount 1
		nanOnWire[2] = 2; // TagNumber
		Array.Copy(BitConverter.GetBytes(double.NaN), 0, nanOnWire, 3, 8);

		byte[] emptyTable = Encode("{ n = 1, {} }");
		byte[] oversizedPairs = (byte[])emptyTable.Clone();
		oversizedPairs[5] = 0xFF; // pairCount u16 at bytes 5..6
		oversizedPairs[6] = 0xFF;

		CheckStackClean("stack clean after truncated rejection", truncated);
		CheckStackClean("stack clean after unknown-tag rejection", unknownTag);
		CheckStackClean("stack clean after NaN-on-wire rejection", nanOnWire);
		CheckStackClean("stack clean after oversized-pairs rejection", oversizedPairs);

		// Success path is net-zero too: the pushed args table is consumed here via SetGlobal.
		KeraLua.Lua state = _lua.State;
		int top = state.GetTop();
		NetSerializer.DecodeArgs(state, valid, 0);
		state.SetGlobal("__d");
		bool clean = state.GetTop() == top;
		if (!clean) state.SetTop(top);
		Check("stack clean after successful decode", clean);
	}

	private static void CheckStackClean(string name, byte[] payload)
	{
		KeraLua.Lua state = _lua.State;
		int top = state.GetTop();
		try
		{
			NetSerializer.DecodeArgs(state, payload, 0);
		}
		catch (NetSerializationException)
		{
			// expected; hygiene is what we are asserting
		}
		bool clean = state.GetTop() == top;
		if (!clean) state.SetTop(top); // keep the test VM usable even when the check fails
		Check(name + " (got " + (state.GetTop() - top) + " extra slot(s))", clean);
	}

	private static void TestDeclaredCountValidation()
	{
		// Crafted counts are rejected against the remaining byte length BEFORE the decoder walks
		// or allocates anything (the guards live in Decoder; asserted via message text).
		byte[] lyingArgs = new byte[] { 0x10, 0x00 }; // argCount 16 (at cap) with zero bytes following
		CheckDecodeThrows("argCount beyond remaining bytes rejected", lyingArgs, "exceeds remaining bytes");

		byte[] emptyTable = Encode("{ n = 1, {} }");
		byte[] lyingArray = (byte[])emptyTable.Clone();
		lyingArray[3] = 0xFF; // arrayCount u16 at bytes 3..4
		lyingArray[4] = 0xFF;
		CheckDecodeThrows("arrayCount beyond remaining bytes rejected", lyingArray, "exceeds remaining bytes");

		byte[] lyingPairs = (byte[])emptyTable.Clone();
		lyingPairs[5] = 200; // pairCount 200 (under the 256 cap) with nothing following
		lyingPairs[6] = 0;
		CheckDecodeThrows("pairCount beyond remaining bytes rejected", lyingPairs, "exceeds remaining bytes");
	}

	// --- Step 2: NetRouter + LoopbackTransport (fake dispatch target; Lua lands in step 3) ---

	private sealed class FakeTransport : INetTransport
	{
		public bool IsHost { get; set; }
		public int LocalPeerId { get; set; }
		public event Action<int, byte[]> MessageReceived;
		public readonly List<byte[]> SentToHost = new List<byte[]>();
		public readonly List<byte[]> Broadcasts = new List<byte[]>();

		public void Inject(int fromPeer, byte[] payload)
		{
			MessageReceived?.Invoke(fromPeer, payload);
		}

		public void SendToHost(byte[] payload) { SentToHost.Add(payload); }
		public void BroadcastToClients(byte[] payload) { Broadcasts.Add(payload); }
		public void SendToClient(int peerId, byte[] payload) { }
	}

	private sealed class FakeDispatch : IDispatchTarget
	{
		public readonly List<string> Calls = new List<string>();
		public readonly HashSet<string> Known = new HashSet<string>();
		public Action<int, NetDirection, string, int, NetOrigin> OnDispatch;

		public static string Key(int handle, NetDirection direction, string name)
		{
			return handle + "|" + direction + "|" + name;
		}

		public bool TryDispatch(int handle, NetDirection direction, string name, int senderPeerId, NetOrigin origin, byte[] argsPayload)
		{
			string key = Key(handle, direction, name);
			if (!Known.Contains(key)) return false;
			Calls.Add(key + "|sender=" + senderPeerId + "|origin=" + origin + "|argsBytes=" + argsPayload.Length);
			Action<int, NetDirection, string, int, NetOrigin> handler = OnDispatch;
			if (handler != null) handler(handle, direction, name, senderPeerId, origin);
			return true;
		}
	}

	private static NetRouter MakeRouter(INetTransport transport, FakeDispatch dispatch, List<string> warnings, Func<double> clock)
	{
		NetRouter router = new NetRouter(transport, dispatch, clock);
		router.Warning += warnings.Add;
		return router;
	}

	private static void TestRouterLoopbackOrdering()
	{
		LoopbackTransport transport = new LoopbackTransport();
		Check("loopback transport is host peer 1",
			transport.IsHost && transport.LocalPeerId == 1);

		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(7, NetDirection.Server, "A"));
		dispatch.Known.Add(FakeDispatch.Key(7, NetDirection.Server, "B"));
		dispatch.Known.Add(FakeDispatch.Key(7, NetDirection.Server, "C"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		router.InvokeLocal(7, NetDirection.Server, "B", null);
		router.InvokeLocal(7, NetDirection.Server, "A", null);
		router.InvokeLocal(7, NetDirection.Server, "C", null);
		Check("uniform queueing: nothing dispatches before Flush", dispatch.Calls.Count == 0);
		router.Flush();
		Check("FIFO order preserved through the queue",
			dispatch.Calls.Count == 3
			&& dispatch.Calls[0].StartsWith("7|Server|B|")
			&& dispatch.Calls[1].StartsWith("7|Server|A|")
			&& dispatch.Calls[2].StartsWith("7|Server|C|"));
		// Item 4 (C): autonomous local calls are SYSTEM even in loopback; Player needs an explicit
		// interaction scope.
		Check("loopback sender is the host peer and an autonomous call is System",
			dispatch.Calls[0].Contains("sender=1") && dispatch.Calls[0].Contains("origin=System"));
		router.Detach();
	}

	private static void TestRouterRecursionBounded()
	{
		LoopbackTransport transport = new LoopbackTransport();
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(1, NetDirection.Server, "Ping"));
		dispatch.Known.Add(FakeDispatch.Key(1, NetDirection.Server, "Pong"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);
		dispatch.OnDispatch = (h, d, n, s, o) =>
		{
			// A→B→A forever: each hop enqueues, so this grows the queue, never the stack.
			router.InvokeLocal(1, NetDirection.Server, n == "Ping" ? "Pong" : "Ping", null);
		};

		router.InvokeLocal(1, NetDirection.Server, "Ping", null);
		router.Flush();
		Check("runaway recursion bounded by the drain budget (dispatched " + dispatch.Calls.Count + ")",
			dispatch.Calls.Count == NetRouter.MaxDrainPerFrame);
		dispatch.OnDispatch = null;
		router.Flush();
		Check("queue drains once handlers stop (calls " + dispatch.Calls.Count + ", queued " + router.QueuedCount + ")",
			dispatch.Calls.Count == NetRouter.MaxDrainPerFrame + 1 && router.QueuedCount == 0);
		router.Detach();
	}

	private static void TestRouterQueueOverflow()
	{
		LoopbackTransport transport = new LoopbackTransport();
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(5, NetDirection.Server, "Flood"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		for (int i = 0; i < NetRouter.MaxQueuedMessages + 100; i++)
			router.InvokeLocal(5, NetDirection.Server, "Flood", null);
		Check("queue capped at " + NetRouter.MaxQueuedMessages + " (got " + router.QueuedCount + ")",
			router.QueuedCount == NetRouter.MaxQueuedMessages);
		Check("queue-full warned once at a frozen clock (got " + warnings.Count + ")",
			warnings.Count == 1 && warnings[0].Contains("queue full"));
		router.Detach();
	}

	private static void TestRouterRateLimit()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "Hit"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);
		int rateLimitedPeer = -1;
		router.PeerRateLimited += peer => rateLimitedPeer = peer;

		byte[] hit = NetRouter.EncodeEnvelope(3, NetDirection.Server, NetOrigin.Player, 42, "Hit", NetRouter.EmptyArgsPayload);

		for (int i = 0; i < NetRouter.CallBurstAllowance + 10; i++) transport.Inject(5, hit);
		router.Flush();
		Check("burst allowance accepts exactly " + NetRouter.CallBurstAllowance + " (dispatched " + dispatch.Calls.Count + ")",
			dispatch.Calls.Count == NetRouter.CallBurstAllowance);
		Check("rate-limit warning throttled to 1 within the window (got " + warnings.Count + ")",
			warnings.Count == 1 && warnings[0].Contains("peer 5") && warnings[0].Contains("rate-limited"));
		Check("PeerRateLimited event fired for the plugin hook", rateLimitedPeer == 5);

		now += 0.25; // 120 tokens/s refills 30
		for (int i = 0; i < 30; i++) transport.Inject(5, hit);
		router.Flush();
		Check("tokens refill from the injected clock (dispatched " + dispatch.Calls.Count + " total)",
			dispatch.Calls.Count == 60);

		// 0.125s is exactly representable in a double (0.1s is not: 0.1 * 120 lands a hair under
		// 12 tokens and the boundary check drops one — the test clock must use exact fractions).
		now += 0.125; // 15 tokens
		for (int i = 0; i < 20; i++) transport.Inject(5, hit);
		router.Flush();
		Check("partial refill accepts 15 (dispatched " + dispatch.Calls.Count + " total)", dispatch.Calls.Count == 75);
		Check("warning still throttled inside 1s (got " + warnings.Count + ")", warnings.Count == 1);

		now += 1.0; // throttle window passes; tokens refill to the 30 burst cap
		for (int i = 0; i < 40; i++) transport.Inject(5, hit);
		router.Flush();
		Check("second throttle window warns again (got " + warnings.Count + ")", warnings.Count == 2);
		router.Detach();
	}

	private static void TestRouterByteRateLimit()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "Hit"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		// Content is irrelevant: the receipt path never parses args before the budget check.
		byte[] fat = NetRouter.EncodeEnvelope(3, NetDirection.Server, NetOrigin.Player, 42, "Hit", new byte[8000]);
		// 8014 bytes per envelope against a 65536-token byte bucket: 8 pass, the 9th drops.
		for (int i = 0; i < 9; i++) transport.Inject(6, fat);
		router.Flush();
		Check("per-peer byte budget caps fat floods (dispatched " + dispatch.Calls.Count + " of 9)",
			dispatch.Calls.Count == 8);
		Check("byte-cap drop warns rate-limited",
			warnings.Count == 1 && warnings[0].Contains("rate-limited"));
		router.Detach();
	}

	private static void TestRouterSenderOriginOverwrite()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "Hit"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		// A client claiming to be peer 999 with System origin: both must be overwritten (§1.3).
		byte[] forged = NetRouter.EncodeEnvelope(3, NetDirection.Server, NetOrigin.System, 999, "Hit", NetRouter.EmptyArgsPayload);
		transport.Inject(7, forged);
		router.Flush();
		Check("sender overwritten from transport metadata (7, not 999)",
			dispatch.Calls.Count == 1 && dispatch.Calls[0].Contains("sender=7"));
		Check("origin overwritten to Player for remote peers",
			dispatch.Calls[0].Contains("origin=Player"));
		router.Detach();
	}

	private static void TestRouterNegativeHandle()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "Hit"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		byte[] bad = NetRouter.EncodeEnvelope(-5, NetDirection.Server, NetOrigin.Player, 9, "Hit", NetRouter.EmptyArgsPayload);
		transport.Inject(9, bad);
		router.Flush();
		Check("negative handle from the wire dropped", dispatch.Calls.Count == 0);
		Check("negative handle warned",
			warnings.Count == 1 && warnings[0].Contains("negative entity handle"));
		router.Detach();
	}

	private static void TestRouterUndefinedHandler()
	{
		LoopbackTransport transport = new LoopbackTransport();
		FakeDispatch dispatch = new FakeDispatch(); // nothing registered as Known
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		router.InvokeLocal(9, NetDirection.Server, "Missing", null);
		router.Flush();
		Check("undefined handler warns and drops",
			dispatch.Calls.Count == 0 && warnings.Count == 1 && warnings[0].Contains("no handler for net.server.Missing"));
		router.Detach();
	}

	private static void TestUnknownHandlerThrottle()
	{
		// Item 6: missing handlers are aggregated per (handle, direction, name) and reported as one
		// throttled summary per frame instead of one warning per call.
		LoopbackTransport transport = new LoopbackTransport();
		FakeDispatch dispatch = new FakeDispatch(); // nothing Known: every call is unknown
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		for (int i = 0; i < 5; i++) router.InvokeLocal(9, NetDirection.Server, "Missing", null);
		router.Flush();
		Check("a burst of one missing handler yields one summary line (got " + warnings.Count + ")",
			warnings.Count == 1 && warnings[0].Contains("no handler for net.server.Missing")
			&& warnings[0].Contains("(5x)"));

		// Still inside the throttle window: occurrences accumulate, no second line.
		for (int i = 0; i < 3; i++) router.InvokeLocal(9, NetDirection.Server, "Missing", null);
		router.Flush();
		Check("hits inside the throttle window do not warn again (got " + warnings.Count + ")",
			warnings.Count == 1);

		// Window passes: the accumulated count is reported truthfully (nothing lost while throttled).
		now += 1.0;
		router.Flush();
		Check("accumulated hits are reported once the window passes (got " + warnings.Count + ")",
			warnings.Count == 2 && warnings[1].Contains("no handler for net.server.Missing")
			&& warnings[1].Contains("(3x)"));

		// Two distinct missing handlers in one frame share a single summary, ordered by handle.
		now += 1.0;
		router.InvokeLocal(9, NetDirection.Server, "Missing", null);
		router.InvokeLocal(7, NetDirection.Client, "Ghost", null);
		router.Flush();
		Check("distinct missing handlers share one frame summary naming both (got " + warnings.Count + ")",
			warnings.Count == 3 && warnings[2].Contains("no handler for net.server.Missing")
			&& warnings[2].Contains("no handler for net.client.Ghost"));
		Check("summary is ordered by entity handle (7 before 9)",
			warnings[2].IndexOf("on entity 7") < warnings[2].IndexOf("on entity 9"));
		router.Detach();
	}

	private static void TestRouterMalformedEnvelope()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "Hit"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		transport.Inject(9, new byte[] { 1, 2, 3 }); // far below the minimum envelope size

		byte[] badDirection = NetRouter.EncodeEnvelope(3, NetDirection.Server, NetOrigin.Player, 9, "Hit", NetRouter.EmptyArgsPayload);
		badDirection[4] = 9; // direction byte
		transport.Inject(9, badDirection);

		// An empty function name is malformed at decode time (encode stays permissive).
		byte[] emptyName = NetRouter.EncodeEnvelope(3, NetDirection.Server, NetOrigin.Player, 9, "", NetRouter.EmptyArgsPayload);
		transport.Inject(9, emptyName);

		router.Flush();
		Check("malformed envelopes never dispatch", dispatch.Calls.Count == 0);
		Check("malformed envelopes each warn (got " + warnings.Count + ")",
			warnings.Count == 3 && warnings[0].Contains("malformed") && warnings[1].Contains("unknown direction")
			&& warnings[2].Contains("empty function name"));
		router.Detach();
	}

	private static void TestRouterHostBroadcast()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(8, NetDirection.Client, "Creak"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		router.InvokeLocal(8, NetDirection.Client, "Creak", null);
		Check("host net.client broadcasts once (got " + transport.Broadcasts.Count + ")",
			transport.Broadcasts.Count == 1);
		bool decoded = NetRouter.TryDecodeEnvelope(transport.Broadcasts[0], out NetRouter.Envelope env, out string error);
		Check("broadcast carries the same envelope" + (decoded ? "" : " (decode: " + error + ")"),
			decoded && env.TargetHandle == 8 && env.Direction == NetDirection.Client && env.Name == "Creak");
		router.Flush();
		Check("host also runs its own net.client handler exactly once (host is a player too)",
			dispatch.Calls.Count == 1 && dispatch.Calls[0].StartsWith("8|Client|Creak|"));
		router.Detach();
	}

	private static void TestRouterClientPaths()
	{
		FakeTransport transport = new FakeTransport { IsHost = false, LocalPeerId = 9 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(8, NetDirection.Client, "Creak"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		router.InvokeLocal(3, NetDirection.Server, "Hit", null);
		Check("client net.server transmits to the host, never the local queue",
			transport.SentToHost.Count == 1 && router.QueuedCount == 0);

		router.InvokeLocal(8, NetDirection.Client, "Creak", null);
		Check("client net.client is local-only (no sends)",
			transport.SentToHost.Count == 1 && transport.Broadcasts.Count == 0 && router.QueuedCount == 1);
		router.Flush();
		Check("client net.client dispatches locally (own view only)",
			dispatch.Calls.Count == 1 && dispatch.Calls[0].Contains("sender=9"));
		router.Detach();
	}

	private static void TestRouterRedeclarationWarning()
	{
		LoopbackTransport transport = new LoopbackTransport();
		FakeDispatch dispatch = new FakeDispatch();
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		router.RegisterHandler(7, NetDirection.Server, "Open", "Door.lua");
		router.RegisterHandler(7, NetDirection.Server, "Open", "Door.lua"); // re-run: silent
		Check("same-script re-declaration is silent", warnings.Count == 0);
		router.RegisterHandler(7, NetDirection.Server, "Open", "Button.lua");
		Check("two scripts sharing one name warn once, naming both",
			warnings.Count == 1 && warnings[0].Contains("Door.lua") && warnings[0].Contains("Button.lua"));
		Check("handler registry tracks the declaration", router.HasHandler(7, NetDirection.Server, "Open"));
		router.UnregisterEntity(7);
		Check("UnregisterEntity drops the entity's declarations", !router.HasHandler(7, NetDirection.Server, "Open"));
		router.Detach();
	}

	private static void TestRouterOriginPaths()
	{
		// Item 4 (C): autonomous calls (lot load, start(), timers) are System in ALL modes, Player
		// only inside an explicit interaction scope; both inherit transitively through a dispatch.
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "Act"));
		dispatch.Known.Add(FakeDispatch.Key(4, NetDirection.Server, "Chained"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);

		router.InvokeLocal(3, NetDirection.Server, "Act", NetRouter.EmptyArgsPayload);
		router.Flush();
		Check("autonomous local call is System (" + Last(dispatch) + ")", Last(dispatch).Contains("origin=System"));

		router.BeginPlayerInteraction();
		router.InvokeLocal(3, NetDirection.Server, "Act", NetRouter.EmptyArgsPayload);
		router.EndPlayerInteraction();
		router.Flush();
		Check("a call inside a player-interaction scope is Player (" + Last(dispatch) + ")",
			Last(dispatch).Contains("origin=Player"));

		// Nesting is depth-counted: one End must not close two scopes.
		router.BeginPlayerInteraction();
		router.BeginPlayerInteraction();
		router.EndPlayerInteraction();
		router.InvokeLocal(3, NetDirection.Server, "Act", NetRouter.EmptyArgsPayload);
		router.EndPlayerInteraction();
		router.Flush();
		Check("nested interaction scopes are depth-counted (" + Last(dispatch) + ")",
			Last(dispatch).Contains("origin=Player"));
		router.InvokeLocal(3, NetDirection.Server, "Act", NetRouter.EmptyArgsPayload);
		router.Flush();
		Check("after the last End the origin is System again (" + Last(dispatch) + ")",
			Last(dispatch).Contains("origin=System"));

		// Transitive: a chained call inherits the dispatch's origin, both ways.
		dispatch.OnDispatch = (h, d, n, s, o) => router.InvokeLocal(4, NetDirection.Server, "Chained", NetRouter.EmptyArgsPayload);
		router.BeginPlayerInteraction();
		router.InvokeLocal(3, NetDirection.Server, "Act", NetRouter.EmptyArgsPayload);
		router.EndPlayerInteraction();
		router.Flush();
		Check("a call chained from a Player dispatch inherits Player (" + Last(dispatch) + ")",
			Last(dispatch).Contains("origin=Player"));
		router.InvokeLocal(3, NetDirection.Server, "Act", NetRouter.EmptyArgsPayload);
		router.Flush();
		Check("a call chained from a System dispatch inherits System (" + Last(dispatch) + ")",
			Last(dispatch).Contains("origin=System"));
		router.Detach();
	}

	private static string Last(FakeDispatch dispatch)
	{
		return dispatch.Calls.Count > 0 ? dispatch.Calls[dispatch.Calls.Count - 1] : "(no calls)";
	}

	private static void TestRouterTransitiveIdentity()
	{
		FakeTransport transport = new FakeTransport { IsHost = true, LocalPeerId = 1 };
		FakeDispatch dispatch = new FakeDispatch();
		dispatch.Known.Add(FakeDispatch.Key(3, NetDirection.Server, "First"));
		dispatch.Known.Add(FakeDispatch.Key(4, NetDirection.Server, "Second"));
		List<string> warnings = new List<string>();
		double now = 0;
		NetRouter router = MakeRouter(transport, dispatch, warnings, () => now);
		dispatch.OnDispatch = (h, d, n, s, o) =>
		{
			// The handler for First initiates another server call mid-dispatch: it must carry
			// the ORIGINAL caller's identity (peer 7), not the host's own (v4 transitive rule).
			if (n == "First")
				router.InvokeLocal(4, NetDirection.Server, "Second", null);
		};

		transport.Inject(7, NetRouter.EncodeEnvelope(3, NetDirection.Server, NetOrigin.Player, 7, "First", NetRouter.EmptyArgsPayload));
		router.Flush();
		Check("handler-initiated call inherits the original sender and origin",
			dispatch.Calls.Count == 2
			&& dispatch.Calls[0].Contains("sender=7")
			&& dispatch.Calls[1].StartsWith("4|Server|Second|")
			&& dispatch.Calls[1].Contains("sender=7")
			&& dispatch.Calls[1].Contains("origin=Player"));
		router.Detach();
	}

	// --- Step 3: bootstrap sandbox, net sugar, watchdog, allocator ---
	//
	// These tests build a private VM with the real LuaBootstrap chunk applied plus a real router
	// and bridge, so the whole Lua -> bridge -> router -> bridge -> Lua round trip is exercised.

	private sealed class TestRuntime
	{
		public Lua State;
		public LuaNetBridge Bridge;
		public NetRouter Router;
		public LuaWatchdog Watchdog;
		public LuaMemoryGuard Guard;
		public readonly List<string> Warnings = new List<string>();
		/// <summary>What the scripts under test wrote via Lot.Log. ScriptRuntime's envs are
		/// stack-only by design (no C# reference is retained), so observable behaviour — not env
		/// inspection — is how effects are verified.</summary>
		public readonly List<string> LotMessages = new List<string>();

		public void OnLotLog(string message)
		{
			LotMessages.Add(message);
		}
	}

	private static TestRuntime CreateRuntime()
	{
		TestRuntime runtime = new TestRuntime();
		runtime.State = new Lua();
		runtime.State.RegisterFunction("__testLotLog", runtime, typeof(TestRuntime).GetMethod(nameof(TestRuntime.OnLotLog)));
		runtime.State.DoString(
			"Lot = { Log = function(message) __testLotLog(tostring(message)) end, " +
			"SpawnCube = function(x, y, z) return 0 end, " +
			"SpawnSphere = function(x, y, z) return 0 end, SpawnCylinder = function(x, y, z) return 0 end }",
			"testlot");
		LuaBootstrap.Apply(runtime.State);

		// Test-side helpers in the REAL global env (never part of the production bootstrap): one
		// persistent sandbox env per handle, plus stack-friendly ways to run chunks and read values.
		runtime.State.DoString(@"
__testEnvs = {}
function __testEnv(handle)
    local env = __testEnvs[handle]
    if env == nil then
        env = __openlot_newEnv(handle)
        __testEnvs[handle] = env
    end
    return env
end
function __testTry(handle, chunk)
    local fn, err = __openlot_load(chunk, 'testchunk', __testEnvs[handle])
    if fn == nil then return false, err end
    return pcall(fn)
end
function __testGet(handle, name)
    return __testEnvs[handle][name]
end
", "testhelpers");

		runtime.Guard = new LuaMemoryGuard();
		runtime.Watchdog = new LuaWatchdog(runtime.State);
		runtime.Bridge = new LuaNetBridge();
		runtime.Bridge.Attach(runtime.State);
		runtime.Bridge.Watchdog = runtime.Watchdog;
		runtime.Bridge.MemoryGuard = runtime.Guard;
		runtime.Bridge.Warning += runtime.Warnings.Add;
		runtime.Router = new NetRouter(new LoopbackTransport(), runtime.Bridge);
		runtime.Router.Warning += runtime.Warnings.Add;
		runtime.Bridge.Router = runtime.Router;

		// The two bootstrap plumbing globals, exactly as LuaManager registers them off LotLuaApi
		// (production routes LotLuaApi.NetInvoke/NetRegister -> these same bridge methods).
		runtime.State.RegisterFunction("NetRegister", runtime.Bridge,
			typeof(LuaNetBridge).GetMethod(nameof(LuaNetBridge.RegisterHandler)));
		runtime.State.RegisterFunction("NetInvoke", runtime.Bridge,
			typeof(LuaNetBridge).GetMethod(nameof(LuaNetBridge.InvokeFromLua)));
		runtime.State.RegisterFunction("CallServer", runtime.Bridge,
			typeof(LuaNetBridge).GetMethod(nameof(LuaNetBridge.CallServer)));
		runtime.State.RegisterFunction("CallClient", runtime.Bridge,
			typeof(LuaNetBridge).GetMethod(nameof(LuaNetBridge.CallClient)));
		return runtime;
	}

	private static void DisposeRuntime(TestRuntime runtime)
	{
		runtime.Bridge.Detach();
		runtime.State.Dispose();
		runtime.Guard.Dispose();
	}

	private static (bool ok, string err) RunInEnv(TestRuntime runtime, int handle, string chunk)
	{
		KeraLua.Lua raw = runtime.State.State;
		int top = raw.GetTop();
		try
		{
			raw.GetGlobal("__testEnv");
			raw.PushInteger(handle);
			if (raw.PCall(1, 0, 0) != KeraLua.LuaStatus.OK)
				return (false, "env error: " + raw.ToString(-1, false));

			raw.GetGlobal("__testTry");
			raw.PushInteger(handle);
			raw.PushString(chunk);
			if (raw.PCall(2, 2, 0) != KeraLua.LuaStatus.OK)
				return (false, "call error: " + raw.ToString(-1, false));

			bool ok = raw.ToBoolean(-2);
			string err = raw.Type(-1) == KeraLua.LuaType.Nil ? null : raw.ToString(-1, false);
			return (ok, err);
		}
		finally
		{
			raw.SetTop(top);
		}
	}

	private static object EnvValue(TestRuntime runtime, int handle, string name)
	{
		KeraLua.Lua raw = runtime.State.State;
		int top = raw.GetTop();
		try
		{
			raw.GetGlobal("__testGet");
			raw.PushInteger(handle);
			raw.PushString(name);
			if (raw.PCall(2, 1, 0) != KeraLua.LuaStatus.OK) return null;
			switch (raw.Type(-1))
			{
				case KeraLua.LuaType.Nil: return null;
				case KeraLua.LuaType.Boolean: return raw.ToBoolean(-1);
				case KeraLua.LuaType.Number: return raw.ToNumber(-1);
				case KeraLua.LuaType.String: return raw.ToString(-1, false);
				default: return "<" + raw.Type(-1) + ">";
			}
		}
		finally
		{
			raw.SetTop(top);
		}
	}

	private static string FirstMatch(List<string> messages, string needle)
	{
		for (int i = 0; i < messages.Count; i++)
		{
			if (messages[i].Contains(needle)) return messages[i];
		}
		return "(none)";
	}

	/// <summary>Runs a declaration chunk and reports "ok" or the Lua error, so a failed
	/// registration check says WHY instead of just "false".</summary>
	private static string DeclarationError(TestRuntime runtime, int handle, string chunk)
	{
		(bool ok, string err) = RunInEnv(runtime, handle, chunk);
		return ok ? "ok" : "error: " + err;
	}

	private static void TestSandboxHardening()
	{
		TestRuntime runtime = CreateRuntime();
		try
		{
			RunInEnv(runtime, 7, "r1 = getmetatable(_G)");
			RunInEnv(runtime, 7, "r2 = getmetatable('')");
			RunInEnv(runtime, 7, "r3 = getmetatable(math)");
			RunInEnv(runtime, 7, "r4 = getmetatable(net)");
			Check("sandbox seals env/string/math/net metatables",
				EnvValue(runtime, 7, "r1") is bool b1 && !b1
				&& EnvValue(runtime, 7, "r2") is bool b2 && !b2
				&& EnvValue(runtime, 7, "r3") is bool b3 && !b3
				&& EnvValue(runtime, 7, "r4") is bool b4 && !b4);

			(bool ok, string err) = RunInEnv(runtime, 7, "math.floor = nil");
			Check("read-only library write rejected (" + err + ")", !ok && err != null && err.Contains("read-only"));

			RunInEnv(runtime, 8, "r5 = math.floor(1.5)");
			Check("other entity's library unaffected (rawset-removal test)",
				Convert.ToDouble(EnvValue(runtime, 8, "r5")) == 1.0);

			RunInEnv(runtime, 7, "r6 = rawset == nil");
			RunInEnv(runtime, 7, "r7 = rawget(math, 'floor') == nil");
			Check("rawset invisible; proxies hold no real keys",
				EnvValue(runtime, 7, "r6") is bool b6 && b6 && EnvValue(runtime, 7, "r7") is bool b7 && b7);

			RunInEnv(runtime, 7,
				"r8 = (coroutine == nil) and (debug == nil) and (load == nil) and (io == nil) and (os == nil) " +
				"and (require == nil) and (collectgarbage == nil) and (print == nil) and (luanet == nil)");
			Check("denied globals are invisible", EnvValue(runtime, 7, "r8") is bool b8 && b8);

			(ok, err) = RunInEnv(runtime, 7, "setmetatable({}, { __gc = function() end })");
			Check("__gc metatable rejected (" + err + ")", !ok && err != null && err.Contains("__gc"));

			(ok, err) = RunInEnv(runtime, 7, "string.rep('x', 2^21)");
			Check("string.rep capped via library access (" + err + ")", !ok && err != null && err.Contains("too large"));
			(ok, err) = RunInEnv(runtime, 7, "local s = ('x'):rep(2^21)");
			Check("string.rep capped via method syntax (" + err + ")", !ok && err != null && err.Contains("too large"));
			RunInEnv(runtime, 7, "r9 = ('x'):rep(3)");
			Check("small string.rep still works", Convert.ToString(EnvValue(runtime, 7, "r9")) == "xxx");

			RunInEnv(runtime, 9, "shared = 'only-nine'");
			RunInEnv(runtime, 7, "r10 = (shared == nil)");
			Check("env globals do not leak between entities", EnvValue(runtime, 7, "r10") is bool b10 && b10);

			RunInEnv(runtime, 7, "_G.viaG = 5  r11 = (viaG == 5)  r12 = this.Handle == 7");
			Check("_G aliases the env; this.Handle readable",
				EnvValue(runtime, 7, "r11") is bool b11 && b11 && EnvValue(runtime, 7, "r12") is bool b12 && b12);
			RunInEnv(runtime, 8, "r13 = (_G.viaG == nil)");
			Check("_G writes stay in the writing env", EnvValue(runtime, 8, "r13") is bool b13 && b13);

			(ok, err) = RunInEnv(runtime, 7, "this.Handle = 99");
			Check("this is read-only (" + err + ")", !ok && err != null && err.Contains("read-only"));

			(ok, err) = RunInEnv(runtime, 7, "net.sender = 5");
			Check("net table is read-only (" + err + ")", !ok && err != null && err.Contains("read-only"));
		}
		catch (Exception ex)
		{
			Check("sandbox suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			DisposeRuntime(runtime);
		}
	}

	private static void TestWatchdog()
	{
		TestRuntime runtime = CreateRuntime();
		try
		{
			runtime.Bridge.SetEntityScript(5, "Spin.lua");
			Check("handler declarations accepted (" + DeclarationError(runtime, 5,
				"net.server.Ok = function() result = 'ok' end " +
				"net.server.Spin = function() while true do end end " +
				"net.server.Shield = function() while true do pcall(function() while true do end end) end end") + ")",
				runtime.Router.HasHandler(5, NetDirection.Server, "Spin"));

			// 1st trip: plain infinite loop inside a real dispatch.
			RunInEnv(runtime, 5, "net.server.Spin()");
			runtime.Router.Flush();
			Check("infinite loop trips the watchdog (warnings: " + string.Join(" | ", runtime.Warnings) + ")",
				runtime.Warnings.Exists(w => w.Contains("script execution limit exceeded")));

			RunInEnv(runtime, 5, "net.server.Ok()");
			runtime.Router.Flush();
			Check("VM usable again after a trip", Convert.ToString(EnvValue(runtime, 5, "result")) == "ok");

			// 2nd trip: the amendment-1 self-test — pcall cannot swallow a trip.
			RunInEnv(runtime, 5, "net.server.Shield()");
			runtime.Router.Flush();
			Check("pcall cannot swallow a trip (warnings: " + string.Join(" | ", runtime.Warnings) + ")",
				runtime.Warnings.Exists(w => w.Contains("script execution limit exceeded")));

			// Budget wiring (named consts reach the Lua side).
			runtime.Watchdog.ArmUnit(LuaWatchdog.DispatchInstructionBudget);
			Check("dispatch budget const wired (" + runtime.Watchdog.ArmedBudget() + ")",
				runtime.Watchdog.ArmedBudget() == LuaWatchdog.DispatchInstructionBudget);
			Check("hook armed while a unit runs", runtime.Watchdog.IsArmed());
			runtime.Watchdog.EndUnit();
			Check("hook cleared and disarmed after the unit", !runtime.Watchdog.IsArmed());
			runtime.Watchdog.ArmUnit(LuaWatchdog.ScriptInstructionBudget);
			Check("script budget const wired",
				runtime.Watchdog.ArmedBudget() == LuaWatchdog.ScriptInstructionBudget);
			runtime.Watchdog.EndUnit();

			// 3rd trip: frame budget spanning dispatches (small value injected for the test).
			LuaCall.CallVoidInt(runtime.State.State, "__openlot_beginFrame", 20000);
			RunInEnv(runtime, 5, "net.server.Spin()");
			runtime.Router.Flush();
			Check("frame budget trips across dispatches",
				runtime.Warnings.Exists(w => w.Contains("frame execution limit exceeded")));

			// Circuit breaker: 3 trips on entity 5 -> its net handlers are disabled for good.
			Check("entity disabled after " + LuaWatchdog.MaxTripsPerEntity + " trips",
				runtime.Bridge.IsEntityDisabled(5));
			Check("disable warning names the entity and its script",
				runtime.Warnings.Exists(w => w.Contains("entity 5") && w.Contains("Spin.lua") && w.Contains("disabled")));

			RunInEnv(runtime, 5, "result = 'untouched'  net.server.Ok()");
			runtime.Router.Flush();
			Check("disabled entity's handlers no longer run",
				Convert.ToString(EnvValue(runtime, 5, "result")) == "untouched");
		}
		catch (Exception ex)
		{
			Check("watchdog suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			DisposeRuntime(runtime);
		}
	}

	private static void TestNetDispatch()
	{
		TestRuntime runtime = CreateRuntime();
		try
		{
			// Vararg signature on purpose: select('#', ...) needs varargs, and the nil hole means
			// named parameters alone cannot tell "3 args with a hole" from "2 args".
			Check("declaration registered through the bound methods (" + DeclarationError(runtime, 3,
				"net.server.Give = function(amount, note, extra, ...) " +
				"argCount = select('#', amount, note, extra, ...) a1 = amount a2 = note a3 = extra " +
				"seenSender = net.sender seenFrom = net.fromPlayer end") + ")",
				runtime.Router.HasHandler(3, NetDirection.Server, "Give"));

			RunInEnv(runtime, 3, "outsideSender = net.sender  outsideFrom = net.fromPlayer");
			Check("sender/fromPlayer are nil/false outside a dispatch",
				EnvValue(runtime, 3, "outsideSender") == null
				&& EnvValue(runtime, 3, "outsideFrom") is bool outsideFrom && !outsideFrom);

			RunInEnv(runtime, 3, "net.server.Give(1, nil, 3)");
			Check("call is queued, not executed inline", runtime.Router.QueuedCount == 1);
			runtime.Router.Flush();
			Check("handler ran through router + bridge with the nil hole intact",
				Convert.ToDouble(EnvValue(runtime, 3, "argCount")) == 3
				&& Convert.ToDouble(EnvValue(runtime, 3, "a1")) == 1
				&& EnvValue(runtime, 3, "a2") == null
				&& Convert.ToDouble(EnvValue(runtime, 3, "a3")) == 3);
			Check("net.sender is the host peer and an autonomous call reports fromPlayer=false",
				Convert.ToDouble(EnvValue(runtime, 3, "seenSender")) == 1
				&& EnvValue(runtime, 3, "seenFrom") is bool seenFrom && !seenFrom);

			// Test-mode entry point: calls made inside the scope are player-originated.
			runtime.LotMessages.Clear();
			runtime.Bridge.SimulatePlayerInteraction(() => RunInEnv(runtime, 3, "net.server.Give(8)"));
			runtime.Router.Flush();
			Check("SimulatePlayerInteraction marks calls as player-originated (fromPlayer=" +
				EnvValue(runtime, 3, "seenFrom") + ")",
				Convert.ToDouble(EnvValue(runtime, 3, "a1")) == 8
				&& EnvValue(runtime, 3, "seenFrom") is bool simPlayer && simPlayer);


			// A player-originated call (explicit interaction scope) reports fromPlayer=true.
			runtime.Router.BeginPlayerInteraction();
			RunInEnv(runtime, 3, "net.server.Give(7)");
			runtime.Router.EndPlayerInteraction();
			runtime.Router.Flush();
			Check("a call inside a player-interaction scope reports fromPlayer=true",
				Convert.ToDouble(EnvValue(runtime, 3, "a1")) == 7
				&& EnvValue(runtime, 3, "seenFrom") is bool seenPlayer && seenPlayer);

			// Another entity calling the same name routes to ITS OWN registry: no handler there.
			RunInEnv(runtime, 4, "net.server.Give()");
			runtime.Router.Flush();
			Check("cross-entity call has no handler on the target entity",
				runtime.Warnings.Exists(w => w.Contains("no handler for net.server.Give on entity 4")));

			(bool ok, string err) = RunInEnv(runtime, 3, "net.server.Give(function() end)");
			Check("function argument rejected at the call site (" + err + ")",
				!ok && err != null && err.Contains("cannot send function"));

			(ok, err) = RunInEnv(runtime, 3, "local t = {} t.self = t  net.server.Give(t)");
			Check("cyclic table rejected at the call site (" + err + ")",
				!ok && err != null && err.Contains("cyclic"));

			(ok, err) = RunInEnv(runtime, 3, "net.server.Give(0/0)");
			Check("NaN rejected at the call site (" + err + ")", !ok && err != null && err.Contains("NaN"));

			(ok, err) = RunInEnv(runtime, 3, "net.server.Give(setmetatable({}, { x = 1 }))");
			Check("metatable table rejected at the call site (" + err + ")",
				!ok && err != null && err.Contains("metatable"));

			Check("rejected calls never reached the queue", runtime.Router.QueuedCount == 0);
		}
		catch (Exception ex)
		{
			Check("net dispatch suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			DisposeRuntime(runtime);
		}
	}

	/// <summary>Mirrors BuilderScene's LotScriptNode placeholders: file path + display name.</summary>
	private static LotScriptNode MakeScript(string fileName)
	{
		LotScriptNode node = new LotScriptNode();
		node.Name = fileName.Replace('.', '_');
		node.DisplayName = fileName;
		node.ScriptPath = "user://Scripts/" + fileName;
		return node;
	}

	private static void TestScriptRuntime()
	{
		TestRuntime runtime = CreateRuntime();
		Node3D lotRoot = new Node3D();
		try
		{
			Dictionary<string, string> files = new Dictionary<string, string>();
			files["user://Scripts/Root.lua"] =
				"Lot.Log('rootRan:' .. tostring(this.Handle)) " +
				"net.server.RootPing = function() Lot.Log('rootPing:sender=' .. tostring(net.sender)) end";
			files["user://Scripts/Door.lua"] =
				"Lot.Log('doorRan')  function start() Lot.Log('doorStart:' .. tostring(this.Handle)) end " +
				"net.server.Open = function() Lot.Log('doorOpen:sender=' .. tostring(net.sender)) end";
			files["user://Scripts/Broken.lua"] = "this is not lua";
			files["user://Scripts/Loop.lua"] = "while true do end";
			files["user://Scripts/GateA.lua"] = "net.server.Toggle = function() gateA = true end";
			files["user://Scripts/GateB.lua"] = "net.server.Toggle = function() gateB = true end";
			files["user://Scripts/Spin.lua"] = "net.server.Spin = function() while true do end end";

			// Stand-in for BuilderScene.EnsureEntityHandle: stable per node, assigned in walk order.
			Dictionary<Node, int> handles = new Dictionary<Node, int>();
			int nextHandle = 1;
			Func<Node, int> ensureHandle = node =>
			{
				int handle;
				if (!handles.TryGetValue(node, out handle))
				{
					handle = nextHandle++;
					handles[node] = handle;
				}
				return handle;
			};
			Func<string, string> readScript = path =>
			{
				string code;
				return files.TryGetValue(path, out code) ? code : null;
			};

			ScriptRuntime scripts = new ScriptRuntime(() => runtime.Bridge, runtime.Warnings.Add, () => { });

			lotRoot.AddChild(MakeScript("Root.lua"));
			Node3D door = new Node3D();
			door.Name = "Door";
			lotRoot.AddChild(door);
			door.AddChild(MakeScript("Door.lua"));
			Node3D broken = new Node3D();
			broken.Name = "Broken";
			lotRoot.AddChild(broken);
			broken.AddChild(MakeScript("Broken.lua"));
			Node3D missing = new Node3D();
			missing.Name = "Missing";
			lotRoot.AddChild(missing);
			missing.AddChild(MakeScript("NotThere.lua"));

			scripts.LoadLot(lotRoot, ensureHandle, readScript);

			Check("lot-root script runs at load with handle 0",
				runtime.LotMessages.Contains("rootRan:0"));
			Check("entity script runs and its start() fires with the entity handle",
				runtime.LotMessages.Contains("doorRan")
				&& runtime.LotMessages.Contains("doorStart:" + handles[door]));
			Check("first entity gets handle 1 in deterministic walk order (got " + handles[door] + ")",
				handles[door] == 1);
			Check("syntax error reported for the failing script",
				runtime.Warnings.Exists(w => w.Contains("Broken.lua") && w.Contains("failed")));
			Check("unreadable script file reported",
				runtime.Warnings.Exists(w => w.Contains("NotThere.lua") && w.Contains("could not be read")));

			// net dispatch through a script that was loaded by ScriptRuntime.
			runtime.LotMessages.Clear();
			runtime.Router.InvokeLocal(handles[door], NetDirection.Server, "Open", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("net handler from a loaded entity script runs with net.sender",
				runtime.LotMessages.Contains("doorOpen:sender=1"));

			runtime.LotMessages.Clear();
			runtime.Router.InvokeLocal(0, NetDirection.Server, "RootPing", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("lot-root entity (handle 0) accepts net calls",
				runtime.LotMessages.Contains("rootPing:sender=1"));

			// Two scripts on ONE entity: separate envs, same handle, and the redeclaration warning
			// must name both files (amendment 7 through the real load path).
			Node3D gate = new Node3D();
			gate.Name = "Gate";
			lotRoot.AddChild(gate);
			gate.AddChild(MakeScript("GateA.lua"));
			gate.AddChild(MakeScript("GateB.lua"));

			// A script that loops forever at load time: stopped by the script budget.
			Node3D looper = new Node3D();
			looper.Name = "Looper";
			lotRoot.AddChild(looper);
			looper.AddChild(MakeScript("Loop.lua"));

			// A script whose handler loops forever at dispatch time: trips the circuit breaker.
			Node3D spinner = new Node3D();
			spinner.Name = "Spinner";
			lotRoot.AddChild(spinner);
			spinner.AddChild(MakeScript("Spin.lua"));

			int warningsBefore = runtime.Warnings.Count;
			scripts.LoadLot(lotRoot, ensureHandle, readScript);

			Check("two scripts on one entity warn, naming both files",
				runtime.Warnings.Exists(w => w.Contains("GateA.lua") && w.Contains("GateB.lua")));
			Check("load-time infinite loop is stopped by the script budget",
				runtime.Warnings.Exists(w => w.Contains("Loop.lua") && w.Contains("script execution limit exceeded")));
			Check("load-time trip does not disable the entity's net handlers",
				!runtime.Bridge.IsEntityDisabled(handles[looper]));
			Check("VM still usable after a load-time trip",
				RunInEnv(runtime, 0, "afterLoadTrip = 1").ok
				&& Convert.ToDouble(EnvValue(runtime, 0, "afterLoadTrip")) == 1.0);
			Check("reload is silent for unchanged scripts (no new redeclaration noise)",
				runtime.Warnings.FindAll(w => w.Contains("Door.lua") && w.Contains("declared by both")).Count == 0);

			// Circuit breaker naming through the real path: 3 dispatch trips on the spinner entity.
			for (int i = 0; i < LuaWatchdog.MaxTripsPerEntity; i++)
			{
				runtime.Router.InvokeLocal(handles[spinner], NetDirection.Server, "Spin", NetRouter.EmptyArgsPayload);
				scripts.Tick();
			}
			Check("circuit breaker warning names the script file (Spin.lua)",
				runtime.Warnings.Exists(w => w.Contains("Spin.lua") && w.Contains("disabled")));

			int loadedBeforeStop = scripts.LoadedScriptCount;
			scripts.Stop();
			Check("Stop() drops the script bookkeeping (" + loadedBeforeStop + " -> " + scripts.LoadedScriptCount + ")",
				loadedBeforeStop > 0 && scripts.LoadedScriptCount == 0);
		}
		catch (Exception ex)
		{
			Check("script runtime suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			lotRoot.Free();
			DisposeRuntime(runtime);
		}
	}

	private static void TestClearRegistriesOnReload()
	{
		// Item 6: a (re)load clears every net registry, so an entity the circuit breaker disabled is
		// NOT still disabled after the lot reloads and re-declared handlers work again.
		TestRuntime runtime = CreateRuntime();
		Node3D lotRoot = new Node3D();
		try
		{
			Dictionary<string, string> files = new Dictionary<string, string>();
			files["user://Scripts/Spin.lua"] = "net.server.Boom = function() while true do end end";
			files["user://Scripts/Calm.lua"] = "net.server.Ping = function() end";
			Dictionary<Node, int> handles = new Dictionary<Node, int>();
			int nextHandle = 1;
			Func<Node, int> ensureHandle = node =>
			{
				int handle;
				if (!handles.TryGetValue(node, out handle)) { handle = nextHandle++; handles[node] = handle; }
				return handle;
			};
			Func<string, string> readScript = path =>
			{
				string code;
				return files.TryGetValue(path, out code) ? code : null;
			};

			ScriptRuntime scripts = new ScriptRuntime(() => runtime.Bridge, runtime.Warnings.Add, () => { });
			Node3D spinner = new Node3D();
			spinner.Name = "Spinner";
			lotRoot.AddChild(spinner);
			spinner.AddChild(MakeScript("Spin.lua"));
			Node3D calm = new Node3D();
			calm.Name = "Calm";
			lotRoot.AddChild(calm);
			calm.AddChild(MakeScript("Calm.lua"));

			scripts.LoadLot(lotRoot, ensureHandle, readScript);
			int spinnerHandle = ensureHandle(spinner);
			Check("precondition: handler declared at load",
				runtime.Router.HasHandler(spinnerHandle, NetDirection.Server, "Boom"));

			// Trip the circuit breaker on the spinner entity.
			for (int i = 0; i < LuaWatchdog.MaxTripsPerEntity; i++)
			{
				runtime.Router.InvokeLocal(spinnerHandle, NetDirection.Server, "Boom", NetRouter.EmptyArgsPayload);
				scripts.Tick();
			}
			Check("precondition: entity disabled by the circuit breaker",
				runtime.Bridge.IsEntityDisabled(spinnerHandle));

			// Reload: registries must clear and the scripts re-establish in the SAME VM.
			scripts.ReloadScripts();
			Check("reload clears the circuit-breaker disable", !runtime.Bridge.IsEntityDisabled(spinnerHandle));
			Check("reload re-establishes declarations",
				runtime.Router.HasHandler(spinnerHandle, NetDirection.Server, "Boom")
				&& runtime.Router.HasHandler(ensureHandle(calm), NetDirection.Server, "Ping"));
		}
		catch (Exception ex)
		{
			Check("clear-registries suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			lotRoot.Free();
			DisposeRuntime(runtime);
		}
	}

	private static void TestCircuitBreakerScriptAttribution()
	{
		// Item 6: with two scripts on one entity, a watchdog trip must be blamed on the script that
		// DECLARED the tripping handler, not the last-loaded script (the pre-fix behaviour named the
		// last one because SetEntityScript overwrites per handle).
		TestRuntime runtime = CreateRuntime();
		Node3D lotRoot = new Node3D();
		try
		{
			Dictionary<string, string> files = new Dictionary<string, string>();
			files["user://Scripts/TwinA.lua"] = "net.server.SpinA = function() while true do end end";
			files["user://Scripts/TwinB.lua"] = "net.server.Safe = function() end";
			Dictionary<Node, int> handles = new Dictionary<Node, int>();
			int nextHandle = 1;
			Func<Node, int> ensureHandle = node =>
			{
				int handle;
				if (!handles.TryGetValue(node, out handle)) { handle = nextHandle++; handles[node] = handle; }
				return handle;
			};
			Func<string, string> readScript = path =>
			{
				string code;
				return files.TryGetValue(path, out code) ? code : null;
			};

			ScriptRuntime scripts = new ScriptRuntime(() => runtime.Bridge, runtime.Warnings.Add, () => { });
			Node3D twin = new Node3D();
			twin.Name = "Twin";
			lotRoot.AddChild(twin);
			twin.AddChild(MakeScript("TwinA.lua")); // loaded first -> declares SpinA
			twin.AddChild(MakeScript("TwinB.lua")); // loaded last  -> blamed before the fix

			scripts.LoadLot(lotRoot, ensureHandle, readScript);
			int twinHandle = ensureHandle(twin);

			string declaring;
			Check("trip is attributed to the declaring script, not the last-loaded one",
				runtime.Router.TryGetHandlerScript(twinHandle, NetDirection.Server, "SpinA", out declaring)
				&& declaring == "TwinA.lua");

			for (int i = 0; i < LuaWatchdog.MaxTripsPerEntity; i++)
			{
				runtime.Router.InvokeLocal(twinHandle, NetDirection.Server, "SpinA", NetRouter.EmptyArgsPayload);
				scripts.Tick();
			}
			Check("two-script entity is disabled after the trips", runtime.Bridge.IsEntityDisabled(twinHandle));

			string disableWarning = runtime.Warnings.Find(w => w.Contains("disabled") && w.Contains("entity " + twinHandle));
			Check("disable warning names TwinA.lua and not TwinB.lua (" + (disableWarning ?? "(none)") + ")",
				disableWarning != null && disableWarning.Contains("TwinA.lua") && !disableWarning.Contains("TwinB.lua"));
		}
		catch (Exception ex)
		{
			Check("script-attribution suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			lotRoot.Free();
			DisposeRuntime(runtime);
		}
	}

	private static void TestCrossEntityCalls()
	{
		// The milestone's acceptance shape: a button script triggers a door script through
		// Lot.CallServer — no manual remote events, no client/server branching in creator code.
		TestRuntime runtime = CreateRuntime();
		Node3D lotRoot = new Node3D();
		try
		{
			Dictionary<string, string> files = new Dictionary<string, string>();
			files["user://Scripts/Door.lua"] =
				"net.server.Open = function(amount, note, extra, ...) " +
				"Lot.Log('doorOpen:sender=' .. tostring(net.sender) .. ':count=' .. tostring(select('#', amount, note, extra, ...)) .. " +
				"':extra=' .. tostring(extra) .. ':hole=' .. tostring(note == nil) .. ':from=' .. tostring(net.fromPlayer)) end " +
				"net.client.Creak = function() Lot.Log('doorCreak') end";
			files["user://Scripts/Button.lua"] =
				"net.server.Press = function() Lot.CallServer(1, 'Open', 1, nil, 3) end " +
				"net.server.Rattle = function() Lot.CallClient(1, 'Creak') end " +
				"net.server.BadArg = function() Lot.CallServer(1, 'Open', function() end) end " +
				"net.server.NoTarget = function() Lot.CallServer(1, 'Nope') end";

			Dictionary<Node, int> handles = new Dictionary<Node, int>();
			int nextHandle = 1;
			Func<Node, int> ensureHandle = node =>
			{
				int handle;
				if (!handles.TryGetValue(node, out handle))
				{
					handle = nextHandle++;
					handles[node] = handle;
				}
				return handle;
			};
			Func<string, string> readScript = path =>
			{
				string code;
				return files.TryGetValue(path, out code) ? code : null;
			};
			// A root-entity script whose start() calls another entity: the load-time (autonomous) path
			// must be System, i.e. net.fromPlayer == false in the callee.
			files["user://Scripts/Caller.lua"] = "function start() Lot.CallServer(1, 'Open', 99) end";

			ScriptRuntime scripts = new ScriptRuntime(() => runtime.Bridge, runtime.Warnings.Add, () => { });

			lotRoot.AddChild(MakeScript("Caller.lua"));
			Node3D door = new Node3D();
			door.Name = "Door";
			lotRoot.AddChild(door);
			door.AddChild(MakeScript("Door.lua"));
			Node3D button = new Node3D();
			button.Name = "Button";
			lotRoot.AddChild(button);
			button.AddChild(MakeScript("Button.lua"));

			scripts.LoadLot(lotRoot, ensureHandle, readScript);
			Check("walk order gives the door handle 1 and the button handle 2 (got " +
				handles[door] + ", " + handles[button] + ")",
				handles[door] == 1 && handles[button] == 2);

			// Button -> door, with the nil hole intact and the identity carried transitively.
			runtime.LotMessages.Clear();
			runtime.Router.InvokeLocal(handles[button], NetDirection.Server, "Press", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("Lot.CallServer reaches the target entity's handler (" +
				string.Join(" | ", runtime.LotMessages) + ")",
				runtime.LotMessages.Exists(m => m.Contains("doorOpen")
					&& m.Contains(":count=3") && m.Contains(":extra=3") && m.Contains(":hole=true")));
			Check("cross-entity call keeps the original caller as net.sender",
				runtime.LotMessages.Exists(m => m.Contains("doorOpen:sender=1")));

			// Load-time path (start() calling another entity) is autonomous => System.
			Check("a call made from start() is autonomous (fromPlayer=false): " +
				FirstMatch(runtime.LotMessages, ":from="),
				runtime.LotMessages.Exists(m => m.Contains(":from=false")));

			// Host-side CallClient: broadcast + host-local dispatch of the target's client handler.
			runtime.LotMessages.Clear();
			runtime.Router.InvokeLocal(handles[button], NetDirection.Server, "Rattle", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("Lot.CallClient runs the target entity's net.client handler",
				runtime.LotMessages.Contains("doorCreak"));

			// The validation pre-pass still guards cross-entity arguments at the call site.
			runtime.LotMessages.Clear();
			runtime.Router.InvokeLocal(handles[button], NetDirection.Server, "BadArg", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("rejected cross-entity argument reported, nothing dispatched",
				runtime.Warnings.Exists(w => w.Contains("BadArg") && w.Contains("cannot send function"))
				&& runtime.LotMessages.Count == 0);

			runtime.Router.InvokeLocal(handles[button], NetDirection.Server, "NoTarget", NetRouter.EmptyArgsPayload);
			scripts.Tick();
			Check("cross-entity call to an unknown target name warns and drops",
				runtime.Warnings.Exists(w => w.Contains("no handler for net.server.Nope on entity 1")));

			// Creator surface: the sugar is visible; the raw bound globals are not. (RunInEnv uses
			// the test helper's env for the same handle — same sandbox whitelist either way.)
			RunInEnv(runtime, handles[button], "apiVisible = type(Lot.CallServer) == 'function' and type(Lot.CallClient) == 'function'");
			Check("sandbox exposes Lot.CallServer and Lot.CallClient",
				EnvValue(runtime, handles[button], "apiVisible") is bool apiVisible && apiVisible);
			(bool ok, string _) = RunInEnv(runtime, handles[button],
				"if CallServer ~= nil or CallClient ~= nil then error('raw globals are visible') end");
			Check("raw CallServer/CallClient globals stay invisible to scripts", ok);
			(ok, string badHandleErr) = RunInEnv(runtime, handles[button], "Lot.CallServer('not a handle', 'Open')");
			Check("non-numeric handle rejected at the call site (" + badHandleErr + ")",
				!ok && badHandleErr != null && badHandleErr.Contains("handle must be a number"));
		}
		catch (Exception ex)
		{
			Check("cross-entity suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			lotRoot.Free();
			DisposeRuntime(runtime);
		}
	}

	private static void TestRebuildNullBridgeWindow()
	{
		// ScriptRuntime must tolerate the window between LuaManager.Shutdown and Initialize, where
		// the resolver returns null, and afterwards treat a different bridge as a rebuild (reload).
		TestRuntime first = CreateRuntime();
		TestRuntime second = CreateRuntime(); // stands in for the rebuilt VM
		Node3D lotRoot = new Node3D();
		try
		{
			Dictionary<string, string> files = new Dictionary<string, string>();
			files["user://Scripts/NullDoor.lua"] = "Lot.Log('nullDoorLoaded')";
			Func<string, string> readScript = path =>
			{
				string code;
				return files.TryGetValue(path, out code) ? code : null;
			};
			Node3D door = new Node3D();
			door.Name = "Door";
			lotRoot.AddChild(door);
			door.AddChild(MakeScript("NullDoor.lua"));

			LuaNetBridge current = first.Bridge;
			ScriptRuntime scripts = new ScriptRuntime(() => current, first.Warnings.Add, () => { }, () => false);
			scripts.LoadLot(lotRoot, node => 5, readScript);
			Check("null-bridge window: initial load runs", first.LotMessages.Contains("nullDoorLoaded"));

			current = null; // the Shutdown..Initialize window
			bool threw = false;
			try { scripts.Tick(); } catch (Exception) { threw = true; }
			Check("null-bridge window: Tick tolerates a null bridge (no throw)", !threw);
			int warningsBefore = first.Warnings.Count;
			scripts.Tick();
			Check("null-bridge window: no reload attempted while there is no VM",
				first.Warnings.Count == warningsBefore);

			second.LotMessages.Clear();
			current = second.Bridge; // the rebuilt VM
			scripts.Tick();
			Check("null -> new bridge is treated as a rebuild and reloads into the new VM",
				second.LotMessages.Contains("nullDoorLoaded"));
			Check("rebuild during the window is reported", first.Warnings.Exists(w => w.Contains("VM was rebuilt")));
		}
		finally
		{
			lotRoot.Free();
			DisposeRuntime(first);
			DisposeRuntime(second);
		}
	}

	private static void TestScriptFileAccess()
	{
		// The real (non-injected) halves of step 4: the on-disk script read path and the fact that
		// the live builder scene stood up its script runtime. Uses a temp file it deletes again.
		const string relative = "__netselftest_tmp.lua";
		const string path = "user://Scripts/" + relative;
		if (!Godot.DirAccess.DirExistsAbsolute("user://Scripts"))
			Godot.DirAccess.MakeDirRecursiveAbsolute("user://Scripts");

		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			Check("script file write for the read-back test", false);
		}
		else
		{
			file.StoreString("-- netselftest temp\n");
			file.Dispose();
			string code = BuilderScene.ReadScriptFile(path);
			Check("BuilderScene.ReadScriptFile round-trips a real user:// file",
				code != null && code.Contains("netselftest temp"));
			Godot.DirAccess dir = Godot.DirAccess.Open("user://Scripts");
			if (dir != null) dir.Remove(relative);
		}
		Check("BuilderScene.ReadScriptFile returns null for a missing file",
			BuilderScene.ReadScriptFile("user://Scripts/__definitely_missing.lua") == null);

		// Reached through the scene tree rather than Builder.Instance: Godot runs a child's _Ready
		// BEFORE the parent's, and these tests execute inside BuilderScene._Ready, so
		// Builder.Instance is still null here by design. Walking the tree verifies the live wiring.
		BuilderScene liveScene = FindBuilderScene(((SceneTree)Engine.GetMainLoop()).Root);
		Check("live builder scene has an active script runtime",
			liveScene != null && liveScene.LuaScripts != null);
		Check("live lot root is registered at the reserved handle 0",
			liveScene != null && liveScene.GetByHandle(ScriptRuntime.LotRootHandle) == liveScene.LotRoot);

		// Handle coverage (step-5 option A): every loaded lot node has a handle, assigned in
		// deterministic order — so the default Ground entity is handle 1.
		Node ground = null;
		if (liveScene != null && liveScene.LotRoot != null)
		{
			for (int i = 0; i < liveScene.LotRoot.GetChildCount(); i++)
			{
				Node child = liveScene.LotRoot.GetChild(i);
				if (child.Name.ToString() == "Ground") { ground = child; break; }
			}
		}
		Check("handle coverage: the default ground entity holds handle 1",
			ground != null && liveScene.GetByHandle(1) == ground);
		int covered = 0;
		if (liveScene != null)
		{
			for (int handle = 1; liveScene.GetByHandle(handle) != null; handle++) covered++;
		}
		Check("handle coverage: handles are contiguous from 1 (" + covered + " entities)", covered >= 1);
	}

	private static BuilderScene FindBuilderScene(Node node)
	{
		if (node == null) return null;
		BuilderScene scene = node as BuilderScene;
		if (scene != null) return scene;
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			BuilderScene found = FindBuilderScene(node.GetChild(i));
			if (found != null) return found;
		}
		return null;
	}

	private static void TestGetHandleByName()
	{
		// Item 5: Lot.GetHandle(name) resolves an entity name to its handle. Contract (rule A):
		// the FIRST entity with that name in deterministic lot order (lowest handle), entities
		// only, silent on duplicates, -1 when nothing matches. Runs against the live builder scene
		// like TestScriptFileAccess does (scene-registry reads only — no Lua runs here, so the fast
		// set stays off the live lot VM; the "exposed to creator scripts" check runs in the
		// integration suite, which owns the live VM).
		BuilderScene liveScene = FindBuilderScene(((SceneTree)Engine.GetMainLoop()).Root);
		Check("GetHandle: live scene reachable", liveScene != null);
		if (liveScene == null) return;

		int ground = liveScene.FindHandleByName("Ground");
		Check("GetHandle('Ground') resolves to the ground entity (handle " + ground + ")",
			ground >= 0 && liveScene.GetByHandle(ground) != null
			&& liveScene.GetByHandle(ground).Name.ToString() == "Ground");
		Check("GetHandle/GetName round-trip on the ground entity",
			liveScene.GetByHandle(ground) != null && liveScene.GetByHandle(ground).Name.ToString() == "Ground");
		Check("GetHandle('__no_such_entity__') is -1", liveScene.FindHandleByName("__no_such_entity__") == -1);
		Check("GetHandle('') is -1", liveScene.FindHandleByName("") == -1);
		Check("GetHandle(null) is -1", liveScene.FindHandleByName(null) == -1);

		// First-match: the same name under two different parents is legal (names are unique only
		// among siblings), and the earlier one in lot order — the lower handle — must win.
		Node3D first = new Node3D();
		first.Name = "__netselftest_dup";
		liveScene.LotRoot.AddChild(first);
		int firstHandle = liveScene.RegisterHandle(first);
		Node3D group = new Node3D();
		group.Name = "__netselftest_dup_group";
		liveScene.LotRoot.AddChild(group);
		Node3D second = new Node3D();
		second.Name = "__netselftest_dup";
		group.AddChild(second);
		int secondHandle = liveScene.RegisterHandle(second);

		int resolved = liveScene.FindHandleByName("__netselftest_dup");
		Check("GetHandle is first-match / lowest-handle (got " + resolved + ", want " + firstHandle +
			"; first<" + "second)",
			resolved == firstHandle && firstHandle < secondHandle);

		liveScene.ForgetHandle(firstHandle);
		liveScene.ForgetHandle(secondHandle);
		first.Free();
		group.Free();
	}

	private static void TestHeavyEncodeLoop()
	{
		// Heavy (opt-in): 20k encodes on a private VM. Stresses per-encode wrapper disposal and the
		// incremental budget checks; a leak would surface as runaway memory, a regression as a
		// wrong-sized payload. Size-stability is the cheap correctness signal.
		const int iterations = 20000;
		Lua lua = new Lua();
		try
		{
			LuaTable packed = (LuaTable)lua.DoString(
				"return { n = 3, 1, 'two', { inner = { 4, 5, 6 }, flag = true } }", "heavyencode")[0];
			byte[] reference = NetSerializer.EncodeArgs(packed, 3);
			bool stable = reference.Length > 0;
			for (int i = 0; i < iterations && stable; i++)
			{
				byte[] payload = NetSerializer.EncodeArgs(packed, 3);
				if (payload.Length != reference.Length) stable = false;
			}
			Check("heavy: " + iterations + " encodes valid and size-stable (" + reference.Length + " bytes)", stable);
			Check("heavy: encoded payload decodes back",
				NetSerializer.DecodeArgs(lua.State, reference, 0) == reference.Length);
			packed.Dispose();
		}
		catch (Exception ex)
		{
			Check("heavy encode loop ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			lua.Dispose();
		}
	}

	private static void TestHeavyAllocations()
	{
		// Heavy (opt-in): ~10 MB of live Lua data under the counting allocator (default 64 MB cap).
		// Proves the allocator tracks a multi-MB working set and the VM survives it without OOM.
		TestRuntime runtime = CreateRuntime();
		try
		{
			runtime.Guard.Install(runtime.State.State, LuaMemoryGuard.MemoryCapBytes);
			long before = runtime.Guard.UsedBytes;
			(bool ok, string err) = RunInEnv(runtime, 1,
				"local parts = {} for i = 1, 256 do parts[i] = string.rep('x', 40000) end held = parts");
			long grown = runtime.Guard.UsedBytes - before;
			Check("heavy: ~10 MB allocated and counted (grew " + grown + " bytes, ok=" + ok + ")",
				ok && grown >= 10000000 && !runtime.Guard.OomHit);
			Check("heavy: VM still runs after 10 MB",
				RunInEnv(runtime, 1, "after = held[256] ~= nil").ok
				&& EnvValue(runtime, 1, "after") is bool after && after);
		}
		catch (Exception ex)
		{
			Check("heavy allocation suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			DisposeRuntime(runtime);
		}
	}

	private static void TestAllocatorCap()
	{
		// Deliberately last in the heavy set: the OOM poisons its VM, so this runtime is built, used and discarded
		// on its own rather than reusing the shared test VM.
		TestRuntime runtime = CreateRuntime();
		try
		{
			runtime.Guard.Install(runtime.State.State, 8L * 1024 * 1024);
			RunInEnv(runtime, 1, @"capped = not pcall(function()
    local s = 'x'
    for i = 1, 40 do
        s = s .. s
    end
end)");
			Check("allocator caps exponential growth (capped=" + EnvValue(runtime, 1, "capped") + ")",
				EnvValue(runtime, 1, "capped") is bool capped && capped);
			Check("OomHit set even though the script pcall caught the error (used " + runtime.Guard.UsedBytes + " bytes)",
				runtime.Guard.OomHit);
			Check("OomHit consumed exactly once",
				runtime.Guard.ConsumeOomHit() && !runtime.Guard.OomHit);
		}
		catch (Exception ex)
		{
			Check("allocator suite ran (" + ex.GetType().Name + ": " + ex.Message + ")", false);
		}
		finally
		{
			DisposeRuntime(runtime);
		}

		// Process survival: a fresh VM builds and runs after the OOM (step 3 in-project re-check
		// of spike E; the LuaManager teardown/recreate path is the same shape).
		TestRuntime fresh = CreateRuntime();
		try
		{
			Check("fresh VM after OOM works",
				RunInEnv(fresh, 1, "ok = 1 + 1").ok && Convert.ToDouble(EnvValue(fresh, 1, "ok")) == 2.0);
		}
		catch (Exception ex)
		{
			Check("fresh VM after OOM works (" + ex.Message + ")", false);
		}
		finally
		{
			DisposeRuntime(fresh);
		}
	}
}
