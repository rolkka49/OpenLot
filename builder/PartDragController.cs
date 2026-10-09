using Godot;

/// <summary>
/// Click-and-hold direct manipulation of a part, active in Select mode only (the gizmo owns the
/// mouse in Translate/Rotate/Scale). The ImGui layout records mouse intent because it owns the
/// ImGui state; the actual space queries and moves run in Builder._PhysicsProcess, because Godot
/// requires raycasts and kinematic moves to happen during the physics step.
///
/// The drag plane is parallel to the surface behind the grabbed part (found by continuing the pick
/// ray past it) but raised to the height the grabbed face ends up at, so the part slides along that
/// surface at a fixed height while the grabbed point stays pinned under the cursor. Only when there
/// is no surface behind (a part floating in mid-air) does it fall back to a camera-facing plane.
///
/// Dragging reuses a single hidden CharacterBody3D "mover". The sweep is Godot's kinematic
/// MoveAndCollide, driven in a short loop that slides the leftover motion along each contact
/// plane, so a dragged part bumps and glides along other parts but can never pass through them.
/// </summary>
public sealed class PartDragController
{
	private const float PickDistance = 10000.0f;

	// MoveAndCollide resolves one contact per call; this bounds how many slide steps a single drag
	// frame may take, so an inside corner settles in a few iterations instead of looping.
	private const int MaxSlideSteps = 4;

	private readonly Builder _builder;
	private readonly BuilderScene _scene;

	private CharacterBody3D _mover;
	private CollisionShape3D _moverShape;

#if DEBUG
	// Bounds the per-drag contact logging so a long drag cannot flood the console.
	private static int _contactLogsRemaining;
#endif

	// Mouse intent recorded during the ImGui layout and consumed during the physics step.
	private bool _pressQueued;
	private bool _releaseQueued;
	private bool _additivePress;
	private bool _allowDrag;
	private bool _hasCursorRay;
	private Vector3 _cursorRayOrigin;
	private Vector3 _cursorRayDirection;

	// Active drag. The grab point is stored relative to the part's geometric centre, and the centre
	// relative to the node; both are constant for the whole drag (dragging never rotates or scales
	// the part). That lets the target be recomputed from scratch every frame, so the part neither
	// drifts nor lags, and the surface it is dragged against can change mid-drag.
	private LotObject _dragged;
	private Vector3 _dragPlaneOrigin;
	private Vector3 _dragPlaneNormal;
	private Vector3 _grabOffset;
	private Vector3 _centreLocal;

	/// <summary>Transform at drag start; the whole drag becomes one history entry on release.</summary>
	private Transform3D _dragBefore;

	// Decal face-slide drag (milestone 2.6): with the Decal tool armed, a drag on a host slides the
	// image panel across the face instead of moving the part. _decalGrabDelta keeps the point under
	// the cursor pinned to the panel, the same way a part drag keeps its grab point. The whole drag
	// is one history entry on release, exactly like a part drag.
	private bool _decalTool;
	private LotObject _draggedDecal;
	private Vector2 _decalBefore;
	private Vector2 _decalGrabDelta;
	private Vector3 _decalPlaneOrigin;
	private Vector3 _decalPlaneNormal;

	/// <summary>Property ids the decal slide writes through, so the drag uses the same single write
	/// path (and therefore the same clamping and dirty-marking) as the Inspector field.</summary>
	private const string DecalOffsetUId = "decal_offset_u";
	private const string DecalOffsetVId = "decal_offset_v";

	public PartDragController(Builder builder, BuilderScene scene)
	{
		_builder = builder;
		_scene = scene;
	}

	/// <summary>The part currently under the mouse button, or null when nothing is being dragged.</summary>
	public LotObject DraggedNode { get { return _dragged; } }

	public bool IsDragging { get { return _dragged != null || _draggedDecal != null; } }

	/// <summary>
	/// Queues a left-press. Consumed by <see cref="Tick"/> on the next physics step.
	/// <paramref name="allowDrag"/> is false outside Select mode, where the click should still
	/// select the part but the gizmo owns all dragging. <paramref name="decalTool"/> arms the decal
	/// face tool, whose press places/picks up a decal panel instead of touching parts.
	/// </summary>
	public void QueuePress(Vector3 rayOrigin, Vector3 rayDirection, bool additive, bool allowDrag, bool decalTool)
	{
		_cursorRayOrigin = rayOrigin;
		_cursorRayDirection = rayDirection;
		_hasCursorRay = true;
		_pressQueued = true;
		_additivePress = additive;
		_allowDrag = allowDrag;
		_decalTool = decalTool;

		// Create the mover now, during the ImGui layout, so its collision body and shape are
		// registered in the physics space well before the physics step consumes this press.
		if (allowDrag)
		{
			EnsureMover();
		}
	}

	/// <summary>Records the latest cursor ray so an active drag can follow it this physics step.</summary>
	public void UpdateCursor(Vector3 rayOrigin, Vector3 rayDirection)
	{
		_cursorRayOrigin = rayOrigin;
		_cursorRayDirection = rayDirection;
		_hasCursorRay = true;
	}

	/// <summary>Queues a left-release. Consumed by <see cref="Tick"/>.</summary>
	public void QueueRelease()
	{
		_releaseQueued = true;
	}

	/// <summary>
	/// Runs the pending pick and then the drag move. Called from Builder._PhysicsProcess so the
	/// physics space is in a valid state for queries and kinematic moves.
	/// </summary>
	public void Tick()
	{
		if (_pressQueued)
		{
			_pressQueued = false;
			HandlePress();
		}

		if (_dragged != null)
		{
			FollowCursor();
		}

		if (_draggedDecal != null)
		{
			FollowDecalCursor();
		}

		if (_releaseQueued)
		{
			_releaseQueued = false;
			if (_draggedDecal != null)
			{
				EndDecalDrag();
			}
			else
			{
				EndDrag();
			}
		}
	}

	private void HandlePress()
	{
		if (!_hasCursorRay)
		{
			return;
		}

		LotObject hit = Pick(_scene, _cursorRayOrigin, _cursorRayDirection, out Vector3 hitPosition, out Vector3 hitNormal);
#if DEBUG
		// One line per click: the fastest way to tell "the ray missed" apart from "the click never
		// reached the viewport" when a drag does not grab anything.
		GD.Print("[PartDragController] press ray=" + _cursorRayOrigin + " dir=" + _cursorRayDirection
			+ " hit=" + (hit != null ? hit.Name.ToString() : "none"));
#endif
		if (hit == null)
		{
			// Background click: clear the selection, exactly like the old viewport click handler.
			_builder.Selection.ClearSelection();
			return;
		}

		// The decal tool owns its press entirely: its click means "this face" (place a panel there,
		// or pick up the panel already there), never "this part".
		if (_decalTool)
		{
			HandleDecalPress(hit, hitPosition, hitNormal);
			return;
		}

		if (_additivePress)
		{
			// Ctrl/Shift toggles membership and never starts a drag.
			if (_builder.Selection.IsSelected(hit))
			{
				_builder.Selection.Deselect(hit);
			}
			else
			{
				_builder.Selection.Select(hit, true);
			}
			return;
		}

		// A plain click collapses the selection onto the grabbed part. Dragging only happens in Select
		// mode; in the other tools the gizmo is the way to move things.
		if (!_builder.Selection.IsSelected(hit))
		{
			_builder.Selection.Select(hit, false);
		}
		if (_allowDrag)
		{
			BeginDrag(hit, hitPosition);
		}
	}

	private void BeginDrag(LotObject hit, Vector3 hitPosition)
	{
		EnsureMover();
		// Captured before the initial placement below, so that placement is part of the entry too.
		_dragBefore = hit.Transform;

#if DEBUG
		_contactLogsRemaining = 12;
#endif

		Vector3 centre = PartCentre(hit);
		_grabOffset = hitPosition - centre;
		_centreLocal = centre - hit.GlobalPosition;

		if (PickBehind(_scene, _cursorRayOrigin, _cursorRayDirection, hit, out Vector3 backPoint, out Vector3 backNormal))
		{
			SetDragPlane(hit, backNormal, backPoint);
		}
		else
		{
			// Nothing behind: fall back to a camera-facing plane through the grab point.
			_dragPlaneNormal = -_scene.Freecam.GlobalTransform.Basis.Z;
			_dragPlaneOrigin = hitPosition;
		}

		// Place the part where the drag starts: resting against the surface with the grabbed point
		// under the cursor. Collide-and-slide, so this first placement cannot tunnel through
		// anything on the way down.
		Vector3 anchor = IntersectPlane(_cursorRayOrigin, _cursorRayDirection, _dragPlaneOrigin, _dragPlaneNormal);
		Vector3 startMove = (anchor - _grabOffset - _centreLocal) - hit.GlobalPosition;
		if (startMove.LengthSquared() > 1e-8f)
		{
			MovePartWithCollision(_mover, _moverShape, hit, startMove);
		}

		_dragged = hit;
		_builder.Outline.Refresh();
	}

	/// <summary>
	/// Sets the drag plane for the surface the part is being dragged against: parallel to that
	/// surface, raised to the height the grabbed face ends up at.
	/// </summary>
	private void SetDragPlane(LotObject part, Vector3 surfaceNormal, Vector3 surfacePoint)
	{
		_dragPlaneNormal = surfaceNormal;
		_dragPlaneOrigin = ComputeDragPlanePoint(part, _grabOffset, surfaceNormal, surfacePoint);
	}

	private void EndDrag()
	{
		if (_dragged == null)
		{
			return;
		}
		LotObject node = _dragged;
		_dragged = null;
		_builder.Outline.Refresh();

		// One entry for the whole drag; no-op when the part came back to where it started.
		if (!GodotObject.IsInstanceValid(node)) return;
		if (!TransformCommand.Changed(_dragBefore, node.Transform)) return;
		_builder.History.Push(new TransformCommand("Move", node, _dragBefore, node.Transform));
	}

	// --- Decal face slide (milestone 2.6) ---

	/// <summary>
	/// The decal tool's press: work out which face of the clicked part the cursor landed on, then
	/// either pick up the panel already there (and start sliding it) or place a new one at the
	/// clicked spot. The face is derived from the surface normal the raycast reported, so "select a
	/// specific face" and "drag it around" are one gesture: you click the face you mean.
	/// </summary>
	private void HandleDecalPress(LotObject hit, Vector3 hitPosition, Vector3 hitNormal)
	{
		int face = FaceUnderPoint(hit, hitPosition, hitNormal, out Vector3 visualPoint);
		LotObject existing = FindDecalAt(hit, face, visualPoint);
		if (existing != null)
		{
			_builder.Selection.Select(existing, false);
			BeginDecalDrag(existing, hit);
			return;
		}

		// Bare surface: place a panel centred on the clicked spot (the decal clamps both values, so
		// a click near the edge still lands fully on the face).
		Vector3 size = DecalMath.MeshSize(hit.MeshInstance != null ? hit.MeshInstance.Mesh : null);
		Vector2 at = DecalMath.OffsetsFromPoint(size, hit.Scale, face, visualPoint);
		_builder.Scene.InsertDecal(_builder, hit, face, at.X, at.Y);
	}

	/// <summary>Which face of the host a world point/normal pair belongs to, and where on it the
	/// point sits (in the host's visual space — the space the placement math measures in).</summary>
	private static int FaceUnderPoint(LotObject host, Vector3 worldPoint, Vector3 worldNormal, out Vector3 visualPoint)
	{
		visualPoint = ToVisualPoint(host, worldPoint);
		Vector3 localNormal = host.GlobalTransform.Basis.Inverse() * worldNormal;
		return DecalMath.FaceFromNormal(localNormal);
	}

	/// <summary>A world point in the host's visual space: its local coordinates with the host's
	/// scale applied, which is the space decal offsets and sizes are fractions of.</summary>
	private static Vector3 ToVisualPoint(LotObject host, Vector3 worldPoint)
	{
		Vector3 local = host.ToLocal(worldPoint);
		return new Vector3(local.X * host.Scale.X, local.Y * host.Scale.Y, local.Z * host.Scale.Z);
	}

	/// <summary>The topmost existing decal of <paramref name="host"/> covering a point on its face,
	/// or null for bare surface. Last child wins, matching draw order, so the panel the creator
	/// sees on top is the one a click picks up.</summary>
	private static LotObject FindDecalAt(LotObject host, int face, Vector3 visualPoint)
	{
		Vector3 size = DecalMath.MeshSize(host.MeshInstance != null ? host.MeshInstance.Mesh : null);
		LotObject found = null;
		for (int i = 0; i < host.GetChildCount(); i++)
		{
			LotObject child = host.GetChild(i) as LotObject;
			if (child == null || child.Kind != LotObjectKind.Decal) continue;
			if (DecalMath.CoversPoint(size, host.Scale, child.DecalFace, child.DecalOffsetU, child.DecalOffsetV,
				child.DecalScaleU, child.DecalScaleV, face, visualPoint))
			{
				found = child;
			}
		}
		return found;
	}

	/// <summary>
	/// Starts a slide: the drag plane is the patch's own face, and the offset between the patch's
	/// centre and the point grabbed on the face is held constant for the whole drag, so the image
	/// does not jump to the cursor.
	/// </summary>
	private void BeginDecalDrag(LotObject decal, LotObject host)
	{
		_decalBefore = new Vector2(decal.DecalOffsetU, decal.DecalOffsetV);
		_decalPlaneOrigin = decal.GlobalPosition;
		_decalPlaneNormal = decal.GlobalTransform.Basis.Z.Normalized();
		_decalGrabDelta = Vector2.Zero;

		if (TryCursorPointOnDecalPlane(out Vector3 grabbed))
		{
			Vector2 cursorOffset = FaceOffsetOfWorldPoint(host, decal.DecalFace, grabbed);
			_decalGrabDelta = _decalBefore - cursorOffset;
		}

		_draggedDecal = decal;
	}

	/// <summary>
	/// Slides the patch to follow the cursor on its face plane. The ray/plane intersection is
	/// recomputed from scratch every frame (like a part drag), so the image neither drifts nor lags,
	/// and a view that is nearly edge-on to the face simply leaves the patch alone instead of
	/// flicking it to an extreme.
	/// </summary>
	private void FollowDecalCursor()
	{
		if (_draggedDecal == null || !GodotObject.IsInstanceValid(_draggedDecal))
		{
			EndDecalDrag();
			return;
		}
		if (!_hasCursorRay)
		{
			EndDecalDrag();
			return;
		}
		LotObject host = _draggedDecal.GetParent() as LotObject;
		if (host == null)
		{
			EndDecalDrag();
			return;
		}
		if (!TryCursorPointOnDecalPlane(out Vector3 anchor))
		{
			return;
		}

		Vector2 wanted = FaceOffsetOfWorldPoint(host, _draggedDecal.DecalFace, anchor) + _decalGrabDelta;
		ApplyDecalOffsets(_builder, _draggedDecal, wanted);
	}

	/// <summary>Where the cursor ray meets the patch's face plane, in world space.</summary>
	private bool TryCursorPointOnDecalPlane(out Vector3 point)
	{
		point = Vector3.Zero;
		float denominator = _cursorRayDirection.Dot(_decalPlaneNormal);
		if (Mathf.Abs(denominator) < 1e-4f) return false;
		float along = (_decalPlaneOrigin - _cursorRayOrigin).Dot(_decalPlaneNormal) / denominator;
		if (along <= 0f) return false;
		point = _cursorRayOrigin + _cursorRayDirection * along;
		return true;
	}

	/// <summary>
	/// The fractional (u, v) on the host's face a world point maps to. The point is converted into
	/// the host's visual space, because a decal's fractions are measured there (the space the
	/// host's scaled mesh occupies) — see <see cref="DecalMath"/>.
	/// </summary>
	private static Vector2 FaceOffsetOfWorldPoint(LotObject host, int face, Vector3 worldPoint)
	{
		Vector3 size = DecalMath.MeshSize(host.MeshInstance != null ? host.MeshInstance.Mesh : null);
		return DecalMath.OffsetsFromPoint(size, host.Scale, face, ToVisualPoint(host, worldPoint));
	}

	/// <summary>
	/// Ends a slide. The whole gesture becomes one history entry (the same rule a part drag
	/// follows), pushed only when the offset actually changed. Both axes travel together, so undo
	/// of a diagonal slide is a single step.
	/// </summary>
	private void EndDecalDrag()
	{
		if (_draggedDecal == null) return;
		LotObject decal = _draggedDecal;
		_draggedDecal = null;
		if (!GodotObject.IsInstanceValid(decal)) return;

		Vector2 before = _decalBefore;
		Vector2 after = new Vector2(decal.DecalOffsetU, decal.DecalOffsetV);
		if (before == after) return;

		_builder.History.Push(new DelegateCommand("Move Decal",
			b => ApplyDecalOffsets(b, decal, after),
			b => ApplyDecalOffsets(b, decal, before)));
	}

	/// <summary>
	/// Writes both offset axes through the property descriptors — the one write path — so a drag
	/// clamps exactly like the Inspector field and a script do. The raw apply is deliberate: a drag
	/// is a live edit session, and the single history entry is pushed on release.
	/// </summary>
	private static void ApplyDecalOffsets(Builder builder, LotObject decal, Vector2 offsets)
	{
		LotPropertyDescriptor offsetU = LotPropertyRegistry.Find(DecalOffsetUId);
		LotPropertyDescriptor offsetV = LotPropertyRegistry.Find(DecalOffsetVId);
		if (offsetU != null) builder.Properties.ApplyFloat(decal, offsetU, offsets.X);
		if (offsetV != null) builder.Properties.ApplyFloat(decal, offsetV, offsets.Y);
	}

	private void FollowCursor()
	{
		if (!_hasCursorRay || !GodotObject.IsInstanceValid(_dragged))
		{
			EndDrag();
			return;
		}

		// Re-pick the surface behind the part every frame, so dragging onto a different face (a table
		// top, a wall) switches which surface the part is dragged against. When nothing is behind,
		// the last plane is kept and the part simply carries on along it.
		if (PickBehind(_scene, _cursorRayOrigin, _cursorRayDirection, _dragged, out Vector3 backPoint, out Vector3 backNormal))
		{
			SetDragPlane(_dragged, backNormal, backPoint);
		}

		// Absolute target: the part sits wherever its grabbed point is under the cursor, recomputed
		// from the ray each frame, so it can neither drift out from under the cursor nor lag behind
		// it after a clamped frame.
		Vector3 anchor = IntersectPlane(_cursorRayOrigin, _cursorRayDirection, _dragPlaneOrigin, _dragPlaneNormal);
		Vector3 target = anchor - _grabOffset - _centreLocal;
		Vector3 frameDelta = target - _dragged.GlobalPosition;

		if (frameDelta.LengthSquared() < 1e-10f)
		{
			return;
		}

		MoveDragged(frameDelta);
	}

	/// <summary>
	/// Moves the dragged part by the frame delta, resolved against every other part's collision
	/// body. Godot's kinematic move handles the bump (stop at contact) and the slide (continue
	/// along the surface), so the part cannot interpenetrate anything.
	/// </summary>
	private void MoveDragged(Vector3 frameDelta)
	{
		MovePartWithCollision(_mover, _moverShape, _dragged, frameDelta);
	}

	/// <summary>
	/// Moves <paramref name="dragged"/> by <paramref name="frameDelta"/> with Godot's kinematic
	/// collide-and-slide: the part stops at the first contact and then continues along the surface,
	/// so it can bump and slide but never interpenetrate. Internal so the DEBUG self-test can drive
	/// the same path against a live physics space.
	/// </summary>
	internal static void MovePartWithCollision(CharacterBody3D mover, CollisionShape3D moverShape, LotObject dragged, Vector3 frameDelta)
	{
		PhysicsBody3D ownBody = dragged.CollisionBody;
		CollisionShape3D ownShape = dragged.CollisionShape;
		if (ownBody == null || ownShape == null || ownShape.Shape == null)
		{
			// No collision geometry (should not happen for a LotObject): plain move, no contact.
			dragged.GlobalPosition += frameDelta;
			return;
		}

		moverShape.Shape = ownShape.Shape;
		mover.GlobalTransform = new Transform3D(dragged.GlobalTransform.Basis, dragged.GlobalPosition);
		// The physics server still holds the mover's previous transform; push the new one through
		// before sweeping, otherwise the move resolves from the wrong place (and, at the origin,
		// from inside the ground).
		mover.ForceUpdateTransform();

		// The mover starts exactly where the object is, i.e. inside the object's own collider.
		// Excluding that body is what stops the part from blocking itself.
		mover.AddCollisionExceptionWith(ownBody);
		MoveWithSlide(mover, frameDelta);
		mover.RemoveCollisionExceptionWith(ownBody);

		dragged.GlobalPosition = mover.GlobalPosition;
	}

	/// <summary>
	/// Bump-and-slide on top of Godot's kinematic sweep. MoveAndCollide stops at the first contact
	/// and reports how much motion is left over; that remainder is projected onto the contact plane
	/// and swept again. The loop is what turns "stop at the wall" into "stop, then slide along it",
	/// and it is also why a dragged part can never end up inside another part.
	/// </summary>
	private static void MoveWithSlide(CharacterBody3D mover, Vector3 frameDelta)
	{
		Vector3 remaining = frameDelta;
		for (int i = 0; i < MaxSlideSteps; i++)
		{
			KinematicCollision3D collision = mover.MoveAndCollide(remaining, false, 0.001f, false, 1);
			if (collision == null)
			{
				return;
			}

#if DEBUG
			if (_contactLogsRemaining > 0)
			{
				_contactLogsRemaining--;
				Node collider = collision.GetCollider() as Node;
				Node owner = collider != null ? collider.GetParent() : null;
				GD.Print("[PartDragController] drag contact with " + (owner != null ? owner.Name.ToString() : "?")
					+ " normal=" + collision.GetNormal() + " remainder=" + collision.GetRemainder());
			}
#endif

			remaining = collision.GetRemainder().Slide(collision.GetNormal());
			if (remaining.LengthSquared() < 1e-8f)
			{
				return;
			}
		}
	}

	/// <summary>
	/// Raycasts the lot physics space and returns the nearest part, if any. Static and internal so
	/// the DEBUG self-test can exercise the real pick path against a live physics space.
	///
	/// Uses <see cref="LotObject.EditorPickMask"/> rather than the gameplay part layer: a part with
	/// `CanCollide = false` moved to the no-collide layer must stay selectable and draggable in the
	/// editor (milestone 2.3), and this mask is the only thing that makes that work.
	/// </summary>
	internal static LotObject Pick(BuilderScene scene, Vector3 origin, Vector3 direction, out Vector3 hitPosition)
	{
		return Pick(scene, origin, direction, out hitPosition, out Vector3 _);
	}

	/// <summary>
	/// The normal-reporting overload: the decal tool needs the surface normal the ray hit, because
	/// that is what names the face a click landed on (see the face-from-normal mapping).
	/// </summary>
	internal static LotObject Pick(BuilderScene scene, Vector3 origin, Vector3 direction, out Vector3 hitPosition, out Vector3 hitNormal)
	{
		hitPosition = Vector3.Zero;
		hitNormal = Vector3.Zero;

		// Query the world the lot parts actually live in (LotRoot's resolved World3D) rather than
		// the SubViewport's own world: the SubViewport only materializes its own world once it is
		// rendered, so a headless run would otherwise have no space to query.
		World3D world = scene.LotRoot.GetWorld3D();
		PhysicsDirectSpaceState3D space = world != null ? world.DirectSpaceState : null;
		if (space == null)
		{
			return null;
		}
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * PickDistance, LotObject.EditorPickMask);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;

		Godot.Collections.Dictionary result = space.IntersectRay(query);
		if (result.Count == 0)
		{
			return null;
		}

		hitPosition = (Vector3)result["position"];
		hitNormal = result.ContainsKey("normal") ? (Vector3)result["normal"] : Vector3.Zero;
		return FindLotObject(result["collider"].As<Node>());
	}

	/// <summary>
	/// Raycasts past <paramref name="excluded"/> to find the surface behind it, which becomes the
	/// drag plane. Returns false when the ray leaves the lot without hitting anything (a part
	/// floating in mid-air), so the caller can fall back to a camera-facing plane.
	/// </summary>
	internal static bool PickBehind(BuilderScene scene, Vector3 origin, Vector3 direction, LotObject excluded, out Vector3 point, out Vector3 normal)
	{
		point = Vector3.Zero;
		normal = Vector3.Up;

		if (excluded == null || excluded.CollisionBody == null)
		{
			return false;
		}

		World3D world = scene.LotRoot.GetWorld3D();
		PhysicsDirectSpaceState3D space = world != null ? world.DirectSpaceState : null;
		if (space == null)
		{
			return false;
		}

		Godot.Collections.Array<Rid> exclude = new Godot.Collections.Array<Rid>();
		exclude.Add(excluded.CollisionBody.GetRid());

		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(origin, origin + direction * PickDistance, LotCollisionGroups.AllBits, exclude);
		query.CollideWithAreas = false;
		query.CollideWithBodies = true;

		Godot.Collections.Dictionary result = space.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}

		point = (Vector3)result["position"];
		normal = (Vector3)result["normal"];
		return true;
	}

	/// <summary>Walks up from the collider (the "Collision" body) to the owning lot object.</summary>
	private static LotObject FindLotObject(Node node)
	{
		while (node != null)
		{
			LotObject lotObject = node as LotObject;
			if (lotObject != null)
			{
				return lotObject;
			}
			node = node.GetParent();
		}
		return null;
	}

	/// <summary>Lazily creates the reusable kinematic mover inside the lot world.</summary>
	private void EnsureMover()
	{
		if (_mover != null && GodotObject.IsInstanceValid(_mover))
		{
			return;
		}
		_mover = CreateMover(_scene, out _moverShape);
	}

	/// <summary>
	/// Creates a kinematic mover in the lot world. Layer 0 so it is never a collision target or a
	/// pick hit; mask <see cref="LotCollisionGroups.AllBits"/> so it still bumps against lot parts in
	/// any group, which is the whole point (a no-collide part stays on its own reserved bit and is
	/// passed through, as before).
	/// </summary>
	internal static CharacterBody3D CreateMover(BuilderScene scene, out CollisionShape3D moverShape)
	{
		CharacterBody3D mover = new CharacterBody3D();
		mover.Name = "PartDragMover";
		mover.CollisionLayer = 0;
		mover.CollisionMask = LotCollisionGroups.AllBits;
		mover.SetMeta(LotObject.InternalChildMeta, true);

		moverShape = new CollisionShape3D();
		moverShape.Name = "Shape";
		moverShape.SetMeta(LotObject.InternalChildMeta, true);

		mover.AddChild(moverShape);
		scene.LotRoot.AddChild(mover);
		return mover;
	}

	/// <summary>
	/// Support distance from the part's centre to its extent along <paramref name="direction"/>: the
	/// box support function over the mesh AABB, so rotated and non-cubic parts measure correctly.
	/// </summary>
	internal static float SupportExtent(LotObject part, Vector3 direction)
	{
		Mesh mesh = part.MeshInstance != null ? part.MeshInstance.Mesh : null;
		if (mesh == null)
		{
			return 0f;
		}

		Vector3 half = mesh.GetAabb().Size * 0.5f;
		Basis basis = part.GlobalTransform.Basis;
		return Mathf.Abs(direction.Dot(basis.X)) * half.X
			+ Mathf.Abs(direction.Dot(basis.Y)) * half.Y
			+ Mathf.Abs(direction.Dot(basis.Z)) * half.Z;
	}

	/// <summary>The mesh AABB's centre in world space, i.e. the part's geometric centre.</summary>
	internal static Vector3 PartCentre(LotObject part)
	{
		Mesh mesh = part.MeshInstance != null ? part.MeshInstance.Mesh : null;
		if (mesh == null)
		{
			return part.GlobalPosition;
		}

		Aabb local = mesh.GetAabb();
		return part.GlobalPosition + part.GlobalTransform.Basis * (local.Position + local.Size * 0.5f);
	}

	/// <summary>
	/// How far to move the part so it actually touches the plane it is being dragged against.
	/// Returns zero when the part already touches (or is already intersecting) the plane, so an
	/// intentionally overlapping part is never pushed away.
	/// </summary>
	internal static Vector3 ComputeSnapDelta(LotObject part, Vector3 planeNormal, Vector3 planePoint)
	{
		float gap = PartCentre(part).Dot(planeNormal) - SupportExtent(part, planeNormal) - planePoint.Dot(planeNormal);
		if (gap <= 1e-4f)
		{
			return Vector3.Zero;
		}
		return -planeNormal * gap;
	}

	/// <summary>
	/// The plane the drag runs on for a given surface: parallel to it, raised so the grabbed point
	/// sits on the plane once the part rests against that surface. Returns a point on the plane; the
	/// plane normal is <paramref name="surfaceNormal"/>.
	///
	/// Passing through the grabbed point is the whole trick: intersecting the cursor ray with this
	/// plane and offsetting by the grab offset puts the part exactly where its grabbed point is under
	/// the cursor, so the part can never drift out from under it as the ray angle changes. It depends
	/// only on the surface and the part's size, not on where the part currently is, which is what
	/// makes it safe to recompute every frame as the surface changes.
	/// </summary>
	internal static Vector3 ComputeDragPlanePoint(LotObject part, Vector3 grabOffset, Vector3 surfaceNormal, Vector3 surfacePoint)
	{
		float offset = surfacePoint.Dot(surfaceNormal) + SupportExtent(part, surfaceNormal) + grabOffset.Dot(surfaceNormal);
		return surfaceNormal * offset;
	}

	private static Vector3 IntersectPlane(Vector3 rayOrigin, Vector3 rayDirection, Vector3 planeOrigin, Vector3 planeNormal)
	{
		float denominator = planeNormal.Dot(rayDirection);
		if (Mathf.Abs(denominator) < 1e-6f)
		{
			return planeOrigin;
		}
		float t = planeNormal.Dot(planeOrigin - rayOrigin) / denominator;
		return rayOrigin + rayDirection * t;
	}
}