using Godot;

/// <summary>
/// Lua code editor window, ported from Unity's LuaCodeEditor + CodeEditorWindow and rebuilt on
/// ImGui's InputTextMultiline, so cursor, selection, undo and clipboard come for free instead of
/// the hand-rolled TMP editor. Saves on deactivate, window close, and a short debounce
/// (Tick from Builder._Process) instead of per keystroke.
/// Syntax highlighting is deferred: OpenLot plans an ImGui modification that can color text
/// ranges — GetDisplayCode() is the single hook that modification replaces, leaving the rest of
/// this file untouched.
/// </summary>
public class CodeEditorWindow
{
	private const int BufferCapacity = 262144;
	private const double SaveDebounceSeconds = 1.2;

	private readonly Builder _builder;

	private string _code = "";
	private string _activePath = "";
	private bool _dirtySinceSave;
	private double _idleSinceEdit;
	private bool _focusNextFrame;

	// Cached gutter content, rebuilt only when the line count changes.
	private int _cachedLineCount = -1;
	private string _cachedLineNumbers = "";
	private float _lastEditorScrollY;

	// Transient Run status shown in the header (set by the Run button).
	private string _runStatus = "";
	private double _runStatusUntil = -1.0;

	public bool IsOpen { get; private set; }

	public CodeEditorWindow(Builder builder)
	{
		_builder = builder;
		UpdateLineNumbers();
	}

	public void Show()
	{
		IsOpen = true;
	}

	public void Open(string path, string content)
	{
		FlushPendingSave();
		_activePath = path ?? "";
		_code = content ?? "";
		_dirtySinceSave = false;
		_idleSinceEdit = 0.0;
		UpdateLineNumbers();
		IsOpen = true;
		_focusNextFrame = true;
	}

	public void Close()
	{
		FlushPendingSave();
		IsOpen = false;
	}

	/// <summary>Called from Builder._Process — debounced save for active edits.</summary>
	public void Tick(double delta)
	{
		if (!_dirtySinceSave || _activePath.Length == 0) return;
		_idleSinceEdit += delta;
		if (_idleSinceEdit >= SaveDebounceSeconds)
			FlushPendingSave();
	}

	public void OnPathRenamed(string oldPath, string newPath)
	{
		if (_activePath == oldPath) _activePath = newPath;
	}

	/// <summary>One-line status for the last Run from this editor (error's first line, or OK).</summary>
	private string DescribeLastLuaError()
	{
		string error = LuaManager.Instance.LastError;
		if (error.Length == 0) return "ran Lua";
		int lineEnd = error.IndexOf('\n');
		string firstLine = lineEnd > 0 ? error.Substring(0, lineEnd) : error;
		return "[Lua error] " + firstLine;
	}

	public void FlushPendingSave()
	{
		if (!_dirtySinceSave || _activePath.Length == 0) return;
		_builder.Scripts.SaveScript(_activePath, _code);
		_dirtySinceSave = false;
	}

	public void Draw(Builder builder)
	{
		if (_focusNextFrame)
		{
			ImGui.SetNextWindowFocus();
			_focusNextFrame = false;
		}
		ImGui.SetNextWindowSize(720f, 480f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(320f, 120f, ImGui.CondFirstUseEver);

		bool open = ImGui.Begin("Code Editor (Lua)");
		if (!open)
		{
			ImGui.End();
			return;
		}

		if (ImGui.SmallButton("X"))
		{
			Close();
			ImGui.End();
			return;
		}
		ImGui.SameLine();
		if (ImGui.SmallButton("Run"))
		{
			// Runs the current buffer against the lot (milestone 3.1 acceptance path).
			LuaManager.Instance.RunString(_code, "LotBuilder_EditorRun");
			_runStatus = DescribeLastLuaError();
			_runStatusUntil = ImGui.GetTime() + 3.0;
		}
		ImGui.SameLine();
		string title = _activePath.Length > 0 ? System.IO.Path.GetFileName(_activePath) : "(unsaved buffer)";
		ImGui.Text(_dirtySinceSave ? title + "   *" : title);
		ImGui.SameLine();
		if (_runStatus.Length > 0 && ImGui.GetTime() < _runStatusUntil)
			ImGui.TextDisabled(_runStatus);
		ImGui.Separator();

		// Line-number gutter: scroll follows the code child with a one-frame lag (invisible on
		// static numbers); exact sync needs a scroll query that runs before the code child draws.
		bool gutterOpen = ImGui.BeginChild("##gutter", 46f, 0f);
		if (gutterOpen)
		{
			ImGui.Text(_cachedLineNumbers);
			ImGui.SetScrollY(_lastEditorScrollY);
			ImGui.EndChild();
		}
		ImGui.SameLine();

		bool hostOpen = ImGui.BeginChild("##codehost", 0f, 0f);
		bool committed = false;
		if (hostOpen)
		{
			Vector2 avail = ImGui.GetContentRegionAvail();
			string edited = ImGui.InputTextMultiline("##code", GetDisplayCode(), avail, BufferCapacity, ImGui.InputTextAllowTabInput);
			if (edited != _code)
			{
				_code = edited;
				_dirtySinceSave = true;
				_idleSinceEdit = 0.0;
				_builder.MarkDirty();
				UpdateLineNumbers();
			}
			// ImGui single/multi-line inputs clear their active id on Enter; after-edit
			// deactivation is the closest commit point the wrapper can reach.
			committed = ImGui.IsItemDeactivatedAfterEdit();
			_lastEditorScrollY = ImGui.GetScrollY();
			ImGui.EndChild();
		}

		if (committed) FlushPendingSave();
		ImGui.End();
	}

	/// <summary>Display-time transform of the editor text. Returns the buffer unchanged today;
	/// the planned colored-text ImGui modification (syntax highlighting) plugs in here without
	/// touching any other part of this file.</summary>
	private string GetDisplayCode()
	{
		return _code;
	}

	private void UpdateLineNumbers()
	{
		int count = 1;
		for (int i = 0; i < _code.Length; i++)
		{
			if (_code[i] == '\n') count++;
		}
		if (count == _cachedLineCount) return;

		_cachedLineCount = count;
		System.Text.StringBuilder sb = new System.Text.StringBuilder(count * 5);
		for (int i = 1; i <= count; i++)
		{
			sb.Append(i);
			sb.Append('\n');
		}
		_cachedLineNumbers = sb.ToString();
	}
}