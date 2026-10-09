using Godot;

/// <summary>
/// Logical input actions for scripts and the game's own controllers (milestone 3.9, design doc
/// D1/D2). Wraps Godot's InputMap: the seven OpenLot action names are registered once with
/// default events (the keys the controllers already used, plus gamepad basics), one poll per
/// frame caches held / pressed-this-frame / released-this-frame / strength plus the accumulated
/// mouse deltas, and <see cref="Rebind"/> swaps an action's keyboard events (the in-memory half of
/// §5.5 — persistence lands with its settings screen).
///
/// Godot-infrastructure-facing: it exposes plain names, floats and booleans upward and nothing
/// else. The controllers read the snapshot, so a rebind moves the character, the freecam and
/// every script together; script reads are session-gated at the API surface, the game's own
/// controllers are not.
/// </summary>
public static class LotInputActions
{
	public const int MoveForward = 0;
	public const int MoveBackward = 1;
	public const int MoveLeft = 2;
	public const int MoveRight = 3;
	public const int Jump = 4;
	public const int Sprint = 5;
	public const int Interact = 6;
	public const int ActionCount = 7;

	/// <summary>The creator-facing action vocabulary. Do not mutate: this array is the contract.</summary>
	public static readonly string[] Names =
	{
		"moveForward", "moveBackward", "moveLeft", "moveRight", "jump", "sprint", "interact"
	};

	private static bool _registered;
	private static readonly bool[] _held = new bool[ActionCount];
	private static readonly bool[] _previousHeld = new bool[ActionCount];
	private static readonly float[] _strength = new float[ActionCount];
	private static float _mouseDeltaX;
	private static float _mouseDeltaY;
	private static int _wheel;
	private static float _pendingMouseX;
	private static float _pendingMouseY;
	private static int _pendingWheel;

	/// <summary>The action index for a creator-facing name, or -1 (case-sensitive, like the rest
	/// of the scripting vocabulary).</summary>
	public static int IndexOf(string name)
	{
		if (string.IsNullOrEmpty(name)) return -1;
		for (int i = 0; i < Names.Length; i++)
		{
			if (Names[i] == name) return i;
		}
		return -1;
	}

	/// <summary>The InputMap name (namespaced so a project/plugin action can never collide).</summary>
	private static string GodotName(int index)
	{
		return "openlot_" + Names[index];
	}

	// --- registration + rebinding ---------------------------------------------------------------

	private static void EnsureRegistered()
	{
		if (_registered) return;
		_registered = true;
		EnsureAction(MoveForward, KeyEvent(Key.W), AxisEvent(JoyAxis.LeftY, -1f));
		EnsureAction(MoveBackward, KeyEvent(Key.S), AxisEvent(JoyAxis.LeftY, 1f));
		EnsureAction(MoveLeft, KeyEvent(Key.A), AxisEvent(JoyAxis.LeftX, -1f));
		EnsureAction(MoveRight, KeyEvent(Key.D), AxisEvent(JoyAxis.LeftX, 1f));
		EnsureAction(Jump, KeyEvent(Key.Space), ButtonEvent(JoyButton.A));
		EnsureAction(Sprint, KeyEvent(Key.Shift), ButtonEvent(JoyButton.LeftShoulder));
		EnsureAction(Interact, KeyEvent(Key.E), ButtonEvent(JoyButton.X));
	}

	private static void EnsureAction(int index, params InputEvent[] events)
	{
		string name = GodotName(index);
		if (InputMap.HasAction(name)) return; // an existing action (a rebind) stands
		InputMap.AddAction(name);
		for (int i = 0; i < events.Length; i++)
		{
			InputMap.ActionAddEvent(name, events[i]);
		}
	}

	/// <summary>
	/// Swaps one action's KEYBOARD events for a single key (gamepad events stay). The in-memory
	/// half of §5.5's rebinding; the settings UI and persistence land with that milestone.
	/// </summary>
	public static void Rebind(string actionName, Key key)
	{
		int index = IndexOf(actionName);
		if (index < 0) return;
		EnsureRegistered();
		string name = GodotName(index);
		Godot.Collections.Array<InputEvent> existing = InputMap.ActionGetEvents(name);
		for (int i = existing.Count - 1; i >= 0; i--)
		{
			if (existing[i] is InputEventKey)
			{
				InputMap.ActionEraseEvent(name, existing[i]);
			}
		}
		InputMap.ActionAddEvent(name, KeyEvent(key));
	}

	private static InputEventKey KeyEvent(Key key)
	{
		InputEventKey keyEvent = new InputEventKey();
		keyEvent.Keycode = key;
		keyEvent.PhysicalKeycode = key;
		return keyEvent;
	}

	private static InputEventJoypadMotion AxisEvent(JoyAxis axis, float value)
	{
		InputEventJoypadMotion motion = new InputEventJoypadMotion();
		motion.Axis = axis;
		motion.AxisValue = value;
		return motion;
	}

	private static InputEventJoypadButton ButtonEvent(JoyButton button)
	{
		InputEventJoypadButton padButton = new InputEventJoypadButton();
		padButton.ButtonIndex = button;
		return padButton;
	}

	// --- the per-frame snapshot (design doc D2) -------------------------------------------------

	/// <summary>
	/// One poll per frame (called by <c>BuilderScene._Process</c>, every mode): reads every action
	/// and the accumulated mouse state once, so reads are pure lookups and two consumers in one
	/// frame can never disagree. Just-pressed/released are computed against the previous poll, so
	/// they are true for exactly one frame.
	/// </summary>
	public static void Poll()
	{
		EnsureRegistered();
		for (int i = 0; i < ActionCount; i++)
		{
			string name = GodotName(i);
			_previousHeld[i] = _held[i];
			_held[i] = Input.IsActionPressed(name);
			_strength[i] = Input.GetActionStrength(name);
		}
		_mouseDeltaX = _pendingMouseX;
		_mouseDeltaY = _pendingMouseY;
		_wheel = _pendingWheel;
		_pendingMouseX = 0f;
		_pendingMouseY = 0f;
		_pendingWheel = 0;
	}

	/// <summary>Clears the snapshot and the accumulators — called on the session boundary so no
	/// "pressed" edge survives it (design doc D3).</summary>
	public static void Reset()
	{
		for (int i = 0; i < ActionCount; i++)
		{
			_held[i] = false;
			_previousHeld[i] = false;
			_strength[i] = 0f;
		}
		_mouseDeltaX = 0f;
		_mouseDeltaY = 0f;
		_wheel = 0;
		_pendingMouseX = 0f;
		_pendingMouseY = 0f;
		_pendingWheel = 0;
	}

	/// <summary>Raw mouse motion, accumulated from events by the scene and snapshotted by the
	/// poll (design doc D8).</summary>
	public static void AccumulateMouseMotion(float relativeX, float relativeY)
	{
		_pendingMouseX += relativeX;
		_pendingMouseY += relativeY;
	}

	/// <summary>Wheel steps (+1 up / -1 down), accumulated like the motion.</summary>
	public static void AccumulateWheel(int direction)
	{
		_pendingWheel += direction;
	}

	// --- reads (pure snapshot lookups) ----------------------------------------------------------

	public static bool IsPressed(int index)
	{
		return index >= 0 && index < ActionCount && _held[index];
	}

	public static bool WasPressed(int index)
	{
		return index >= 0 && index < ActionCount && _held[index] && !_previousHeld[index];
	}

	public static bool WasReleased(int index)
	{
		return index >= 0 && index < ActionCount && !_held[index] && _previousHeld[index];
	}

	public static float Strength(int index)
	{
		return index >= 0 && index < ActionCount ? _strength[index] : 0f;
	}

	public static float MouseDeltaX { get { return _mouseDeltaX; } }
	public static float MouseDeltaY { get { return _mouseDeltaY; } }
	public static int Wheel { get { return _wheel; } }

	/// <summary>
	/// Test seam: applies a held/strength set exactly as a poll would (edge detection included),
	/// so the self-test can exercise the snapshot without real input devices.
	/// </summary>
	internal static void ApplySnapshotForTest(bool[] held, float[] strength)
	{
		for (int i = 0; i < ActionCount; i++)
		{
			_previousHeld[i] = _held[i];
			_held[i] = held != null && i < held.Length && held[i];
			_strength[i] = strength != null && i < strength.Length ? strength[i] : 0f;
		}
	}
}