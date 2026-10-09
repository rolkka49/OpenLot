using Godot;

/// <summary>
/// The editor's constraint tools (milestone 3.6). Arming one turns the next clicks in the 3D view
/// into link endpoints instead of selections:
///   * Weld asks for two parts, in order; the finished gesture is one undo entry.
///   * Hinge asks for two sides first — a part, then the second part (or empty space, which pins
///     the hinge to the world) — and then enters a PLACING stage: a visible hinge-point marker
///     (a ball with its axis rod) appears at the first click's point, further clicks move it, and
///     the Toolbox's Confirm/Cancel buttons finish or abandon the gesture. Nothing is created
///     until Confirm, so a misplaced hinge costs nothing.
///
/// Mouse intent is recorded during the ImGui layout (ViewportWindow owns that state) and resolved
/// in Builder._PhysicsProcess, where Godot allows the space-state ray — the same split
/// PartDragController uses, and the ray is that controller's own pick so both find the same part.
/// </summary>
public sealed class ConstraintPicker
{
	public enum Tool
	{
		None,
		Weld,
		Hinge
	}

	private readonly Builder _builder;

	/// <summary>Which tool is waiting for clicks (None = the viewport behaves normally).</summary>
	public Tool Armed { get; private set; } = Tool.None;

	// The gesture so far. `_secondPicked` distinguishes "no second side yet" from "the second side
	// is the world" (which is a real choice, not an absence).
	private LotObject _firstPart;
	private Vector3 _firstPoint;
	private LotObject _secondPart;
	private bool _secondPicked;
	private bool _placing;
	private Vector3 _hingePoint;
	private Vector3 _hingeAxis = Vector3.Up;

	// Marker visuals, created when placement starts and freed when the gesture ends.
	private Node3D _marker;
	private MeshInstance3D _axisRod;

	// Click recorded in the layout pass, consumed in the physics step.
	private bool _clickQueued;
	private Vector3 _rayOrigin;
	private Vector3 _rayDirection;

	public ConstraintPicker(Builder builder)
	{
		_builder = builder;
	}

	public bool IsArmed { get { return Armed != Tool.None; } }

	/// <summary>True while a hinge's point is being placed (the Confirm/Cancel step).</summary>
	public bool IsPlacingHinge { get { return _placing; } }

	/// <summary>One line for the Toolbox panel telling the creator what the tool wants next.</summary>
	public string Hint
	{
		get
		{
			if (Armed == Tool.Weld)
			{
				return _firstPart == null
					? "Weld: click the first part (Esc cancels)"
					: "Weld: click the second part (Esc cancels)";
			}
			if (Armed == Tool.Hinge)
			{
				if (_firstPart == null) return "Hinge: click the first part (Esc cancels)";
				if (!_secondPicked) return "Hinge: click the second part, or empty space for the world (Esc cancels)";
				return "Hinge: click to move the hinge point, then Confirm (Esc cancels)";
			}
			return "";
		}
	}

	/// <summary>Arms a tool (or disarms it when re-picking the same button), dropping any half gesture.</summary>
	public void Arm(Tool tool)
	{
		Armed = Armed == tool ? Tool.None : tool;
		ClearGesture();
	}

	/// <summary>Ends the gesture. Escape, Confirm and every finished pick go through here.</summary>
	public void Cancel()
	{
		Armed = Tool.None;
		ClearGesture();
	}

	private void ClearGesture()
	{
		_firstPart = null;
		_secondPart = null;
		_secondPicked = false;
		_placing = false;
		_clickQueued = false;
		FreeMarker();
	}

	/// <summary>Records a left-click ray (layout pass); the pick itself runs in <see cref="Tick"/>.</summary>
	public void QueueClick(Vector3 rayOrigin, Vector3 rayDirection)
	{
		if (!IsArmed) return;
		_rayOrigin = rayOrigin;
		_rayDirection = rayDirection;
		_clickQueued = true;
	}

	/// <summary>Resolves a queued click. Runs from Builder._PhysicsProcess (space queries are only
	/// legal in the physics step).</summary>
	public void Tick()
	{
		if (!_clickQueued) return;
		_clickQueued = false;
		if (!IsArmed) return;
		// A part deleted mid-gesture invalidates it instead of picking a ghost. (A null second part
		// is legitimate: it is the hinge's world side.)
		if (_firstPart != null && !GodotObject.IsInstanceValid(_firstPart))
		{
			Cancel();
			return;
		}
		if (_secondPart != null && !GodotObject.IsInstanceValid(_secondPart))
		{
			Cancel();
			return;
		}

		LotObject hit = PartDragController.Pick(_builder.Scene, _rayOrigin, _rayDirection, out Vector3 point);
		if (Armed == Tool.Weld) HandleWeld(hit);
		else if (_placing) HandleHingePlace(hit, point);
		else HandleHingePick(hit, point);
	}

	private void HandleWeld(LotObject hit)
	{
		if (hit == null)
		{
			LotLog.Warn("constraints", "weld tool: click a part, not empty space");
			GD.PushWarning("[Constraints] weld tool: click a part, not empty space");
			return;
		}
		if (_firstPart == null)
		{
			_firstPart = hit;
			return;
		}
		if (_firstPart == hit)
		{
			LotLog.Warn("constraints", "weld tool: a weld needs two different parts");
			GD.PushWarning("[Constraints] weld tool: a weld needs two different parts");
			return;
		}

		int handleA = _builder.Scene.EnsureEntityHandle(_firstPart);
		int handleB = _builder.Scene.EnsureEntityHandle(hit);
		LotConstraints.CanonicalPair(handleA, handleB, out int first, out int second);
		if (LotConstraints.FindWeld(first, second) == null)
		{
			LotConstraintRecord record = new LotConstraintRecord
			{
				Kind = LotConstraintKind.Weld,
				A = first,
				B = second
			};
			_builder.History.Push(new DelegateCommand("Weld Parts",
				b => { LotConstraints.Add(record); if (b.Scene != null) b.Scene.RebuildConstraints(); },
				b => { LotConstraints.Remove(record.Id); if (b.Scene != null) b.Scene.RebuildConstraints(); }));
		}
		Cancel();
	}

	private void HandleHingePick(LotObject hit, Vector3 point)
	{
		if (_firstPart == null)
		{
			if (hit == null)
			{
				LotLog.Warn("constraints", "hinge tool: click a part first (its click point seeds the hinge point)");
				GD.PushWarning("[Constraints] hinge tool: click a part first (its click point seeds the hinge point)");
				return;
			}
			_firstPart = hit;
			_firstPoint = point;
			return;
		}

		if (hit == _firstPart)
		{
			LotLog.Warn("constraints", "hinge tool: the second side must be a different part (click empty space for the world)");
			GD.PushWarning("[Constraints] hinge tool: the second side must be a different part (click empty space for the world)");
			return;
		}

		_secondPart = hit; // null is the world side, deliberately
		_secondPicked = true;
		// Seed the marker at the first click's point — for a door that is usually the hinge edge
		// the creator clicked — and the axis at the swing-friendly default, then let the sharp
		// tools (Toolbox, Inspector) and further clicks adjust both before Confirm.
		_hingePoint = _firstPoint;
		_hingeAxis = DefaultHingeAxis(_firstPart, hit);
		_placing = true;
		EnsureMarker();
	}

	/// <summary>Moves the hinge point: a click on a surface puts it there; a click on empty space
	/// slides it along the camera-facing plane through its current position (the fallback the part
	/// drag uses too), so it can be placed anywhere in the view.</summary>
	private void HandleHingePlace(LotObject hit, Vector3 point)
	{
		if (hit != null)
		{
			_hingePoint = point;
			UpdateMarker();
			return;
		}

		Vector3 normal = -_rayDirection;
		float denominator = normal.Dot(_rayDirection);
		if (Mathf.Abs(denominator) < 1e-6f) return;
		float t = normal.Dot(_hingePoint - _rayOrigin) / denominator;
		if (t <= 0f) return;
		_hingePoint = _rayOrigin + _rayDirection * t;
		UpdateMarker();
	}

	/// <summary>
	/// Finishes a placed hinge: records the link (the marked point, the default axis) as ONE undo
	/// entry. Called by the Toolbox's Confirm button; a no-op when no hinge is being placed.
	/// </summary>
	public void ConfirmHinge()
	{
		if (!_placing || _firstPart == null) return;
		LotObject pivotPart = _firstPart;
		LotObject second = _secondPart;
		int handleA = _builder.Scene.EnsureEntityHandle(pivotPart);
		int handleB = second != null ? _builder.Scene.EnsureEntityHandle(second) : LotConstraintRecord.WorldHandle;
		// The pivot is stored in the first part's local space, so the hinge follows the part.
		Vector3 localPivot = pivotPart.GlobalTransform.AffineInverse() * _hingePoint;
		LotConstraintRecord record = new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = handleA,
			B = handleB,
			Pivot = localPivot,
			Axis = _hingeAxis
		};
		_builder.History.Push(new DelegateCommand("Hinge",
			b => { LotConstraints.Add(record); if (b.Scene != null) b.Scene.RebuildConstraints(); },
			b => { LotConstraints.Remove(record.Id); if (b.Scene != null) b.Scene.RebuildConstraints(); }));
		Cancel();
	}

	// --- sharp axis tools (placement step) ---------------------------------------------------------
	//
	// Thin shells over HingeAxisMath, shared with the Inspector's hinge rows: exact presets and 45°
	// steps. Each press re-aims the marker rod, so the result is visible before Confirm.

	/// <summary>Points the pending hinge axis straight up.</summary>
	public void MakeHingeAxisVertical()
	{
		_hingeAxis = HingeAxisMath.VerticalAxis;
		UpdateMarker();
	}

	/// <summary>Flattens the pending hinge axis onto the ground plane, keeping its heading.</summary>
	public void MakeHingeAxisHorizontal()
	{
		_hingeAxis = HingeAxisMath.Horizontal(_hingeAxis, DefaultHingeAxis(_firstPart, _secondPart));
		UpdateMarker();
	}

	/// <summary>Tilts the pending hinge axis 45° in its own vertical plane.</summary>
	public void TiltHingeAxis()
	{
		_hingeAxis = HingeAxisMath.Tilt(_hingeAxis);
		UpdateMarker();
	}

	/// <summary>Yaws the pending hinge axis 45° about the world vertical.</summary>
	public void YawHingeAxis()
	{
		_hingeAxis = HingeAxisMath.Yaw(_hingeAxis);
		UpdateMarker();
	}

	/// <summary>
	/// The axis a fresh hinge gets: the horizontal direction perpendicular to the line between the
	/// two parts, so a hanging part swings under gravity like a pendulum instead of hanging in the
	/// zero-torque equilibrium a vertical axis produces — which reads as "the hinge does nothing".
	/// World hinges (and parts stacked vertically, where any horizontal direction is equivalent)
	/// fall back to world X. The sharp tools and the Inspector refine it afterwards.
	/// </summary>
	public static Vector3 DefaultHingeAxis(LotObject first, LotObject second)
	{
		if (first == null || second == null) return Vector3.Right;
		Vector3 delta = second.GlobalPosition - first.GlobalPosition;
		delta.Y = 0f;
		if (delta.LengthSquared() < 1e-4f) return Vector3.Right;
		return Vector3.Up.Cross(delta.Normalized()).Normalized();
	}

	// --- hinge-point marker ------------------------------------------------------------------------

	/// <summary>Creates the marker (via <see cref="ConstraintVisuals"/>, the same visual the editor's
	/// hinge markers use) and parks it at the current point. Idempotent while placing.</summary>
	private void EnsureMarker()
	{
		if (_marker != null && GodotObject.IsInstanceValid(_marker))
		{
			UpdateMarker();
			return;
		}

		_marker = ConstraintVisuals.CreateMarker(_builder.Scene.LotRoot, "HingePointMarker", out _axisRod);
		UpdateMarker();
	}

	private void UpdateMarker()
	{
		if (_marker == null || !GodotObject.IsInstanceValid(_marker)) return;
		_marker.GlobalPosition = _hingePoint;
		// The rod shows the axis Confirm will use; the sharp tools above re-aim it.
		ConstraintVisuals.AimAxis(_axisRod, _hingeAxis);
	}

	private void FreeMarker()
	{
		if (_marker != null && GodotObject.IsInstanceValid(_marker)) _marker.Free();
		_marker = null;
		_axisRod = null;
	}
}
