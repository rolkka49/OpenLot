using Godot;

/// <summary>
/// Verification for milestone 5.5 (keybinding customization): the defaults are exactly the keys that
/// were hardcoded before the milestone, a row holds SEVERAL keys (add/remove/replace, conflict checks
/// that also catch a duplicate within one row), the chord format/parse pair round-trips, a gameplay
/// action refuses modifiers, the <c>[keybindings]</c> block round-trips on a scratch file as a
/// comma-separated list (leaving the other blocks alone, and reading a pre-multi-key single value as
/// a one-entry list), the pure editor-command resolver follows the pre-5.5 rules for every key of a
/// row, and the capture state machine assigns a slot, appends at the row's count ("+"), waits on a
/// bare modifier and ends on a refused chord. Follows the project's self-test convention: PASS/FAIL
/// lines, returns the failure count. Runs from <c>BuilderScene._Ready</c> under <c>#if DEBUG</c>.
/// </summary>
public static class KeyBindingSelfTest
{
	private const string ScratchPath = "user://keybindings_selftest.cfg";

	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestDefaults();
		TestChordFormatting();
		TestParseRefusals();
		TestConflicts();
		TestPersistence();
		TestEditorResolution();
		TestCaptureMachine();
		TestInputMapMultiKey();

		GD.Print("[KeyBindingSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition) GD.Print("[KeyBindingSelfTest] PASS  " + name);
		else { _failures++; GD.PrintErr("[KeyBindingSelfTest] FAIL  " + name); }
	}

	private static InputEventKey Press(Key key, bool ctrl = false, bool shift = false, bool alt = false,
		bool pressed = true, bool echo = false, bool meta = false)
	{
		return new InputEventKey
		{
			Keycode = key, Pressed = pressed, Echo = echo,
			CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt, MetaPressed = meta,
		};
	}

	// --- defaults ---------------------------------------------------------------------------------

	private static void TestDefaults()
	{
		KeyBindings bindings = new KeyBindings();
		Check("defaults: 12 rows are rebindable (7 gameplay + 5 editor)", bindings.Rows.Count == 12);

		Check("defaults: movement is the pre-5.5 WASD",
			bindings.Find("action.moveForward").ChordAt(0).Key == Key.W
			&& bindings.Find("action.moveBackward").ChordAt(0).Key == Key.S
			&& bindings.Find("action.moveLeft").ChordAt(0).Key == Key.A
			&& bindings.Find("action.moveRight").ChordAt(0).Key == Key.D);
		Check("defaults: jump/sprint/interact are Space/Shift/E",
			bindings.Find("action.jump").ChordAt(0).Key == Key.Space
			&& bindings.Find("action.sprint").ChordAt(0).Key == Key.Shift
			&& bindings.Find("action.interact").ChordAt(0).Key == Key.E);
		Check("defaults: gameplay rows are modifierless",
			bindings.Find("action.moveForward").ChordAt(0).IsModifierless
			&& bindings.Find("action.jump").ChordAt(0).IsModifierless);

		Check("defaults: save/undo are Ctrl+S / Ctrl+Z",
			bindings.Find(KeyBindingIds.Save).ChordAt(0).Format().StartsWith("Ctrl+")
			&& bindings.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.S
			&& bindings.Find(KeyBindingIds.Undo).ChordAt(0).Key == Key.Z
			&& !bindings.Find(KeyBindingIds.Undo).ChordAt(0).Shift);
		Check("defaults: redo carries BOTH of its chords on one row (Ctrl+Shift+Z, Ctrl+Y)",
			bindings.Find(KeyBindingIds.Redo).ChordCount == 2
			&& bindings.Find(KeyBindingIds.Redo).ChordAt(0).Key == Key.Z
			&& bindings.Find(KeyBindingIds.Redo).ChordAt(0).Shift
			&& bindings.Find(KeyBindingIds.Redo).ChordAt(1).Key == Key.Y
			&& bindings.Find(KeyBindingIds.Redo).ChordAt(1).Ctrl
			&& !bindings.Find(KeyBindingIds.Redo).ChordAt(1).Shift);
		Check("defaults: delete and the terminal toggle are unmodified keys",
			bindings.Find(KeyBindingIds.Delete).ChordAt(0).Key == Key.Delete
			&& bindings.Find(KeyBindingIds.Delete).ChordAt(0).IsModifierless
			&& bindings.Find(KeyBindingIds.Terminal).ChordAt(0).Key == Key.Quoteleft);
		Check("defaults: the gameplay action name is the script vocabulary",
			bindings.Find("action.moveForward").ActionName == "moveForward");

		bool allDefault = true;
		bool allBound = true;
		for (int i = 0; i < bindings.Rows.Count; i++)
		{
			if (!bindings.Rows[i].IsDefault) allDefault = false;
			if (bindings.Rows[i].ChordCount == 0) allBound = false;
		}
		Check("defaults: every row starts at its default keys", allDefault);
		Check("defaults: no row starts unbound", allBound);
		Check("defaults: an out-of-range key read is harmless",
			bindings.Find(KeyBindingIds.Save).ChordAt(9).Key == Key.None);
	}

	// --- chord format / parse ---------------------------------------------------------------------

	private static void TestChordFormatting()
	{
		KeyChord[] chords =
		{
			new KeyChord(Key.W),
			new KeyChord(Key.Space),
			new KeyChord(Key.Delete),
			new KeyChord(Key.Quoteleft),
			new KeyChord(Key.Shift),
			new KeyChord(Key.S, ctrl: true),
			new KeyChord(Key.Z, ctrl: true, shift: true),
			new KeyChord(Key.Y, ctrl: true, alt: true),
		};

		bool allRoundTrip = true;
		for (int i = 0; i < chords.Length; i++)
		{
			if (!KeyChord.TryParse(chords[i].Format(), out KeyChord parsed)) { allRoundTrip = false; break; }
			if (parsed.Key != chords[i].Key || parsed.Ctrl != chords[i].Ctrl
				|| parsed.Shift != chords[i].Shift || parsed.Alt != chords[i].Alt)
			{
				allRoundTrip = false;
				break;
			}
		}
		Check("chord: every default chord round-trips through Format/TryParse", allRoundTrip);

		Check("chord: modifiers are spelled Ctrl+/Shift+/Alt+",
			new KeyChord(Key.Z, ctrl: true, shift: true).Format()
				== "Ctrl+Shift+" + OS.GetKeycodeString(Key.Z));
		Check("chord: a bare modifier reports as one", KeyChord.IsModifierKey(Key.Ctrl)
			&& KeyChord.IsModifierKey(Key.Shift) && KeyChord.IsModifierKey(Key.Alt)
			&& !KeyChord.IsModifierKey(Key.W));

		// Ctrl and Cmd are the same binding (the code editor's clipboard convention).
		Check("chord: a Meta press matches a Ctrl chord",
			new KeyChord(Key.S, ctrl: true).Matches(Press(Key.S, meta: true)));
		Check("chord: an unmodified press does not match a Ctrl chord",
			!new KeyChord(Key.S, ctrl: true).Matches(Press(Key.S)));
		Check("chord: a release and an echo do not match",
			!new KeyChord(Key.W).Matches(Press(Key.W, pressed: false))
			&& !new KeyChord(Key.W).Matches(Press(Key.W, echo: true))
			&& !new KeyChord(Key.W).Matches(null));
	}

	private static void TestParseRefusals()
	{
		Check("parse: empty and null are refused",
			!KeyChord.TryParse("", out KeyChord _) && !KeyChord.TryParse(null, out KeyChord _));
		Check("parse: an unknown modifier is refused", !KeyChord.TryParse("Hyper+W", out KeyChord _));
		Check("parse: an unknown key name is refused", !KeyChord.TryParse("Ctrl+NotAKey", out KeyChord _));
	}

	// --- conflicts --------------------------------------------------------------------------------

	private static void TestConflicts()
	{
		KeyBindings bindings = new KeyBindings();

		Check("conflict: the W key is reported as already used",
			bindings.FindConflict(KeyBindingIds.Delete, 0, new KeyChord(Key.W), out bool _) == "Move forward");
		Check("conflict: a row ignoring its own slot finds no conflict",
			bindings.FindConflict("action.moveForward", 0, new KeyChord(Key.W), out bool _) == "");
		Check("conflict: an unused chord has no conflict",
			bindings.FindConflict(KeyBindingIds.Delete, 0, new KeyChord(Key.P), out bool _) == "");
		Check("conflict: the label names the owner",
			bindings.LabelFor("action.moveForward") == "Move forward");

		// A key already on the row is a duplicate, not a clash with another action.
		string duplicate = bindings.FindConflict(KeyBindingIds.Redo, 9, new KeyChord(Key.Y, ctrl: true), out bool sameRow);
		Check("conflict: a key already on the same row is reported as a duplicate",
			duplicate == "Redo" && sameRow);
		Check("conflict: the row's own slot being replaced is not a duplicate",
			bindings.FindConflict(KeyBindingIds.Redo, 1, new KeyChord(Key.Y, ctrl: true), out bool _) == "");
		Check("conflict: the second key of a row is matched too",
			!bindings.TryAssign(KeyBindingIds.Delete, 0, new KeyChord(Key.Y, ctrl: true), out string dupMessage)
			&& dupMessage.Contains("already used by Redo"));

		Check("assign: a key is replaced in place",
			bindings.TryAssign(KeyBindingIds.Delete, 0, new KeyChord(Key.P), out string _)
			&& bindings.Find(KeyBindingIds.Delete).ChordAt(0).Key == Key.P);
		Check("assign: adding a key the row already has is refused as a duplicate",
			!bindings.TryAssign(KeyBindingIds.Delete, 1, new KeyChord(Key.P), out string dupRowMessage)
			&& dupRowMessage.Contains("already on Delete selection"));
		Check("assign: replacing a slot with the same key is a no-op success",
			bindings.TryAssign(KeyBindingIds.Delete, 0, new KeyChord(Key.P), out string _)
			&& bindings.Find(KeyBindingIds.Delete).ChordCount == 1);
		bindings.Find(KeyBindingIds.Delete).ResetToDefault();
		Check("assign: restore returns the default",
			bindings.Find(KeyBindingIds.Delete).ChordAt(0).Key == Key.Delete);
	}

	// --- persistence ------------------------------------------------------------------------------

	private static void TestPersistence()
	{
		ConfigFile seed = new ConfigFile();
		seed.SetValue("wallpaper", "source_path", "user://keep.png");
		seed.Save(ScratchPath);

		KeyBindings written = new KeyBindings();
		written.TryAssign(KeyBindingIds.Save, 0, new KeyChord(Key.P, ctrl: true), out string _);
		written.TryAssign(KeyBindingIds.Save, 1, new KeyChord(Key.Enter), out string _);
		written.TryAssign("action.jump", 0, new KeyChord(Key.Q), out string _);
		written.TryAssign(KeyBindingIds.Terminal, 0, new KeyChord(Key.F1), out string _);
		written.TryRemoveChord(KeyBindingIds.Terminal, 0);   // the "x" on a row's only key: now unbound
		written.Save(ScratchPath);

		KeyBindings read = new KeyBindings();
		read.Load(ScratchPath);
		Check("persist: an edited editor chord survives",
			read.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.P && read.Find(KeyBindingIds.Save).ChordAt(0).Ctrl);
		Check("persist: BOTH keys of a row survive",
			read.Find(KeyBindingIds.Save).ChordCount == 2 && read.Find(KeyBindingIds.Save).ChordAt(1).Key == Key.Enter);
		Check("persist: the key order is the order they were added",
			read.Find(KeyBindingIds.Save).ChordAt(0).Format().StartsWith("Ctrl+"));
		Check("persist: an edited gameplay key survives", read.Find("action.jump").ChordAt(0).Key == Key.Q);
		Check("persist: an untouched row keeps its default (and its extra keys)",
			read.Find(KeyBindingIds.Undo).ChordAt(0).Key == Key.Z && read.Find(KeyBindingIds.Redo).ChordCount == 2);
		Check("persist: a row left without keys is written as unbound, not as its default",
			read.Find(KeyBindingIds.Terminal).ChordCount == 0 && read.Find(KeyBindingIds.Terminal).ChordAt(0).Key == Key.None);

		ConfigFile live = new ConfigFile();
		live.Load(ScratchPath);
		Check("persist: the list form is what lands on disk",
			live.GetValue("keybindings", KeyBindingIds.Save, "").AsString() == "Ctrl+P, Enter");
		Check("persist: an unbound row stores an empty value",
			live.GetValue("keybindings", KeyBindingIds.Terminal, "missing").AsString() == "");

		ConfigFile config = new ConfigFile();
		config.Load(ScratchPath);
		Check("persist: the wallpaper block survives a keybinding save",
			config.GetValue("wallpaper", "source_path", "").AsString() == "user://keep.png");

		KeyBindings missing = new KeyBindings();
		missing.Load("user://keybindings_selftest_missing.cfg");
		Check("persist: a missing file restores the defaults",
			missing.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.S);

		// A single-chord value written by an older build is a one-entry list, not a parse failure.
		ConfigFile legacy = new ConfigFile();
		legacy.Load(ScratchPath);
		legacy.SetValue("keybindings", KeyBindingIds.Save, "Ctrl+O");
		legacy.SetValue("keybindings", "action.jump", "F");
		legacy.Save(ScratchPath);
		KeyBindings upgraded = new KeyBindings();
		upgraded.Load(ScratchPath);
		Check("persist: a pre-multi-key value loads as a single key",
			upgraded.Find(KeyBindingIds.Save).ChordCount == 1
			&& upgraded.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.O
			&& upgraded.Find("action.jump").ChordAt(0).Key == Key.F);

		// A corrupt list is treated as unbound rather than silently restored to the default.
		legacy.SetValue("keybindings", KeyBindingIds.Save, "Hyper+Q");
		legacy.Save(ScratchPath);
		KeyBindings corrupt = new KeyBindings();
		corrupt.Load(ScratchPath);
		Check("persist: an unparseable value leaves the row unbound (never a silent default)",
			corrupt.Find(KeyBindingIds.Save).ChordCount == 0);

		// Restoring defaults restores each row's own default key list: Redo keeps both of its keys,
		// the rows this test edited return to their single default.
		read.RestoreDefaults();
		Check("restore: a multi-key row keeps all of its default keys",
			read.Find(KeyBindingIds.Redo).ChordCount == 2);
		Check("restore: an edited row returns to its default key",
			read.Find(KeyBindingIds.Save).ChordCount == 1 && read.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.S);
	}

	// --- the pure editor-command resolver ---------------------------------------------------------

	private static void TestEditorResolution()
	{
		Check("resolve: Ctrl+S is save",
			KeyBindingService.ResolveEditorCommand(Press(Key.S, ctrl: true)) == KeyBindingIds.Save);
		Check("resolve: a plain S is nothing",
			KeyBindingService.ResolveEditorCommand(Press(Key.S)) == "");
		Check("resolve: Ctrl+Z is undo",
			KeyBindingService.ResolveEditorCommand(Press(Key.Z, ctrl: true)) == KeyBindingIds.Undo);
		Check("resolve: Ctrl+Shift+Z is redo",
			KeyBindingService.ResolveEditorCommand(Press(Key.Z, ctrl: true, shift: true)) == KeyBindingIds.Redo);
		Check("resolve: Ctrl+Y is the same redo command",
			KeyBindingService.ResolveEditorCommand(Press(Key.Y, ctrl: true)) == KeyBindingIds.Redo);
		Check("resolve: Delete is delete",
			KeyBindingService.ResolveEditorCommand(Press(Key.Delete)) == KeyBindingIds.Delete);
		Check("resolve: the quote key is the terminal toggle",
			KeyBindingService.ResolveEditorCommand(Press(Key.Quoteleft)) == KeyBindingIds.Terminal);
		Check("resolve: an unbound key is nothing",
			KeyBindingService.ResolveEditorCommand(Press(Key.F7)) == "");
		Check("resolve: gameplay keys are not editor commands",
			KeyBindingService.ResolveEditorCommand(Press(Key.W)) == "");

		Check("menu: the accelerator label is the live chord",
			KeyBindingService.ChordLabel(KeyBindingIds.Undo).StartsWith("Ctrl+")
			&& KeyBindingService.ChordLabel(KeyBindingIds.Undo).Contains("Z"));
		Check("menu: a multi-key row shows its primary (first) key",
			KeyBindingService.ChordLabel(KeyBindingIds.Redo) == "Ctrl+Shift+" + OS.GetKeycodeString(Key.Z));
		Check("menu: an unknown id yields an empty accelerator", KeyBindingService.ChordLabel("nope") == "");
	}

	// --- the capture state machine -----------------------------------------------------------------

	private static void TestCaptureMachine()
	{
		string originalPath = KeyBindingService.SettingsPath;
		KeyBindingService.SettingsPath = ScratchPath;

		// A bare modifier keeps waiting and consumes nothing.
		KeyBindingService.BeginCapture(KeyBindingIds.Save, 0);
		bool modifierKept = KeyBindingService.TryCapture(Press(Key.Ctrl, ctrl: true), out bool _, out string _)
			&& KeyBindingService.IsCapturing(KeyBindingIds.Save, 0);
		Check("capture: a bare modifier keeps the capture open", KeyBindingService.Capturing && modifierKept);
		KeyBindingService.CancelCapture();

		// A free chord replaces the slot it was captured for.
		KeyBindingService.BeginCapture(KeyBindingIds.Save, 0);
		bool assigned = KeyBindingService.TryCapture(Press(Key.P, ctrl: true), out bool assignOk, out string assignMessage);
		Check("capture: a free chord is assigned",
			assigned && assignOk && KeyBindingService.Current.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.P);
		Check("capture: the assignment is reported", assignMessage.Contains("Save lot"));
		Check("capture: the capture ends after one key", !KeyBindingService.Capturing);

		// The "+" case: capturing at the row's current count appends a second key.
		KeyBindingService.BeginCapture(KeyBindingIds.Save, KeyBindingService.Current.Find(KeyBindingIds.Save).ChordCount);
		bool appended = KeyBindingService.TryCapture(Press(Key.Enter), out bool appendOk, out string appendMessage);
		Check("capture: a capture at the row's count ADDS a key",
			appended && appendOk && KeyBindingService.Current.Find(KeyBindingIds.Save).ChordCount == 2
			&& KeyBindingService.Current.Find(KeyBindingIds.Save).ChordAt(1).Key == Key.Enter);
		Check("capture: a key's capture is reported as an addition", appendMessage.Contains("also uses"));

		// Both keys now fire the same command; either can be removed on its own.
		Check("capture: both keys of the row now resolve to the same command",
			KeyBindingService.ResolveEditorCommand(Press(Key.P, ctrl: true)) == KeyBindingIds.Save
			&& KeyBindingService.ResolveEditorCommand(Press(Key.Enter)) == KeyBindingIds.Save);
		bool removed = KeyBindingService.RemoveChord(KeyBindingIds.Save, 0, out string removeMessage);
		Check("remove: the 'x' drops one key and keeps the rest",
			removed && KeyBindingService.Current.Find(KeyBindingIds.Save).ChordCount == 1
			&& KeyBindingService.Current.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.Enter
			&& removeMessage.Contains("Ctrl+P"));

		// Removing the last key leaves the action unbound, and says so.
		bool unbound = KeyBindingService.RemoveChord(KeyBindingIds.Save, 0, out string unbindMessage);
		Check("remove: removing the last key unbinds the action",
			unbound && KeyBindingService.Current.Find(KeyBindingIds.Save).ChordCount == 0
			&& unbindMessage.Contains("unbound"));
		Check("remove: an out-of-range slot is refused",
			!KeyBindingService.RemoveChord(KeyBindingIds.Save, 0, out string _));

		// A conflicting chord is refused, names the owner, and still ends the capture.
		KeyBindingService.BeginCapture(KeyBindingIds.Undo, 0);
		bool conflictConsumed = KeyBindingService.TryCapture(
			Press(Key.Z, ctrl: true, shift: true), out bool conflictOk, out string conflictMessage);
		Check("capture: a conflicting chord is refused",
			conflictConsumed && !conflictOk && conflictMessage.Contains("Redo"));
		Check("capture: a refused capture still ends", !KeyBindingService.Capturing);
		Check("capture: the refused chord was not applied",
			KeyBindingService.Current.Find(KeyBindingIds.Undo).ChordAt(0).Key == Key.Z
			&& !KeyBindingService.Current.Find(KeyBindingIds.Undo).ChordAt(0).Shift);

		// A gameplay action refuses a modifier chord; an unknown id is refused outright.
		Check("capture: a gameplay action refuses modifiers",
			!KeyBindingService.TryAssign("action.jump", 0, new KeyChord(Key.J, ctrl: true), out string gameplayMessage)
			&& gameplayMessage.Contains("plain"));
		Check("capture: an unknown id is refused",
			!KeyBindingService.TryAssign("nope", 0, new KeyChord(Key.J), out string _));

		// Leave the live bindings and the settings path exactly as they were found.
		KeyBindingService.RestoreDefaults();
		KeyBindingService.SettingsPath = originalPath;
		Check("capture: restore returns the live bindings to their defaults",
			KeyBindingService.Current.Find(KeyBindingIds.Save).ChordCount == 1
			&& KeyBindingService.Current.Find(KeyBindingIds.Save).ChordAt(0).Key == Key.S);
	}

	// --- the InputMap side of several keys ---------------------------------------------------------

	/// <summary>
	/// The gameplay half of a multi-key row really reaches Godot: one action, one key event per bound
	/// key, an empty row clearing them (so an unbound action stops firing), and the pad events left
	/// alone. <c>RebindKeys</c> registers the action itself, so this runs headlessly before any session
	/// exists; the live table is re-applied afterwards so later suites see the real bindings.
	/// </summary>
	private static void TestInputMapMultiKey()
	{
		StringName name = new StringName("openlot_moveForward");

		LotInputActions.RebindKeys("moveForward", new[] { Key.W, Key.Up });
		Check("inputmap: two keys on one action become two key events",
			CountKeyEvents(name) == 2 && HasKeyEvent(name, Key.W) && HasKeyEvent(name, Key.Up));
		Check("inputmap: gamepad events survive a keyboard rebind (pad rebinding is §16.1's)",
			CountNonKeyEvents(name) > 0);

		LotInputActions.RebindKeys("moveForward", new Key[0]);
		Check("inputmap: an empty row clears the action's keyboard shortcuts", CountKeyEvents(name) == 0);

		KeyBindingService.ApplyToInputMap();
		KeyBinding moveForward = KeyBindingService.Current.Find("action.moveForward");
		Check("inputmap: re-applying the table writes exactly the row's own keys",
			CountKeyEvents(name) == moveForward.ChordCount && HasKeyEvent(name, moveForward.ChordAt(0).Key));
	}

	private static int CountKeyEvents(StringName name)
	{
		Godot.Collections.Array<InputEvent> events = InputMap.ActionGetEvents(name);
		int count = 0;
		for (int i = 0; i < events.Count; i++)
		{
			if (events[i] is InputEventKey) count++;
		}
		return count;
	}

	private static int CountNonKeyEvents(StringName name)
	{
		Godot.Collections.Array<InputEvent> events = InputMap.ActionGetEvents(name);
		int count = 0;
		for (int i = 0; i < events.Count; i++)
		{
			if (!(events[i] is InputEventKey)) count++;
		}
		return count;
	}

	private static bool HasKeyEvent(StringName name, Key key)
	{
		Godot.Collections.Array<InputEvent> events = InputMap.ActionGetEvents(name);
		for (int i = 0; i < events.Count; i++)
		{
			if (events[i] is InputEventKey keyEvent && keyEvent.Keycode == key) return true;
		}
		return false;
	}
}

