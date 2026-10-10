using Godot;

/// <summary>
/// The per-face scale handles (milestone 2.7): six spheres, one per face of a part's mesh bounds,
/// replacing the Im3d axis-line handles in Scale mode. Everything here is OpenLot-original — it is
/// deliberately NOT in GizmoBehavior, which stays a transliteration of Im3d's Context methods that
/// can be read side by side with im3d.cpp.
///
/// Two invariants drive the design:
///   * A handle's position is DERIVED FRESH every frame from the mesh bounds pushed through the
///     part's global transform (see <see cref="WorldFaceCenter"/>). Nothing is cached, so the
///     spheres always sit on the visual faces of a rotated, non-uniformly scaled part — the
///     "face stays fixed" bug cannot happen through a stale position.
///   * Dragging one face moves only that side. The opposite face's position is re-derived at press
///     time and held as the fixed anchor; the arithmetic in <see cref="ResolveFaceDrag"/> moves
///     the origin by exactly the half-extent change, which is what leaves the opposite face where
///     it was.
/// </summary>
public static class FaceScaleMath
{
	/// <summary>The six faces, the same vocabulary the §2.6 decal uses: the tables and the normal
	/// vectors come from <see cref="DecalMath"/>, so the two features cannot disagree about what
	/// "+X" means.</summary>
	public const int FaceCount = 6;

	/// <summary>First handle index used by the face spheres. Indices 0-8 are taken by the ported
	/// Im3d handles (axes, planes, view, uniform dot), so faces start at 10 and never collide —
	/// <see cref="GizmoContext.MakeHandleId"/> packs the index into the low byte.</summary>
	public const int HandleBase = 10;

	/// <summary>Smallest scale a face drag may reach (the milestone's chosen floor). The Im3d axis
	/// handle keeps its own 1e-3 relative floor and the uniform dot its 1e-4 multiplier floor; the
	/// difference is deliberate and the self-test pins all three numbers side by side.</summary>
	public const float MinScale = 1e-4f;

	/// <summary>Below this mesh half-extent the face has no thickness to drag (a ground plane's Y
	/// extent is exactly zero), so the drag refuses instead of dividing by nothing.</summary>
	public const float MinMeshHalf = 1e-5f;

	/// <summary>Smallest view alignment (1 - |normal . viewDir|) a captured face drag will act
	/// on. Below it the view runs within ~8 degrees of straight down the face normal, where the
	/// closest-approach measurement is ill-conditioned, so the drag holds still rather than
	/// flinging — the same refusal Im3d's axis handles carry (aligned &lt; 0.01). Decided at
	/// press and latched for the gesture, like every other piece of drag state.</summary>
	public const float MinViewAlignment = 0.01f;

	/// <summary>Handle index for a face; see <see cref="HandleBase"/>.</summary>
	public static int HandleId(int face)
	{
		return HandleBase + DecalMath.ClampFace(face);
	}

	/// <summary>0/1/2 for the X/Y/Z axis a face's normal lies on — the scale component it drives.</summary>
	public static int AxisForFace(int face)
	{
		switch (DecalMath.ClampFace(face))
		{
			case 2:
			case 3: return 0; // +X / -X
			case 4:
			case 5: return 1; // +Y / -Y
			default: return 2; // +Z / -Z
		}
	}

	/// <summary>The sign of a face's normal on its own axis: +1 for +X/+Y/+Z, -1 for their mirrors.
	/// A "-" handle grows its own side outward — its normal is the handle's direction, not the
	/// part's — which is why the drag math needs no per-side sign flip.</summary>
	public static float SignForFace(int face)
	{
		switch (DecalMath.ClampFace(face))
		{
			case 0:
			case 2:
			case 4: return 1f;
			default: return -1f;
		}
	}

	/// <summary>The centre of a face in the mesh's own frame: the bounds midpoint with the face axis
	/// pushed out to that side, so the point sits on the part's real surface.</summary>
	public static Vector3 LocalFaceCenter(int face, Vector3 meshMin, Vector3 meshMax)
	{
		Vector3 mid = (meshMin + meshMax) * 0.5f;
		int axis = AxisForFace(face);
		mid[axis] = SignForFace(face) > 0f ? meshMax[axis] : meshMin[axis];
		return mid;
	}

	/// <summary>
	/// A face's centre in world space — the one function the draw, the pick and the drag all call,
	/// which is what keeps a handle exactly on its face. <paramref name="globalTransform"/> carries
	/// the part's rotation and non-uniform scale: pushing the mesh-space centre through it is what
	/// makes a rotated part's handles follow its faces rather than its local axes.
	/// </summary>
	public static Vector3 WorldFaceCenter(int face, Vector3 meshMin, Vector3 meshMax, Transform3D globalTransform)
	{
		return globalTransform * LocalFaceCenter(face, meshMin, meshMax);
	}

	/// <summary>The face's outward normal in world space. Normalized, because pushing an axis
	/// through a non-uniform scale would otherwise stretch it.</summary>
	public static Vector3 WorldFaceNormal(int face, Transform3D globalTransform)
	{
		return (globalTransform.Basis * DecalMath.Normal(face)).Normalized();
	}

	/// <summary>
	/// The world-axis-aligned half extents of a transformed mesh bounds — the standard
	/// AABB-of-a-transformed-AABB sum. Used to size the scale tool's pick bound so a face handle on
	/// a building-sized part stays reachable (Im3d sizes its pick sphere for its own handles, which
	/// sit much closer to the origin).
	/// </summary>
	public static Vector3 WorldHalfExtents(Vector3 meshMin, Vector3 meshMax, Transform3D globalTransform)
	{
		Vector3 half = (meshMax - meshMin) * 0.5f;
		Basis basis = globalTransform.Basis;
		return new Vector3(
			Mathf.Abs(basis.X.X) * half.X + Mathf.Abs(basis.Y.X) * half.Y + Mathf.Abs(basis.Z.X) * half.Z,
			Mathf.Abs(basis.X.Y) * half.X + Mathf.Abs(basis.Y.Y) * half.Y + Mathf.Abs(basis.Z.Y) * half.Z,
			Mathf.Abs(basis.X.Z) * half.X + Mathf.Abs(basis.Y.Z) * half.Y + Mathf.Abs(basis.Z.Z) * half.Z);
	}

	/// <summary>
	/// How far a face drag's view is from head-on: 1 - |normal . viewDir|, the same measure
	/// Im3d's axis handles guard on. 0 means the view runs straight down the face normal (where
	/// the closest-approach measurement is ill-conditioned), 1 means the face is viewed edge-on.
	/// A press below <see cref="MinViewAlignment"/> still captures, but the drag refuses to move
	/// instead of jumping.
	/// </summary>
	public static float ViewAlignment(Vector3 faceNormal, Vector3 viewDirection)
	{
		return 1.0f - Mathf.Abs(faceNormal.Normalized().Dot(viewDirection.Normalized()));
	}

	/// <summary>The mesh half-extent along a face's own axis: the reference that turns a world
	/// displacement into a change of that axis's scale component.</summary>
	public static float MeshHalfAlongAxis(int face, Vector3 meshMin, Vector3 meshMax)
	{
		int axis = AxisForFace(face);
		return (meshMax[axis] - meshMin[axis]) * 0.5f;
	}

	/// <summary>
	/// The face drag's half-extent reference for a part whose dragged value is its authored
	/// **size** rather than a scale multiplier (the mesh-regeneration migration): one unit of the
	/// value is half of it, so the reference is 0.5 — times the node's own scale, because a legacy
	/// `SetScale` still multiplies the generated mesh (the editor's normal state is scale 1, the
	/// plain 0.5 case). Passing the live mesh's half here instead would make the drag arithmetic
	/// size-dependent: a 4-unit part would grow a quarter of the cursor's motion while the origin
	/// still shifted the full amount, which reads as "the part moves instead of scaling".
	/// </summary>
	public static float SizeDomainHalfPerUnit(int face, Vector3 nodeScale)
	{
		int axis = AxisForFace(face);
		return 0.5f * Mathf.Abs(nodeScale[axis]);
	}

	/// <summary>What a face drag resolved to: the new value of the dragged axis's scale component,
	/// and the world shift of the part's origin that keeps the opposite face where it was.</summary>
	public struct FaceDrag
	{
		public bool Changed;
		public float ScaleAxis;
		public Vector3 PositionShift;
	}

	/// <summary>
	/// The drag arithmetic. The cursor's movement along the face normal (snapped in world units, the
	/// way the move tool snaps) moves ONE face, and the opposite face stays put by construction:
	///   * the half-extent grows by half the drag, and the origin shifts by that same half;
	///   * opposite face = centre - half, so (c + t) - (h + t) == c - h for any t.
	/// A negative drag shrinks the part; the resulting scale is floored at <see cref="MinScale"/>,
	/// and when the floor bites the shift is recomputed from the clamped increase, so the opposite
	/// face cannot move even at the clamp.
	/// </summary>
	public static FaceDrag ResolveFaceDrag(int face, float startScaleAxis, float meshHalfAxis,
		Vector3 faceNormalWorld, Vector3 cursorDeltaWorld, float snap)
	{
		FaceDrag result = new FaceDrag();
		if (meshHalfAxis < MinMeshHalf)
		{
			// A face with no thickness (a flat mesh) has nothing to drag.
			return result;
		}

		float delta = GizmoMath.Snap(cursorDeltaWorld.Dot(faceNormalWorld), snap);
		if (Mathf.Abs(delta) < 1e-6f)
		{
			return result;
		}

		float halfIncrease = delta * 0.5f;
		float desired = startScaleAxis + halfIncrease / meshHalfAxis;
		float applied = Mathf.Max(desired, MinScale);
		if (desired < MinScale)
		{
			halfIncrease = (applied - startScaleAxis) * meshHalfAxis;
		}

		result.ScaleAxis = applied;
		result.PositionShift = faceNormalWorld * halfIncrease;
		result.Changed = !Mathf.IsEqualApprox(applied, startScaleAxis);
		return result;
	}
}

/// <summary>
/// The face spheres' per-frame logic: the same hot/active lifecycle the Im3d behaviors use, with
/// the face drag's press capture on top. The drag state is a value the controller owns and passes
/// by reference, so <see cref="GizmoContext"/> keeps its Im3d shape and nothing face-specific
/// leaks into the ported types.
///
/// Measurement: the cursor ray's closest approach to the face's own normal axis — the line through
/// the press-time face centre along the face normal — the same scheme the ported axis handles use
/// (<see cref="GizmoMath.Nearest"/>). That is what keeps the ball under the cursor: the face
/// tracks the pointer on screen at any camera angle, perspective-correct. The first cut measured
/// the cursor on a camera-facing plane and projected onto the normal, which moved the face at the
/// square of the view's sine — half the cursor's speed at 45 degrees, nothing head-on — so the
/// ball visibly lagged ("the balls don't follow the cursor"). The one place closest approach is
/// ill-conditioned, the view running down the normal, is refused explicitly at press via
/// <see cref="FaceScaleMath.MinViewAlignment"/>, so a head-on grab cannot fling the scale.
/// </summary>
public static class FaceScaleBehavior
{
	/// <summary>Everything a drag needs, captured against the press-time face.</summary>
	public struct DragState
	{
		public bool Captured;
		public bool Degenerate;     // press was inside the head-on refusal cone: no motion this drag
		public int Face;
		public Vector3 LineOrigin;  // press-time face centre: the line the closest point is measured on
		public Vector3 LineDir;     // press-time face normal (unit): the drag moves along this line
		public float StartParam;    // press-time closest-approach parameter along the line
		public Vector3 StartOrigin; // press-time part origin: the base every absolute write is from
		public float StartScaleAxis;
		public float MeshHalfAxis;
	}

	/// <summary>
	/// One frame for one face handle. Returns true only on the frames that produce a change; the
	/// controller then writes <paramref name="newScaleAxis"/> and <paramref name="newOrigin"/>
	/// together, which is what keeps the opposite face anchored.
	///
	/// Both outputs are ABSOLUTE values measured from the press-time part — the controller assigns
	/// them, it never accumulates deltas. That is load-bearing: an offset that is recomputed from
	/// the cursor's total movement since press and then added to the live origin every frame would
	/// re-apply itself each frame and the part would run away from the cursor.
	///
	/// <paramref name="faceCentre"/>, <paramref name="faceNormal"/> and <paramref name="handleRadius"/>
	/// describe the LIVE handle (they drive drawing and picking); the drag itself measures against
	/// the captured line (see the class doc), and <paramref name="targetOrigin"/> is only read
	/// while capturing.
	/// </summary>
	public static bool Apply(GizmoContext ctx, uint id, int face, Vector3 faceCentre, Vector3 faceNormal,
		Vector3 targetOrigin, float handleRadius, float scaleAxis, float meshHalfAxis, float snap,
		ref DragState state, out float newScaleAxis, out Vector3 newOrigin)
	{
		newScaleAxis = scaleAxis;
		newOrigin = targetOrigin;

		GizmoAppData appData = ctx.AppData;
		GizmoRay ray = new GizmoRay(appData.CursorRayOrigin, appData.CursorRayDirection);

		if (ctx.IsActive(id))
		{
			if (ctx.IsKeyDown(GizmoKeys.ActionSelect) && state.Captured)
			{
				// Looking (almost) straight down the face normal: closest approach is
				// ill-conditioned there, so the drag holds still rather than flinging — the
				// milestone's "no jump head-on" contract as an explicit guard.
				if (state.Degenerate)
				{
					return false;
				}

				GizmoMath.Nearest(ray, new GizmoLine(state.LineOrigin, state.LineDir), out _, out float tl);
				// The cursor's total movement along the face normal since press, grab offset
				// included (StartParam is the press-time closest point, so grabbing the ball
				// off-centre does not jump on the first frame). Parallel to the normal, so
				// ResolveFaceDrag's dot reads exactly this number back out.
				Vector3 deltaWorld = state.LineDir * (tl - state.StartParam);

				FaceScaleMath.FaceDrag drag = FaceScaleMath.ResolveFaceDrag(state.Face, state.StartScaleAxis,
					state.MeshHalfAxis, state.LineDir, deltaWorld, snap);
				if (!drag.Changed)
				{
					return false;
				}
				newScaleAxis = drag.ScaleAxis;
				// Absolute from the press-time origin, NOT a per-frame delta: the offset is already
				// the cursor's total movement since press, so adding it every frame would drift.
				newOrigin = state.StartOrigin + drag.PositionShift;
				return true;
			}

			// The button came up: the drag is over, and the controller's history commit takes it
			// from here — the same release rule every other handle follows.
			ctx.MakeActive(GizmoContext.IdInvalid);
			state.Captured = false;
			return false;
		}

		if (ctx.IsHot(id))
		{
			float t0, t1;
			if (GizmoMath.Intersect(ray, new GizmoSphere(faceCentre, handleRadius), out t0, out t1))
			{
				if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
				{
					ctx.MakeActive(id);
					state.Captured = true;
					state.Face = face;
					state.StartOrigin = targetOrigin;
					state.StartScaleAxis = scaleAxis;
					state.MeshHalfAxis = meshHalfAxis;

					Vector3 viewDir = appData.ProjectionOrtho
						? appData.ViewDirection
						: (faceCentre - appData.ViewOrigin).Normalized();
					state.Degenerate = FaceScaleMath.ViewAlignment(faceNormal, viewDir)
						< FaceScaleMath.MinViewAlignment;
					state.LineOrigin = faceCentre;
					state.LineDir = faceNormal.Normalized();
					GizmoMath.Nearest(ray, new GizmoLine(state.LineOrigin, state.LineDir), out _, out state.StartParam);
				}
			}
			else
			{
				ctx.ResetId();
			}
			return false;
		}

		float depth0, depth1;
		bool intersects = GizmoMath.Intersect(ray, new GizmoSphere(faceCentre, handleRadius), out depth0, out depth1);
		ctx.MakeHot(id, depth0, intersects);
		return false;
	}
}
