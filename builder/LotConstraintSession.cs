using System.Collections.Generic;
using Godot;

/// <summary>
/// Builds and tears the Godot joint nodes that enforce <see cref="LotConstraints"/> for a player
/// session (milestone 3.6), and performs the dynamic-body swap that makes the links meaningful.
///
/// Two rules from real-world Godot usage shape this type:
///   * A joint node must be a SIBLING of the bodies it constrains, never a child of one of them —
///     parenting a joint to a body it links is the documented way to get half-frozen axes.
///   * A `Generic6DofJoint3D` axis locks by enabling its limit with lower == upper (Godot has no
///     separate "locked" switch), which is why <see cref="LockAllAxes"/> is a flag plus two params
///     per axis rather than one call.
///
/// Joints are session artifacts: built from records when a session starts (or its scripts reload),
/// freed when it ends. They are marked <see cref="LotObject.InternalChildMeta"/>, so the hierarchy,
/// marquee and undo ignore them, exactly like the outline and collision children.
///
/// Anchored parts deliberately KEEP their <c>StaticBody3D</c> — an anchored part is infinite-mass by
/// definition, so swapping it to a frozen rigid body would only cost a body the solver has to skip.
/// Only unanchored parts become dynamic, which is also what makes a weld to an anchored part work
/// (Godot joints accept a static side; they need at least one rigid side to do anything).
/// </summary>
public sealed class LotConstraintSession
{
	/// <summary>Meta key carrying the record id on a built joint node, so a live edit can find it.</summary>
	private const string ConstraintIdMeta = "openlot_constraint_id";

	private readonly BuilderScene _scene;
	private readonly List<Node> _joints = new List<Node>();
	private readonly List<LotObject> _partBuffer = new List<LotObject>();
	private readonly List<LotObject> _simulatedParts = new List<LotObject>();
	private readonly Dictionary<int, bool> _activity = new Dictionary<int, bool>();

	public LotConstraintSession(BuilderScene scene)
	{
		_scene = scene;
	}

	/// <summary>How many joint nodes are currently built (0 outside a session).</summary>
	public int JointCount => _joints.Count;

	/// <summary>
	/// (Re)builds the whole session state: frees any previous joints, applies the body swap, then
	/// creates one joint per active constraint. Idempotent, so a script-runtime reload mid-session
	/// simply rebuilds against the new tree.
	/// </summary>
	/// <param name="playerCharacter">The part the player controller owns; it is kept static so
	/// physics never fights the controller for it (null when the lot has no capsule).</param>
	public void Build(LotObject playerCharacter)
	{
		TeardownJoints();
		ApplySimulation(true, playerCharacter);

		LotConstraints.ComputeActivity(IsAnchoredHandle, _activity);
		WarnOnLongChains();
		IReadOnlyList<LotConstraintRecord> all = LotConstraints.All;
		for (int i = 0; i < all.Count; i++)
		{
			LotConstraintRecord record = all[i];
			bool active;
			if (!_activity.TryGetValue(record.Id, out active) || !active) continue;

			LotObject partA = ResolvePart(record.A);
			if (partA == null) continue;

			if (record.Kind == LotConstraintKind.Weld)
			{
				LotObject partB = ResolvePart(record.B);
				if (partB == null) continue;
				BuildWeld(record, partA, partB);
			}
			else
			{
				LotObject partB = record.B == LotConstraintRecord.WorldHandle ? null : ResolvePart(record.B);
				if (record.B != LotConstraintRecord.WorldHandle && partB == null) continue;
				BuildHinge(record, partA, partB);
			}
		}
	}

	/// <summary>Frees every joint and returns every swapped body to its build-mode static form.</summary>
	public void Teardown()
	{
		TeardownJoints();
		ApplySimulation(false, null);
	}

	/// <summary>
	/// Warns when one welded island carries more active links than
	/// <see cref="LotConstraints.ChainWarnThreshold"/>: long dynamic chains visibly flex under load
	/// (the solver's shared limitation), and the choice between accepting that and anchoring a root
	/// or splitting the build is the creator's. A warning, never a cap.
	/// </summary>
	private void WarnOnLongChains()
	{
		int largest = LotConstraints.LargestLinkIsland(id =>
		{
			bool active;
			return _activity.TryGetValue(id, out active) && active;
		});
		if (largest > LotConstraints.ChainWarnThreshold)
		{
			LotLog.Warn("constraints", "an assembly carries " + largest + " active links; long "
				+ "dynamic chains flex under load — consider anchoring a root part or splitting the build");
			GD.PushWarning("[Constraints] an assembly carries " + largest + " active links; long "
				+ "dynamic chains flex under load — consider anchoring a root part or splitting the build");
		}
	}

	/// <summary>
	/// Frees the joints touching a part that is being destroyed. Called from the entity-destroy walk
	/// (a joint whose body node is gone is a crash waiting to happen), and tolerant of the record
	/// already having been removed — a missing record means the link is gone, so its joint goes too.
	/// </summary>
	public void RemoveJointsFor(int handle)
	{
		for (int i = _joints.Count - 1; i >= 0; i--)
		{
			LotConstraintRecord record = LotConstraints.Find(JointRecordId(_joints[i]));
			if (record == null || record.Touches(handle)) FreeJointAt(i);
		}
	}

	/// <summary>
	/// Re-applies a hinge record's limits and motor to its live joint, so a parameter edit made
	/// during a session takes effect immediately. Returns false when no joint is built for it
	/// (outside a session, or the link is inactive) — the values still apply at the next build.
	/// </summary>
	public bool ApplyHingeParams(int id)
	{
		LotConstraintRecord record = LotConstraints.Find(id);
		if (record == null || record.Kind != LotConstraintKind.Hinge) return false;
		HingeJoint3D joint = FindJoint(id) as HingeJoint3D;
		if (joint == null) return false;
		ApplyHingeParams(joint, record);
		return true;
	}

	// --- joint construction ------------------------------------------------------------------------

	private void BuildWeld(LotConstraintRecord record, LotObject a, LotObject b)
	{
		Generic6DofJoint3D joint = new Generic6DofJoint3D();
		joint.Name = "Weld_" + record.Id;
		joint.SetMeta(LotObject.InternalChildMeta, true);
		joint.SetMeta(ConstraintIdMeta, record.Id);
		joint.ExcludeNodesFromCollision = true;

		// Sibling of both bodies (see the type summary), oriented at the midpoint. A fully locked
		// 6DOF captures the parts' current relative pose, so the frame orientation is irrelevant.
		_scene.LotRoot.AddChild(joint);
		joint.GlobalTransform = new Transform3D(Basis.Identity, (a.GlobalPosition + b.GlobalPosition) * 0.5f);
		joint.NodeA = a.CollisionBody.GetPath();
		joint.NodeB = b.CollisionBody.GetPath();
		LockAllAxes(joint);
		_joints.Add(joint);
	}

	private void BuildHinge(LotConstraintRecord record, LotObject a, LotObject b)
	{
		HingeJoint3D joint = new HingeJoint3D();
		joint.Name = "Hinge_" + record.Id;
		joint.SetMeta(LotObject.InternalChildMeta, true);
		joint.SetMeta(ConstraintIdMeta, record.Id);
		joint.ExcludeNodesFromCollision = true;

		// The pivot is stored in A's local space, so a moved/aligned part carries its hinge along.
		_scene.LotRoot.AddChild(joint);
		joint.GlobalTransform = new Transform3D(BasisForAxis(record.Axis), a.GlobalTransform * record.Pivot);
		joint.NodeA = a.CollisionBody.GetPath();
		// An empty NodeB anchors the free rotation to a world point at the joint's position — the
		// "door in an invisible wall" form (same as Unity's connectedBody = null).
		if (b != null) joint.NodeB = b.CollisionBody.GetPath();
		ApplyHingeParams(joint, record);
		_joints.Add(joint);
	}

	/// <summary>Pushes a record's limit/motor values onto a hinge joint (radians, Godot's unit).</summary>
	private static void ApplyHingeParams(HingeJoint3D joint, LotConstraintRecord record)
	{
		joint.SetFlag(HingeJoint3D.Flag.UseLimit, record.LimitsEnabled);
		joint.SetParam(HingeJoint3D.Param.LimitLower, Mathf.DegToRad(record.LowerDeg));
		joint.SetParam(HingeJoint3D.Param.LimitUpper, Mathf.DegToRad(record.UpperDeg));
		joint.SetFlag(HingeJoint3D.Flag.EnableMotor, record.Motor == HingeMotorMode.Spin);
		joint.SetParam(HingeJoint3D.Param.MotorTargetVelocity, Mathf.DegToRad(record.MotorVelocity));
		joint.SetParam(HingeJoint3D.Param.MotorMaxImpulse, record.MotorMaxPush);
	}

	/// <summary>
	/// The joint basis that makes a Godot hinge rotate around <paramref name="axis"/>. A
	/// HingeJoint3D rotates around its own local Z, so this aims the Z column at the axis and builds
	/// a right-handed frame around it (X = reference x Z, Y = Z x X; world X stands in for up when
	/// the axis is vertical). The world-axis cases reproduce the original three frozen bases
	/// exactly, and the self-test pins the general construction. Pure and static so the convention
	/// is unit-testable without a physics step.
	/// </summary>
	public static Basis BasisForAxis(Vector3 axis)
	{
		Vector3 reference = Mathf.Abs(axis.Dot(Vector3.Up)) > 0.99f ? Vector3.Right : Vector3.Up;
		Vector3 x = reference.Cross(axis).Normalized();
		Vector3 y = axis.Cross(x);
		return new Basis(x, y, axis);
	}

	/// <summary>Locks all six axes: enabled limit, lower == upper == 0 (Godot's way of locking).</summary>
	private static void LockAllAxes(Generic6DofJoint3D joint)
	{
		joint.SetFlagX(Generic6DofJoint3D.Flag.EnableLinearLimit, true);
		joint.SetParamX(Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
		joint.SetParamX(Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
		joint.SetFlagY(Generic6DofJoint3D.Flag.EnableLinearLimit, true);
		joint.SetParamY(Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
		joint.SetParamY(Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
		joint.SetFlagZ(Generic6DofJoint3D.Flag.EnableLinearLimit, true);
		joint.SetParamZ(Generic6DofJoint3D.Param.LinearLowerLimit, 0f);
		joint.SetParamZ(Generic6DofJoint3D.Param.LinearUpperLimit, 0f);
		joint.SetFlagX(Generic6DofJoint3D.Flag.EnableAngularLimit, true);
		joint.SetParamX(Generic6DofJoint3D.Param.AngularLowerLimit, 0f);
		joint.SetParamX(Generic6DofJoint3D.Param.AngularUpperLimit, 0f);
		joint.SetFlagY(Generic6DofJoint3D.Flag.EnableAngularLimit, true);
		joint.SetParamY(Generic6DofJoint3D.Param.AngularLowerLimit, 0f);
		joint.SetParamY(Generic6DofJoint3D.Param.AngularUpperLimit, 0f);
		joint.SetFlagZ(Generic6DofJoint3D.Flag.EnableAngularLimit, true);
		joint.SetParamZ(Generic6DofJoint3D.Param.AngularLowerLimit, 0f);
		joint.SetParamZ(Generic6DofJoint3D.Param.AngularUpperLimit, 0f);
	}

	// --- simulation swap ---------------------------------------------------------------------------

	/// <summary>
	/// Swaps the parts that should be dynamic in this session. Only unanchored parts become rigid —
	/// anchored parts are infinite-mass by definition and stay <c>StaticBody3D</c> — and the player
	/// character is excluded because the controller writes its position every physics step. The
	/// simulated list is kept so <see cref="SyncVisuals"/> can run without walking the tree per step.
	/// </summary>
	private void ApplySimulation(bool simulate, LotObject exclude)
	{
		_partBuffer.Clear();
		CollectParts(_scene.LotRoot, _partBuffer);
		_simulatedParts.Clear();
		for (int i = 0; i < _partBuffer.Count; i++)
		{
			LotObject part = _partBuffer[i];
			if (!GodotObject.IsInstanceValid(part)) continue;
			// A node that is queued for deletion is still in the tree for the rest of the frame.
			// Simulating one would give it a root-hosted body that its own deletion cannot release
			// (the body is a sibling, not a child — see SetSimulated), leaving an orphaned
			// collider in the lot: §3.7's walk-path probe caught exactly that, where a just-
			// destroyed loose part's body blocked the character in the next session.
			if (part.IsQueuedForDeletion()) continue;
			bool shouldSimulate = simulate && !part.Anchored && part != exclude;
			// The lot root hosts the simulated body as a sibling of the part (see SetSimulated).
			part.SetSimulated(shouldSimulate, _scene.LotRoot);
			if (part.IsSimulated) _simulatedParts.Add(part);
		}
	}

	/// <summary>
	/// Pulls every simulated part's node to its physics body's transform. Runs once per physics step
	/// from BuilderScene._PhysicsProcess, because this is what actually makes a falling cube fall on
	/// screen: physics owns the body, the body is a sibling of the part, and the part is a puppet of
	/// it — the same shape CapsuleController uses for the player. The rotation is copied too, so a
	/// toppling part tips over visually. Allocation-free: the list is rebuilt only when the session
	/// is.
	/// </summary>
	public void SyncVisuals()
	{
		for (int i = 0; i < _simulatedParts.Count; i++)
		{
			LotObject part = _simulatedParts[i];
			if (part == null || !GodotObject.IsInstanceValid(part)) continue;
			PhysicsBody3D body = part.CollisionBody;
			if (body == null || !GodotObject.IsInstanceValid(body)) continue;
			part.GlobalTransform = body.GlobalTransform;
		}
	}

	/// <summary>Collects every LotObject under (and including descendants of) <paramref name="node"/>.</summary>
	private static void CollectParts(Node node, List<LotObject> into)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			LotObject part = child as LotObject;
			if (part != null) into.Add(part);
			// Parts can be nested (hierarchy reparenting), so recurse through everything; internal
			// children (mesh, outline, collision, joints) are not LotObjects and add nothing.
			CollectParts(child, into);
		}
	}

	private LotObject ResolvePart(int handle)
	{
		Node node = _scene.GetByHandle(handle);
		LotObject part = node as LotObject;
		if (part == null || !part.IsInsideTree()) return null;
		return part;
	}

	private bool IsAnchoredHandle(int handle)
	{
		LotObject part = _scene.GetByHandle(handle) as LotObject;
		// An unresolvable part counts as anchored: the link goes inactive instead of being built
		// against a body that is not there.
		return part == null || part.Anchored;
	}

	// --- joint bookkeeping -------------------------------------------------------------------------

	private HingeJoint3D FindJoint(int id)
	{
		for (int i = 0; i < _joints.Count; i++)
		{
			if (JointRecordId(_joints[i]) == id) return _joints[i] as HingeJoint3D;
		}
		return null;
	}

	private static int JointRecordId(Node joint)
	{
		if (joint == null || !GodotObject.IsInstanceValid(joint) || !joint.HasMeta(ConstraintIdMeta)) return -1;
		return joint.GetMeta(ConstraintIdMeta).AsInt32();
	}

	private void FreeJointAt(int index)
	{
		Node joint = _joints[index];
		_joints.RemoveAt(index);
		if (joint != null && GodotObject.IsInstanceValid(joint)) joint.Free();
	}

	private void TeardownJoints()
	{
		for (int i = 0; i < _joints.Count; i++)
		{
			Node joint = _joints[i];
			if (joint != null && GodotObject.IsInstanceValid(joint)) joint.Free();
		}
		_joints.Clear();
	}
}
