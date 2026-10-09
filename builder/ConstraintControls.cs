using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// The Inspector's Constraints section (milestone 3.6): one editable row per weld/hinge that
/// touches the selected part. Rows are drawn from the lot-level <see cref="LotConstraints"/> table
/// rather than from node properties — a link is not a node property (a part can carry several) —
/// but every widget follows the same rules the property widgets do: one history entry per edit, the
/// Float fields apply live and commit once on release, and an inactive link (both sides anchored,
/// per the Roblox rule in <see cref="LotConstraints.ComputeActivity"/>) says so instead of silently
/// doing nothing.
///
/// Static, like the other inline controls (<see cref="CollisionGroupControls"/>), because the
/// Inspector owns exactly one instance and the per-frame buffers must not allocate.
/// </summary>
public static class ConstraintControls
{
	private static readonly List<LotConstraintRecord> _rows = new List<LotConstraintRecord>();
	private static readonly Dictionary<int, bool> _activity = new Dictionary<int, bool>();
	private static readonly string[] _motorItems = { "Off", "Spin" };

	// One float edit session at a time: ImGui has a single active item, so keying by (id, field)
	// is enough to know which value the session belongs to.
	private static bool _floatActive;
	private static int _floatId;
	private static string _floatField = "";
	private static float _floatBefore;

	public static void Draw(Builder builder, LotObject part)
	{
		if (builder.Scene == null || part == null) return;
		int handle = HandleOf(part);
		if (handle < 0) return;

		_rows.Clear();
		IReadOnlyList<LotConstraintRecord> all = LotConstraints.All;
		for (int i = 0; i < all.Count; i++)
		{
			if (all[i].Touches(handle)) _rows.Add(all[i]);
		}
		if (_rows.Count == 0) return;

		LotConstraints.ComputeActivity(h => IsAnchored(builder, h), _activity);

		ImGui.Spacing();
		ImGui.Separator();
		ImGui.TextDisabled("Constraints");
		for (int i = 0; i < _rows.Count; i++) DrawRow(builder, part, _rows[i]);
	}

	private static void DrawRow(Builder builder, LotObject part, LotConstraintRecord record)
	{
		bool active;
		_activity.TryGetValue(record.Id, out active);

		ImGui.Text(Describe(builder, record));
		if (!active)
		{
			ImGui.SameLine();
			ImGui.TextDisabled(record.Enabled ? "(inactive — both sides anchored)" : "(disabled)");
		}

		// Enabled is a link-presence change, so it rebuilds the session's joints.
		BoolWidget(builder, record, "Enabled", record.Enabled, value => record.Enabled = value, true);

		if (record.Kind == LotConstraintKind.Hinge)
		{
			AxisWidget(builder, record);
			BoolWidget(builder, record, "Limits", record.LimitsEnabled, value => record.LimitsEnabled = value, false);
			FloatWidget(builder, record, "lower", "Lower deg", 1f);
			FloatWidget(builder, record, "upper", "Upper deg", 1f);
			MotorWidget(builder, record);
			FloatWidget(builder, record, "velocity", "Velocity deg/s", 5f);
			FloatWidget(builder, record, "maxPush", "Max push", 1f);
		}

		ImGui.SameLine();
		if (ImGui.Button("Remove##c" + record.Id)) RemoveWithHistory(builder, record);
	}

	/// <summary>A checkbox that records one history entry per flip. <paramref name="rebuild"/> picks
	/// the live effect: a link-presence change rebuilds the session's joints, a parameter change is
	/// pushed onto the live joint.</summary>
	private static void BoolWidget(Builder builder, LotConstraintRecord record, string label,
		bool current, Action<bool> set, bool rebuild)
	{
		bool edited = ImGui.Checkbox(label + "##c" + record.Id, current);
		if (edited == current) return;
		bool after = edited;
		bool before = current;
		builder.History.Push(new DelegateCommand(label == "Enabled" ? "Toggle Constraint" : "Hinge " + label,
			b => { set(after); AfterEdit(b, record, rebuild); },
			b => { set(before); AfterEdit(b, record, rebuild); }));
	}

	/// <summary>
	/// The hinge axis editor: a direction readout plus the same sharp tools the placement step
	/// offers (<see cref="HingeAxisMath"/>) — exact vertical/horizontal presets and 45° tilt/yaw
	/// steps, so an orientation can be changed to any of those exactly, and nudged to an angle in
	/// between. One press, one history entry; the change is applied by rebuilding, because the axis
	/// IS the joint frame's orientation, and the editor's hinge markers in the view show the result
	/// without entering Test mode.
	/// </summary>
	private static void AxisWidget(Builder builder, LotConstraintRecord record)
	{
		ImGui.Text("Axis: " + FormatAxis(record.Axis));

		float width = ImGui.GetContentRegionAvail().X * 0.5f - 8f;
		if (AxisButton(record, "Vertical", width, "Point the hinge axis straight up."))
			ApplyAxis(builder, record, HingeAxisMath.VerticalAxis);
		ImGui.SameLine();
		if (AxisButton(record, "Horizontal", width, "Flatten the hinge axis onto the ground plane."))
			ApplyAxis(builder, record, HingeAxisMath.Horizontal(record.Axis, Vector3.Right));
		if (AxisButton(record, "Tilt 45", width, "Rotate the axis 45 degrees in its own vertical plane (horizontal -> diagonal -> vertical)."))
			ApplyAxis(builder, record, HingeAxisMath.Tilt(record.Axis));
		ImGui.SameLine();
		if (AxisButton(record, "Yaw 45", width, "Rotate the axis 45 degrees around the vertical."))
			ApplyAxis(builder, record, HingeAxisMath.Yaw(record.Axis));
	}

	/// <summary>One axis-tool button: unique per record, tooltip on hover.</summary>
	private static bool AxisButton(LotConstraintRecord record, string label, float width, string tooltip)
	{
		bool clicked = ImGui.Button(label + "##c" + record.Id, width, 24f);
		if (ImGui.IsItemHovered() && tooltip != null) ImGui.SetTooltip(tooltip);
		return clicked;
	}

	/// <summary>Records one axis change as a single history entry; the rebuild is what applies it
	/// (also live when a session is running).</summary>
	private static void ApplyAxis(Builder builder, LotConstraintRecord record, Vector3 after)
	{
		Vector3 before = record.Axis;
		if (after.IsEqualApprox(before)) return;
		builder.History.Push(new DelegateCommand("Hinge Axis",
			b => { record.Axis = after; if (b.Scene != null) b.Scene.RebuildConstraints(); },
			b => { record.Axis = before; if (b.Scene != null) b.Scene.RebuildConstraints(); }));
	}

	private static string FormatAxis(Vector3 axis)
	{
		return "(" + axis.X.ToString("0.00") + ", " + axis.Y.ToString("0.00") + ", "
			+ axis.Z.ToString("0.00") + ")";
	}

	private static void MotorWidget(Builder builder, LotConstraintRecord record)
	{
		int chosen = ImGui.Combo("Motor##c" + record.Id, (int)record.Motor, _motorItems);
		if (chosen == (int)record.Motor) return;
		HingeMotorMode after = (HingeMotorMode)chosen;
		HingeMotorMode before = record.Motor;
		builder.History.Push(new DelegateCommand("Hinge Motor",
			b => { record.Motor = after; AfterEdit(b, record, false); },
			b => { record.Motor = before; AfterEdit(b, record, false); }));
	}

	/// <summary>
	/// A hinge number. Applies live while the widget is active (the joint follows the drag) and
	/// commits ONE history entry on release — the same begin/commit split the Inspector's property
	/// floats use, so a drag cannot flood the stack.
	/// </summary>
	private static void FloatWidget(Builder builder, LotConstraintRecord record, string field, string label, float step)
	{
		float current = ReadField(record, field);
		float edited = ImGui.InputFloat(label + "##c" + record.Id, current, step, 0f);

		if (ImGui.IsItemActivated())
		{
			_floatActive = true;
			_floatId = record.Id;
			_floatField = field;
			_floatBefore = current;
		}
		if (edited != current)
		{
			WriteField(record, field, edited);
			AfterEdit(builder, record, false);
		}
		if (!_floatActive || _floatId != record.Id || _floatField != field) return;
		if (!ImGui.IsItemDeactivatedAfterEdit()) return;

		_floatActive = false;
		float after = ReadField(record, field);
		float before = _floatBefore;
		builder.History.Push(new DelegateCommand("Hinge " + label,
			b => { WriteField(record, field, after); AfterEdit(b, record, false); },
			b => { WriteField(record, field, before); AfterEdit(b, record, false); }));
	}

	private static void RemoveWithHistory(Builder builder, LotConstraintRecord record)
	{
		builder.History.Push(new DelegateCommand("Remove Constraint",
			b => { LotConstraints.Remove(record.Id); if (b.Scene != null) b.Scene.RebuildConstraints(); },
			b => { LotConstraints.Add(record); if (b.Scene != null) b.Scene.RebuildConstraints(); }));
	}

	/// <summary>The live effect of an edit: rebuild every joint, or push a hinge's parameters onto
	/// its joint when one is built (outside a session the values simply apply at the next build).</summary>
	private static void AfterEdit(Builder builder, LotConstraintRecord record, bool rebuild)
	{
		if (builder.Scene == null) return;
		if (rebuild) builder.Scene.RebuildConstraints();
		else if (builder.Scene.ConstraintSession != null) builder.Scene.ConstraintSession.ApplyHingeParams(record.Id);
	}

	private static float ReadField(LotConstraintRecord record, string field)
	{
		if (field == "lower") return record.LowerDeg;
		if (field == "upper") return record.UpperDeg;
		if (field == "velocity") return record.MotorVelocity;
		return record.MotorMaxPush;
	}

	private static void WriteField(LotConstraintRecord record, string field, float value)
	{
		if (field == "lower") record.LowerDeg = value;
		else if (field == "upper") record.UpperDeg = value;
		else if (field == "velocity") record.MotorVelocity = value;
		else record.MotorMaxPush = Mathf.Max(0f, value);
	}

	private static string Describe(Builder builder, LotConstraintRecord record)
	{
		string kind = record.Kind == LotConstraintKind.Weld ? "Weld" : "Hinge";
		string other = record.B == LotConstraintRecord.WorldHandle ? "the world" : NameOf(builder, record.B);
		return kind + " → " + other;
	}

	private static string NameOf(Builder builder, int handle)
	{
		Node node = builder.Scene != null ? builder.Scene.GetByHandle(handle) : null;
		return node != null ? node.Name.ToString() : "missing part";
	}

	private static bool IsAnchored(Builder builder, int handle)
	{
		LotObject part = builder.Scene != null ? builder.Scene.GetByHandle(handle) as LotObject : null;
		return part == null || part.Anchored;
	}

	private static int HandleOf(Node node)
	{
		if (node == null || !node.HasMeta(BuilderScene.HandleMeta)) return -1;
		return node.GetMeta(BuilderScene.HandleMeta).AsInt32();
	}
}
