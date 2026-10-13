using System.Collections.Generic;
using Godot;

/// <summary>
/// Lua code editor window, ported from Unity's LuaCodeEditor + CodeEditorWindow and rebuilt on
/// ImGui's InputTextMultiline, so cursor, selection, in-widget undo and in-app clipboard come for
/// free instead of the hand-rolled TMP editor. Saves on deactivate, window close, and a short
/// debounce (Tick from Builder._Process) instead of per keystroke.
///
/// The OS clipboard is bridged here, the way the terminal's console does it (the vendored ImGui
/// addon exposes no clipboard API at all — it only ever uses ImGui's *in-process* clipboard, which
/// never touches the OS, so nothing could be copied out of OpenLot or pasted into it):
/// <list type="bullet">
/// <item><b>Ctrl+V</b> reads the OS clipboard and types it into the focused field as synthesized
/// keystrokes, so it lands at the caret and rides ImGui's own undo (the console's Ctrl+V inserts at
/// its cursor the same way).</item>
/// <item><b>Ctrl+C / Ctrl+Shift+C</b> put the buffer on the OS clipboard — the console's
/// Ctrl+Shift+C "copy everything" binding, because the ImGui wrapper exposes no text-selection
/// query to read a partial selection from.</item>
/// <item><b>Ctrl+X</b> is the same copy followed by a cut.</item>
/// </list>
/// Builder._Input routes the three shortcuts (see there for why the press is visible at all).
///
/// Syntax highlighting is deferred: OpenLot plans an ImGui modification that can color text
/// ranges — GetDisplayCode() is the single hook that modification replaces, leaving the rest of
/// this file untouched.
/// </summary>
public class CodeEditorWindow : IEditorWindow
{
	private const int BufferCapacity = 262144;
	private const double SaveDebounceSeconds = 1.2;

	/// <summary>What a Ctrl/Meta keystroke asks the editor's clipboard bridge to do.</summary>
	public enum ClipboardAction
	{
		None,
		Paste,
		Copy,
		Cut
	}

	/// <summary>One synthesized keystroke: the key to press and release, plus the character it types
	/// (0 for a key with no character, such as Enter or Backspace).</summary>
	public struct Keystroke
	{
		public Key Key;
		public long Unicode;
	}

	private readonly Builder _builder;

	private string _code = "";
	private string _activePath = "";
	private bool _dirtySinceSave;
	private double _idleSinceEdit;
	private bool _focusNextFrame;

	/// <summary>
	/// Buffer contents captured when the code field gained focus; null when no edit session is open.
	/// One session (focus in → focus out) becomes one history entry (milestone 2.4), instead of one
	/// entry per keystroke.
	/// </summary>
	private string _pendingEditBefore = null;

	/// <summary>
	/// True while the code text field owns keyboard focus. Builder's shortcut handler reads it so
	/// Ctrl+Z stays ImGui's own in-widget undo while the field is active, and only drives the lot
	/// history when it is not.
	/// </summary>
	public bool HasCodeFocus { get; private set; }

	// Cached gutter content, rebuilt only when the line count changes.
	private int _cachedLineCount = -1;
	private string _cachedLineNumbers = "";
	private float _lastEditorScrollY;

	// Transient Run status shown in the header (set by the Run button).
	private string _runStatus = "";
	private double _runStatusUntil = -1.0;

	// Clipboard bridge (see the class summary): text waiting to be typed into the field, and the
	// "clear the field" half a cut needs beyond its copy. Both are consumed by Tick.
	private string _pendingPaste;
	private bool _pendingClearAll;

	public bool IsOpen { get; private set; }

	/// <summary>Add Window menu label (IEditorWindow).</summary>
	public string Title { get { return "Code Editor"; } }

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
		CommitScriptEdit();
		FlushPendingSave();
		_activePath = path ?? "";
		_code = content ?? "";
		_dirtySinceSave = false;
		_idleSinceEdit = 0.0;
		_pendingEditBefore = null;
		HasCodeFocus = false;
		UpdateLineNumbers();
		IsOpen = true;
		_focusNextFrame = true;
	}

	public void Close()
	{
		// Closing must not silently drop the last edit session: commit it as a history entry first.
		CommitScriptEdit();
		FlushPendingSave();
		IsOpen = false;
		HasCodeFocus = false;
	}

	/// <summary>Called from Builder._Process — debounced save for active edits, and the OS
	/// clipboard bridge's keystroke injection (which must not run inside the input callback that
	/// queued it).</summary>
	public void Tick(double delta)
	{
		InjectPendingClipboard();
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

	// --- OS clipboard bridge (see the class summary) ---------------------------------------------
	//
	// The vendored addon has no clipboard API: ImGui's Ctrl+C/Ctrl+V only ever touch ImGui's own
	// in-process clipboard, so nothing leaves or enters OpenLot. These four members are the bridge.
	// They are deliberately split into pure rules (mapping a key to an action, turning text into
	// keystrokes) and thin I/O, so the rules are pinned by EditorWorkflowSelfTest without a frame.

	/// <summary>
	/// The clipboard action a key press asks for, or <see cref="ClipboardAction.None"/>. Pure, so
	/// Builder's input handler and the self-test share one rule. Ctrl and Meta (Cmd) both count, and
	/// key echoes are ignored so a held key does not repeat the action. Ctrl+Shift+C resolves to the
	/// same copy as Ctrl+C — the console's "copy everything" binding, kept working here too.
	/// </summary>
	public static ClipboardAction ResolveClipboardAction(InputEventKey key)
	{
		if (key == null || !key.Pressed || key.Echo) return ClipboardAction.None;
		if (!key.CtrlPressed && !key.MetaPressed) return ClipboardAction.None;
		switch (key.Keycode)
		{
			case Key.V: return ClipboardAction.Paste;
			case Key.C: return ClipboardAction.Copy;
			case Key.X: return ClipboardAction.Cut;
			default: return ClipboardAction.None;
		}
	}

	/// <summary>
	/// Clipboard text as the field should receive it: CR is dropped and CRLF collapsed (ImGui wants
	/// plain newlines), and the length is capped at the field's capacity so an absurd paste cannot
	/// exceed the buffer. Pure.
	/// </summary>
	public static string NormalizeClipboardText(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";
		string clean = text.IndexOf('\r') >= 0 ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
		if (clean.Length > BufferCapacity) clean = clean.Substring(0, BufferCapacity);
		return clean;
	}

	/// <summary>
	/// The keystrokes that type <paramref name="text"/> into the field: a character key per
	/// printable character, and an Enter key per newline (ImGui filters control characters out of
	/// the character stream, so a newline has to arrive as its key). Pasted text is otherwise typed
	/// exactly as it reads. Pure.
	/// </summary>
	public static List<Keystroke> BuildPasteKeystrokes(string text)
	{
		List<Keystroke> steps = new List<Keystroke>();
		if (string.IsNullOrEmpty(text)) return steps;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '\n') steps.Add(new Keystroke { Key = Key.Enter, Unicode = 0 });
			else if (c >= 0x20) steps.Add(new Keystroke { Key = Key.None, Unicode = c });
		}
		return steps;
	}

	/// <summary>Queues the OS clipboard to be typed into the field. Nothing happens for an empty
	/// clipboard, so a stray Ctrl+V cannot disturb the buffer.</summary>
	public void PasteFromClipboard()
	{
		PasteText(DisplayServer.ClipboardGet());
	}

	/// <summary>
	/// Queues text to be typed into the field, as if it had come from the clipboard (the OS read
	/// above is a thin wrapper over this). Splitting the two is what lets the self-test drive the
	/// real injection path without depending on what a headless display server's clipboard holds.
	/// </summary>
	public void PasteText(string text)
	{
		string normalized = NormalizeClipboardText(text);
		if (normalized.Length == 0) return;
		_pendingPaste = normalized;
	}

	/// <summary>Puts the whole buffer on the OS clipboard (the wrapper exposes no selection, so
	/// copy is all-or-nothing — see the class summary).</summary>
	public void CopyAllToClipboard()
	{
		if (_code.Length == 0) return;
		DisplayServer.ClipboardSet(_code);
	}

	/// <summary>Copies the whole buffer to the OS clipboard and clears the field.</summary>
	public void CutAllToClipboard()
	{
		if (_code.Length == 0) return;
		CopyAllToClipboard();
		_pendingClearAll = true;
	}

	/// <summary>Types the queued paste (called from Tick, so the keystrokes are fed outside the
	/// input callback that queued them).</summary>
	private void InjectPendingClipboard()
	{
		if (_pendingClearAll)
		{
			_pendingClearAll = false;
			// Ctrl+A then Backspace removes everything as one ImGui edit (so it undoes in one step).
			SendKeystroke(Key.A, 0, true);
			SendKeystroke(Key.Backspace, 0, false);
			Input.FlushBufferedEvents();
		}
		if (_pendingPaste == null) return;
		string text = _pendingPaste;
		_pendingPaste = null;
		List<Keystroke> steps = BuildPasteKeystrokes(text);
		for (int i = 0; i < steps.Count; i++) SendKeystroke(steps[i].Key, steps[i].Unicode, false);
		// ParseInputEvent feeds the buffered input system, so the flush is what makes the typed
		// characters visible without waiting for the next input flush (the InputSelfTest spike).
		Input.FlushBufferedEvents();
	}

	/// <summary>Feeds one synthetic key press and its release to the input system, which the addon
	/// forwards into ImGui — the field is focused, so it consumes them as typing.</summary>
	private static void SendKeystroke(Key key, long unicode, bool ctrl)
	{
		InputEventKey down = new InputEventKey
		{
			Keycode = key,
			PhysicalKeycode = key,
			Pressed = true,
			CtrlPressed = ctrl,
			Unicode = unicode
		};
		Input.ParseInputEvent(down);
		InputEventKey up = new InputEventKey
		{
			Keycode = key,
			PhysicalKeycode = key,
			Pressed = false,
			CtrlPressed = ctrl,
			Unicode = 0
		};
		Input.ParseInputEvent(up);
	}

	/// <summary>
	/// Turns a finished edit session (focus in → focus out, or window close) into ONE history entry.
	/// Recorded only when the edited script's node is selected (milestone 2.4): otherwise the file
	/// is still saved by the debounce, but the lot history is not touched — the editor is just a
	/// buffer then. A no-op when nothing changed or no session was open.
	/// </summary>
	private void CommitScriptEdit()
	{
		string before = _pendingEditBefore;
		_pendingEditBefore = null;
		if (before == null || _activePath.Length == 0 || before == _code) return;
		if (!ShouldRecordScriptEdit(_builder, _activePath)) return;
		_builder.History.Push(new ScriptEditCommand(_activePath, before, _code));
	}

	/// <summary>
	/// Applies a script buffer by path (the apply half of <see cref="ScriptEditCommand"/>). Refuses
	/// to recreate a file that no longer exists, so undoing an edit to a deleted script is a no-op
	/// rather than resurrecting a deleted asset. When the editor is showing that path its buffer is
	/// replaced and any pending session is dropped — an undo/redo is not itself a new edit.
	/// </summary>
	public void ApplyCode(string path, string code)
	{
		if (string.IsNullOrEmpty(path)) return;
		if (!Godot.FileAccess.FileExists(path))
		{
			Godot.GD.PushWarning("[CodeEditor] script no longer exists; undo/redo skipped: " + path);
			LotLog.Warn("editor", "script no longer exists; undo/redo skipped: " + path);
			return;
		}

		_builder.Scripts.SaveScript(path, code ?? "");
		if (_activePath != path) return;

		_code = code ?? "";
		_dirtySinceSave = false;
		_idleSinceEdit = 0.0;
		_pendingEditBefore = null;
		UpdateLineNumbers();
	}

	/// <summary>
	/// The "only if the script is selected" rule for recording a code edit. Pure over the lot tree
	/// and the selection, so <see cref="EditorWorkflowSelfTest"/> can pin it without an ImGui frame.
	/// </summary>
	public static bool ShouldRecordScriptEdit(Builder builder, string path)
	{
		if (builder == null || string.IsNullOrEmpty(path) || builder.Scene == null || builder.Selection == null) return false;
		LotScriptNode node = FindScriptNode(builder.Scene.LotRoot, path);
		if (node == null) node = FindScriptNode(builder.Scene.LotUIRoot, path);
		return node != null && builder.Selection.IsSelected(node);
	}

	/// <summary>Depth-first search for the hierarchy placeholder whose ScriptPath matches.</summary>
	public static LotScriptNode FindScriptNode(Node root, string path)
	{
		if (root == null) return null;
		LotScriptNode script = root as LotScriptNode;
		if (script != null && script.ScriptPath == path) return script;
		int count = root.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			LotScriptNode found = FindScriptNode(root.GetChild(i), path);
			if (found != null) return found;
		}
		return null;
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

		bool open = ImGui.Begin("Code Editor (Lua)", EditorChrome.PanelWindowFlags);
		if (!open)
		{
			// The window is collapsed: nothing is drawing the field, so it cannot hold focus.
			HasCodeFocus = false;
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
		}
		// EndChild() is unconditional: BeginChild() returns false for a clipped/collapsed child
		// (skips the body, not the pairing ImGui requires).
		ImGui.EndChild();
		ImGui.SameLine();

		bool hostOpen = ImGui.BeginChild("##codehost", 0f, 0f);
		bool committed = false;
		if (hostOpen)
		{
			Vector2 avail = ImGui.GetContentRegionAvail();
			string edited = ImGui.InputTextMultiline("##code", GetDisplayCode(), avail, BufferCapacity, ImGui.InputTextAllowTabInput);
			// Focus in (or the first keystroke) opens an edit session: remember the buffer as it was
			// so the commit below can turn the whole session into one undo entry.
			if (ImGui.IsItemActivated() && _pendingEditBefore == null)
				_pendingEditBefore = _code;
			if (edited != _code)
			{
				_code = edited;
				_dirtySinceSave = true;
				_idleSinceEdit = 0.0;
				UpdateLineNumbers();
			}
			// Builder's Ctrl+Z arbitration reads this next frame (same one-frame pattern as the
			// gizmo/drag WantsMouse flags).
			HasCodeFocus = ImGui.IsItemActive();
			// ImGui single/multi-line inputs clear their active id on Enter; after-edit
			// deactivation is the closest commit point the wrapper can reach.
			committed = ImGui.IsItemDeactivatedAfterEdit();
			_lastEditorScrollY = ImGui.GetScrollY();
		}
		// EndChild() is unconditional: BeginChild() returns false for a clipped/collapsed child
		// (skips the body, not the pairing ImGui requires).
		ImGui.EndChild();

		if (committed)
		{
			FlushPendingSave();
			CommitScriptEdit();
		}
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