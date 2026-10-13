using System.Collections.Generic;
using Godot;

/// <summary>
/// The set of objects a gizmo drag acts on, resolved from the build-mode selection.
///
/// With one member everything stays exactly as it was: the gizmo works on that node's own
/// transform, scale included (the six per-face precision handles of milestone 2.7). With two or
/// more the gizmo becomes a GROUP transform around a shared pivot — and the one rule that makes
/// that coherent is the scale rule: a group scale is UNIFORM, so every object keeps its own
/// proportions and only its distance from the pivot changes. That restriction is deliberately
/// group-only; a single selected object keeps its per-axis scale.
///
/// <para>Membership rules, all of them there to prevent double-work or nonsensical input:</para>
/// <list type="bullet">
/// <item>Only <see cref="Node3D"/>s under the 3D lot root (a UI element or a script placeholder
/// is not a 3D transform).</item>
/// <item>Internal children are skipped (mesh, collision body, drag mover, mask meshes).</item>
/// <item>A decal is skipped: its transform is derived from its host's face (see
/// <see cref="DecalMath"/>), so a gizmo write would be overwritten on the next refresh — the same
/// reason a single selected decal shows no gizmo.</item>
/// <item>A node whose ANCESTOR is also selected is skipped, because moving the ancestor already
/// moves it: transforming both would apply the change twice.</item>
/// </list>
///
/// <para>Everything the group applies is ABSOLUTE from a capture taken just before the drag's
/// first frame (the §2.7 drift lesson): holding the cursor still holds the objects still, and
/// dragging further keeps tracking — never the sum of per-frame deltas.</para>
/// </summary>
public sealed class GizmoSelectionGroup
{
	/// <summary>One object a group drag transforms, with the state the whole operation is
	/// measured from.</summary>
	public sealed class Member
	{
		public Node3D Node;
		/// <summary>The member as a lot part, when it is one (null for a plain group node).</summary>
		public LotObject Part;
		/// <summary>True when the member resizes through the authored Size rather than node scale.</summary>
		public bool HasSize;
		/// <summary>The member's LOCAL transform when the drag was captured (what undo restores).</summary>
		public Transform3D Before;
		/// <summary>The member's GLOBAL transform when the drag was captured (what the math uses).</summary>
		public Transform3D BeforeGlobal;
		public Vector3 BeforeSize;
	}

	private readonly List<Member> _pool = new List<Member>();
	private readonly List<Node> _selectionScratch = new List<Node>();
	private int _count;
	private Vector3 _pivot;
	private bool _captured;

	/// <summary>How many members the resolved selection has.</summary>
	public int Count { get { return _count; } }

	/// <summary>True when the gizmo should act as a group (two or more members).</summary>
	public bool IsGroup { get { return _count >= 2; } }

	/// <summary>The one member, or null when there are none or several.</summary>
	public Node3D Single { get { return _count == 1 ? _pool[0].Node : null; } }

	/// <summary>Member accessor (the pool is reused between resolves, so there is no per-frame
	/// allocation while a gizmo is shown).</summary>
	public Member GetMember(int index) { return _pool[index]; }

	/// <summary>True once <see cref="Capture"/> has recorded a drag's start state.</summary>
	public bool Captured { get { return _captured; } }

	/// <summary>The pivot the current capture rotates and scales around.</summary>
	public Vector3 Pivot { get { return _pivot; } }

	/// <summary>
	/// Resolves the effective member list from the selection. Pure filtering — nothing is written —
	/// so it is safe to call every frame the gizmo draws.
	/// </summary>
	public void Resolve(SelectionManager selection, Node3D lotRoot)
	{
		_count = 0;
		if (selection == null || lotRoot == null) return;

		_selectionScratch.Clear();
		selection.CopySelection(_selectionScratch);
		for (int i = 0; i < _selectionScratch.Count; i++)
		{
			AddCandidate(_selectionScratch[i], lotRoot, selection);
		}
	}

	private void AddCandidate(Node node, Node3D lotRoot, SelectionManager selection)
	{
		if (node == null || !GodotObject.IsInstanceValid(node)) return;
		if (!(node is Node3D)) return;
		if (node.HasMeta(LotObject.InternalChildMeta)) return;
		if (node is LotScriptNode) return;
		if (node != lotRoot && !lotRoot.IsAncestorOf(node)) return;
		// The group's math is world-space (GlobalTransform), which Godot only defines for a node
		// INSIDE the tree — a detached node reports the identity and logs an error — so anything
		// outside it is skipped rather than dragged as if it were at the origin.
		if (!node.IsInsideTree()) return;

		LotObject part = node as LotObject;
		if (part != null && part.Kind == LotObjectKind.Decal) return;

		// A selected ancestor already carries this node: transforming both would double-apply.
		Node parent = node.GetParent();
		while (parent != null && parent != lotRoot)
		{
			if (selection.IsSelected(parent)) return;
			parent = parent.GetParent();
		}

		while (_pool.Count <= _count) _pool.Add(new Member());
		Member member = _pool[_count];
		member.Node = (Node3D)node;
		member.Part = part;
		member.HasSize = part != null && part.HasSize;
		_count++;
	}

	/// <summary>
	/// The live pivot while nothing is being dragged: the average of the members' own centres.
	///
	/// An average of centres rather than the centre of the combined bounds, deliberately: a
	/// rotated member's axis-aligned bounds are conservative (they grow as it tilts), so a
	/// bounds union would make the pivot wobble while the very operation being measured is a
	/// rotation. The centroid of centres is exactly invariant under the group rotation.
	/// </summary>
	public Vector3 LiveCenter()
	{
		if (_count == 0) return Vector3.Zero;
		Vector3 sum = Vector3.Zero;
		for (int i = 0; i < _count; i++) sum += MemberCentre(_pool[i].Node);
		return sum / _count;
	}

	/// <summary>
	/// Records where every member (and the pivot) starts, which is what makes the drag absolute.
	/// Called every idle frame while a gizmo is shown, so the capture that a drag uses is the last
	/// state before its first apply — no press-frame ordering hazard.
	/// </summary>
	public void Capture()
	{
		for (int i = 0; i < _count; i++)
		{
			Member member = _pool[i];
			if (member.Node == null || !GodotObject.IsInstanceValid(member.Node)) continue;
			member.Before = member.Node.Transform;
			member.BeforeGlobal = member.Node.GlobalTransform;
			member.BeforeSize = member.HasSize && member.Part != null ? member.Part.Size : Vector3.One;
		}
		_pivot = LiveCenter();
		_captured = true;
	}

	/// <summary>Forgets the capture (after a commit or when the gizmo is reset).</summary>
	public void ClearCapture()
	{
		_captured = false;
	}

	/// <summary>Every member moves by the same world-space offset.</summary>
	public void ApplyTranslate(Vector3 delta)
	{
		for (int i = 0; i < _count; i++)
		{
			Member member = _pool[i];
			if (!IsLive(member)) continue;
			Transform3D start = member.BeforeGlobal;
			member.Node.GlobalTransform = new Transform3D(start.Basis, start.Origin + delta);
		}
	}

	/// <summary>
	/// Every member rotates around the captured pivot in world space: its own orientation is
	/// pre-multiplied by <paramref name="rotation"/> and its centre orbits the pivot.
	/// </summary>
	public void ApplyRotate(Basis rotation)
	{
		for (int i = 0; i < _count; i++)
		{
			Member member = _pool[i];
			if (!IsLive(member)) continue;
			Transform3D start = member.BeforeGlobal;
			Basis basis = rotation * start.Basis;
			Vector3 origin = _pivot + rotation * (start.Origin - _pivot);
			member.Node.GlobalTransform = new Transform3D(basis, origin);
		}
	}

	/// <summary>
	/// The group's uniform scale: centres move away from the pivot by <paramref name="factor"/> and
	/// each member is scaled by the same factor on every axis. A sized part (cube/sphere/cylinder/
	/// wedge) multiplies its authored Size — exactly what a single-object Scale drag writes — and
	/// any other member multiplies its node scale, so no object can be distorted.
	/// </summary>
	public void ApplyUniformScale(float factor)
	{
		for (int i = 0; i < _count; i++)
		{
			Member member = _pool[i];
			if (!IsLive(member)) continue;
			Transform3D start = member.BeforeGlobal;
			Vector3 origin = _pivot + (start.Origin - _pivot) * factor;
			if (member.HasSize && member.Part != null)
			{
				member.Node.GlobalTransform = new Transform3D(start.Basis, origin);
				member.Part.SetSize(member.BeforeSize * factor);
			}
			else
			{
				Vector3 scale = GizmoMath.GetScale(start.Basis) * factor;
				member.Node.GlobalTransform = new Transform3D(GizmoMath.SetScale(start.Basis, scale), origin);
			}
		}
	}

	/// <summary>
	/// The world centre of one member: its own mesh's AABB centre for a part, the union of its
	/// descendant parts' boxes for a model group (so a group's pivot sits on what it contains), and
	/// its origin as a last resort.
	/// </summary>
	public static Vector3 MemberCentre(Node3D node)
	{
		if (node == null || !GodotObject.IsInstanceValid(node)) return Vector3.Zero;
		LotObject part = node as LotObject;
		if (part != null && part.MeshInstance != null && part.MeshInstance.Mesh != null)
		{
			return PartDragController.PartCentre(part);
		}

		bool any = false;
		Vector3 min = Vector3.Zero;
		Vector3 max = Vector3.Zero;
		UnionBounds(node, ref any, ref min, ref max);
		if (!any) return node.GlobalPosition;
		return (min + max) * 0.5f;
	}

	/// <summary>Accumulates the world AABBs of every descendant part that has a mesh.</summary>
	private static void UnionBounds(Node3D node, ref bool any, ref Vector3 min, ref Vector3 max)
	{
		LotObject part = node as LotObject;
		if (part != null && part.MeshInstance != null && part.MeshInstance.Mesh != null)
		{
			Aabb local = part.MeshInstance.Mesh.GetAabb();
			Vector3 centre = PartDragController.PartCentre(part);
			Vector3 half = FaceScaleMath.WorldHalfExtents(local.Position, local.End, part.GlobalTransform);
			if (!any)
			{
				min = centre - half;
				max = centre + half;
				any = true;
			}
			else
			{
				min = new Vector3(Mathf.Min(min.X, centre.X - half.X),
					Mathf.Min(min.Y, centre.Y - half.Y), Mathf.Min(min.Z, centre.Z - half.Z));
				max = new Vector3(Mathf.Max(max.X, centre.X + half.X),
					Mathf.Max(max.Y, centre.Y + half.Y), Mathf.Max(max.Z, centre.Z + half.Z));
			}
		}

		int children = node.GetChildCount();
		for (int i = 0; i < children; i++)
		{
			Node3D child = node.GetChild(i) as Node3D;
			if (child == null) continue;
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			UnionBounds(child, ref any, ref min, ref max);
		}
	}

	private static bool IsLive(Member member)
	{
		return member.Node != null && GodotObject.IsInstanceValid(member.Node);
	}
}
