using Godot;

/// <summary>
/// Drives the gizmo: fills Im3d's AppData from the Godot viewport, dispatches to the active tool
/// (Im3d::Gizmo, im3d.cpp:1332), runs the per-handle behaviors, draws the handles, and writes the
/// result back to the selected node's transform.
///
/// This is the port of Im3d::GizmoTranslation (im3d.cpp:896), GizmoRotation (im3d.cpp:1075) and
/// GizmoScale (im3d.cpp:1181), plus the application-side work Im3d deliberately leaves to its host.
/// One Im3d feature is intentionally not wired:
///   * Local space (_local == true) needs Im3d's matrix stack, and the toolbox has no local/global
///     toggle yet, so translation and rotation are always world-space. (Scale is inherently
///     local-axis in Im3d too, so it needs no toggle.)
/// Snapping is wired through the toolbox (ToolboxPanel): its three increments resolve once per
/// frame in FillAppData via ResolveSnap, with Alt as the hold-to-invert modifier. With the toggle
/// off the increments stay at Im3d's 0 (disabled), so an untouched toolbox behaves as before.
///
/// One part of the scale tool is OpenLot-original rather than ported (milestone 2.7): a part with a
/// mesh gets six per-face spheres (FaceScaleMath / FaceScaleBehavior) that stretch one side while
/// the opposite face stays put, and a target without a mesh keeps the Im3d axis lines.
/// </summary>
public sealed class GizmoController
{
	/// <summary>Stable id for the single builder gizmo. Must be non-zero (see MakeHandleId).</summary>
	private const uint GizmoId = 1;

	private const int HandleAxisX = 0;
	private const int HandleAxisY = 1;
	private const int HandleAxisZ = 2;
	private const int HandlePlaneYZ = 3;
	private const int HandlePlaneXZ = 4;
	private const int HandlePlaneXY = 5;
	private const int HandlePlaneView = 6;
	private const int HandleAxisAngleView = 7;
	private const int HandleUniformScale = 8;

	private readonly Builder _builder;
	private readonly GizmoAppData _appData = new GizmoAppData();
	private readonly GizmoContext _context;

	/// <summary>Last tool mode seen, so a mid-hover tool switch can drop the stale handle state.</summary>
	private GizmoMode _lastMode = GizmoMode.Select;

	// Drag history (milestone 2.4): a whole gizmo drag is one undo entry, committed on release.
	private Node3D _dragTarget;
	private Transform3D _dragBefore;
	private GizmoMode _dragMode;
	private bool _wasDragging;

	// Face scale drag state (milestone 2.7). Owned here rather than in GizmoContext so the context
	// keeps its Im3d shape; the handle ids still live in the shared state machine.
	private FaceScaleBehavior.DragState _faceDrag;

	/// <summary>
	/// True when the gizmo owns the left mouse button, i.e. a handle is hovered or being dragged.
	/// The Viewport window consults this before treating a background click as "clear selection".
	/// </summary>
	public bool WantsMouse { get; private set; }

	public GizmoController(Builder builder)
	{
		_builder = builder;
		_context = new GizmoContext(_appData);
		_builder.Selection.OnSelectionChanged += OnSelectionChanged;
	}

	/// <summary>
	/// Drops the selection-changed subscription. The Builder owns both the selection and the gizmo
	/// and frees them together, but unsubscribing explicitly keeps the event from outliving its
	/// subscriber if either lifetime is ever split out.
	/// </summary>
	public void Shutdown()
	{
		_builder.Selection.OnSelectionChanged -= OnSelectionChanged;
	}

	/// <summary>
	/// A new selection means the gizmo's stored target is gone, so any hover or drag must end with
	/// it. Without this, a handle id from the old object would still look hot/active on the new one
	/// because all three gizmos reuse the same handle indices.
	/// </summary>
	private void OnSelectionChanged()
	{
		ResetInteraction();
	}

	/// <summary>Clears hot/active/hotDepth so no handle can carry across a target or tool change.</summary>
	private void ResetInteraction()
	{
		// A drag interrupted by a selection/tool change still becomes one history entry, so the
		// move the user already made is undoable.
		if (_wasDragging) CommitGizmoDrag();
		_wasDragging = false;
		_faceDrag.Captured = false;
		_context.ResetId();
		WantsMouse = false;
	}

	/// <summary>
	/// Drops all gizmo interaction state without running the gizmo. Used while UI mode owns the
	/// viewport, so a handle that was hot when the mode switched cannot stay hot across the gap.
	/// </summary>
	public void Suspend()
	{
		ResetInteraction();
	}

	/// <summary>
	/// Runs the gizmo for one frame. Must be called from inside the Viewport ImGui window, right
	/// after the 3D image item, because it reads ImGui mouse state and draws into that window's
	/// draw list. <paramref name="imageOrigin"/> and <paramref name="imageSize"/> are the image
	/// item's screen rect.
	/// </summary>
	public void Update(Vector2 imageOrigin, Vector2 imageSize)
	{
		WantsMouse = false;
		_context.BeginFrame();

		// The tool buttons live in ToolboxPanel, which raises no event; sample the mode here and
		// treat a change like a selection change, since every gizmo reuses the same handle indices.
		GizmoMode mode = _builder.Toolbox.CurrentGizmoMode;
		if (mode != _lastMode)
		{
			ResetInteraction();
			_lastMode = mode;
		}

		Node3D target = _builder.Selection.GetFirstValid() as Node3D;
		// A decal's transform is derived from its face/offset/scale against the host, so a gizmo
		// drag would be overwritten on the next refresh. A selected decal therefore shows no
		// gizmo; it is dragged directly on the host's face instead (PartDragController).
		if (target is LotObject lotTarget && lotTarget.Kind == LotObjectKind.Decal)
		{
			target = null;
		}
		if (target == null || (mode != GizmoMode.Translate && mode != GizmoMode.Rotate && mode != GizmoMode.Scale))
		{
			// No gizmo this frame: drop hover state so a stale hot handle cannot survive the
			// selection or the tool mode changing underneath it.
			ResetInteraction();
			return;
		}

		Camera3D camera = _builder.Scene.Freecam;
		Vector2I viewportSize = _builder.Scene.LotViewport.Size;
		if (camera == null || viewportSize.X <= 0 || viewportSize.Y <= 0 || imageSize.X <= 0f || imageSize.Y <= 0f)
		{
			_context.ResetId();
			return;
		}

		FillAppData(camera, viewportSize, imageOrigin, imageSize);

		ImGuiGizmoDraw draw = new ImGuiGizmoDraw(camera, imageOrigin, imageSize, viewportSize);
		RunGizmo(target, mode, draw);
	}

	/// <summary>
	/// Im3d::Gizmo (im3d.cpp:1332) dispatch: switches on the active tool, decomposes the target's
	/// transform into the piece the tool works on, runs that gizmo, and recomposes. The port works
	/// on Godot's Transform3D directly rather than a float[4*4].
	/// </summary>
	private void RunGizmo(Node3D target, GizmoMode mode, IGizmoDraw draw)
	{
		Transform3D transform = target.GlobalTransform;
		bool changed;
		switch (mode)
		{
			case GizmoMode.Rotate:
			{
				// Im3d: Mat3 rotation = outMat4->getRotation(); GizmoRotation(...); setRotation(...)
				Basis rotation = GizmoMath.GetRotation(transform.Basis);
				changed = RunRotationGizmo(target, ref rotation, draw);
				if (changed)
				{
					target.GlobalTransform = new Transform3D(GizmoMath.SetRotation(transform.Basis, rotation), transform.Origin);
				}
				break;
			}
			case GizmoMode.Scale:
			{
				// Im3d: Vec3 scale = outMat4->getScale(); GizmoScale(...); setScale(...)
				// The face handles (milestone 2.7) also shift the origin — that is what keeps the
				// opposite face anchored — so the origin travels with the scale here.
				Vector3 scale = GizmoMath.GetScale(transform.Basis);
				Vector3 position = transform.Origin;
				changed = RunScaleGizmo(target, ref scale, ref position, draw);
				if (changed)
				{
					target.GlobalTransform = new Transform3D(GizmoMath.SetScale(transform.Basis, scale), position);
				}
				break;
			}
			default:
				changed = RunTranslationGizmo(target, draw);
				break;
		}

		// The history hook must also see the frame a drag ENDS on: the release frame reports no
		// change (the handle deactivates), and gating this on `changed` alone left the entry
		// uncommitted until some later selection/tool change — which folded a following drag into
		// the previous entry. `_wasDragging` is true exactly while an entry is open, so this call
		// runs once more on the release frame and the commit lands where it belongs.
		if (changed || _wasDragging)
		{
			// No MarkDirty during the drag: the commit below records one command when the drag ends,
			// so undoing back to the save point can report the lot clean again.
			UpdateDragHistory(target, mode);
		}
	}

	/// <summary>
	/// Opens an undo entry when a handle becomes active and closes it when the drag ends, so the
	/// whole drag is one history entry instead of one per frame.
	/// </summary>
	private void UpdateDragHistory(Node3D target, GizmoMode mode)
	{
		bool dragging = _context.ActiveId != GizmoContext.IdInvalid;
		if (dragging && !_wasDragging)
		{
			_dragTarget = target;
			_dragBefore = target.Transform;
			_dragMode = mode;
		}
		else if (!dragging && _wasDragging)
		{
			CommitGizmoDrag();
		}
		_wasDragging = dragging;
	}

	/// <summary>Records the finished gizmo drag as one history entry (a no-op if nothing moved).</summary>
	private void CommitGizmoDrag()
	{
		Node3D node = _dragTarget;
		Transform3D before = _dragBefore;
		_dragTarget = null;
		if (node == null || !GodotObject.IsInstanceValid(node)) return;
		if (!TransformCommand.Changed(before, node.Transform)) return;

		string label = _dragMode == GizmoMode.Rotate ? "Rotate"
			: (_dragMode == GizmoMode.Scale ? "Scale" : "Move");
		_builder.History.Push(new TransformCommand(label, node, before, node.Transform));
	}

	/// <summary>
	/// Resolves the per-frame snap increment from the toolbox toggle and the hold-to-invert
	/// modifier. Pure, so the interaction is verifiable without an ImGui frame (same pattern as
	/// <see cref="ViewportWindow.ResolveCameraAuthority"/>). Holding the modifier inverts whatever
	/// the toggle says: snap-on is briefly freed, snap-off is briefly snapped, so one key covers
	/// both directions. With the toggle off and nothing held the result is 0, which Im3d's Snap()
	/// treats as disabled.
	/// </summary>
	public static float ResolveSnap(bool enabled, float increment, bool overrideHeld)
	{
		return (enabled != overrideHeld) ? increment : 0.0f;
	}

	/// <summary>
	/// Fills the AppData subset the gizmo reads. Im3d leaves this to the application — its own
	/// examples do the same — so this is the port's equivalent of that host code.
	/// </summary>
	private void FillAppData(Camera3D camera, Vector2I viewportSize, Vector2 imageOrigin, Vector2 imageSize)
	{
		Vector3 forward = -camera.GlobalTransform.Basis.Z;
		_appData.WorldUp = Vector3.Up;
		_appData.ViewOrigin = camera.GlobalPosition;
		_appData.ViewDirection = forward;
		_appData.ViewportSize = new Vector2(viewportSize.X, viewportSize.Y);
		_appData.ProjectionOrtho = camera.Projection == Camera3D.ProjectionType.Orthogonal;
		_appData.ProjectionScaleY = GizmoProjection.ScaleY(camera, viewportSize);
		// Snap (§2.2): resolved from the toolbox toggle and the hold-to-invert modifier once per
		// frame, so the behaviors below read a plain increment. Rotation enters Im3d in radians
		// (see GizmoAppData.SnapRotation) while the toolbox field is in degrees.
		bool invertSnap = Input.IsPhysicalKeyPressed(Key.Alt);
		ToolboxPanel toolbox = _builder.Toolbox;
		_appData.SnapTranslation = ResolveSnap(toolbox.SnapEnabled, toolbox.MoveSnap, invertSnap);
		_appData.SnapRotation = ResolveSnap(toolbox.SnapEnabled, Mathf.DegToRad(toolbox.RotateSnapDegrees), invertSnap);
		_appData.SnapScale = ResolveSnap(toolbox.SnapEnabled, toolbox.ScaleSnap, invertSnap);
		_appData.FlipGizmoWhenBehind = true;  // Im3d default (im3d.h:543)

		// Screen mouse -> viewport pixels -> world ray (shared with the part drag controller, so both
		// agree exactly on where the cursor points).
		ViewportRay.FromScreen(camera, ImGui.GetMousePos(), imageOrigin, imageSize, viewportSize, out _appData.CursorRayOrigin, out _appData.CursorRayDirection);

		for (int i = 0; i < GizmoKeys.Count; i++)
		{
			_appData.KeyDown[i] = false;
		}
		// Im3d's Action_Select is Mouse_Left. The T/R/S/L tool hotkeys are deliberately not wired:
		// ToolboxPanel.CurrentGizmoMode is the single source of truth for the active tool.
		_appData.KeyDown[GizmoKeys.ActionSelect] = ImGui.IsMouseDown(ImGui.MouseButtonLeft);
	}

	/// <summary>
	/// Im3d::GizmoTranslation (im3d.cpp:896), world-space path only, without the culling, layer,
	/// sorting and matrix-stack plumbing the port does not have.
	///
	/// Note the Im3d detail that drawAt is captured before anything runs and every handle is both
	/// drawn and hit-tested at drawAt, while only outPos accumulates: the handles therefore stay
	/// anchored to where the object was at the start of the frame and the object catches up. That
	/// is Im3d's actual behavior, not an approximation.
	/// </summary>
	private bool RunTranslationGizmo(Node3D target, IGizmoDraw draw)
	{
		Vector3 drawAt = target.GlobalPosition;
		Vector3 outPos = drawAt;

		float worldHeight = _context.PixelsToWorldSize(drawAt, _context.GizmoHeightPixels);
		float planeSize = worldHeight * (0.5f * 0.5f);
		float planeOffset = worldHeight * 0.5f;
		float worldSize = _context.PixelsToWorldSize(drawAt, _context.GizmoSizePixels);

		Vector3[] axes =
		{
			new Vector3(1.0f, 0.0f, 0.0f), // Im3d "axisX"
			new Vector3(0.0f, 1.0f, 0.0f), // Im3d "axisY"
			new Vector3(0.0f, 0.0f, 1.0f)  // Im3d "axisZ"
		};
		Color[] axisColors = { GizmoColors.Red, GizmoColors.Green, GizmoColors.Blue };
		Vector3[] planeOrigins =
		{
			new Vector3(0.0f, planeOffset, planeOffset), // Im3d "planeYZ"
			new Vector3(planeOffset, 0.0f, planeOffset), // Im3d "planeXZ"
			new Vector3(planeOffset, planeOffset, 0.0f), // Im3d "planeXY"
			Vector3.Zero                                 // Im3d "planeV"
		};

		// invert axes if viewing from behind
		if (_appData.FlipGizmoWhenBehind)
		{
			Vector3 viewDir = _appData.ProjectionOrtho
				? -_appData.ViewDirection
				: (_appData.ViewOrigin - drawAt).Normalized();
			for (int i = 0; i < 3; i++)
			{
				if (axes[i].Dot(viewDir) < 0.0f)
				{
					axes[i] = -axes[i];
					for (int j = 0; j < 3; j++)
					{
						planeOrigins[j] = NegateComponent(planeOrigins[j], i);
					}
				}
			}
		}

		_context.BeginGizmo(GizmoId);

		// Im3d: Sphere boundingSphere(*outVec3, worldHeight * 1.5f);
		//       bool intersects = ctx.m_appHotId == ctx.m_appId || Intersects(ray, boundingSphere);
		//
		// The sphere is only a broad-phase reject. It is additionally gated on the cursor being
		// over the viewport, so a handle sitting under another panel cannot be grabbed by a click
		// meant for that panel. The gate releases while a handle is hot or active so Im3d's
		// hot-clearing path (a handle calling resetId from inside its own behavior) still runs.
		GizmoSphere boundingSphere = new GizmoSphere(drawAt, worldHeight * 1.5f);
		GizmoRay ray = new GizmoRay(_appData.CursorRayOrigin, _appData.CursorRayDirection);
		bool cursorCanPick = ImGui.IsWindowHovered()
			|| _context.ActiveId != GizmoContext.IdInvalid
			|| _context.HotId != GizmoContext.IdInvalid;
		bool intersects = cursorCanPick
			&& (_context.AppHotId == GizmoId || GizmoMath.Intersects(ray, boundingSphere));

		bool isHotHandle(int index) { return _context.HotId == GizmoContext.MakeHandleId(GizmoId, index); }

		bool ret = false;

		// Planes.
		for (int i = 0; i < 3; i++)
		{
			Vector3 planeOrigin = drawAt + planeOrigins[i];
			uint planeId = GizmoContext.MakeHandleId(GizmoId, HandlePlaneYZ + i);
			GizmoBehavior.PlaneTranslationDraw(_context, draw, planeId, planeOrigin, axes[i], planeSize, GizmoColors.Highlight);
			if (intersects)
			{
				ret |= GizmoBehavior.PlaneTranslationBehavior(_context, draw, planeId, planeOrigin, axes[i], _appData.SnapTranslation, planeSize, ref outPos);
			}
		}

		if (intersects)
		{
			// View plane: capture the normal when the handle becomes active, so a drag keeps
			// translating along the plane it started on even as the camera turns.
			uint previousActive = _context.ActiveId;
			uint viewId = GizmoContext.MakeHandleId(GizmoId, HandlePlaneView);
			Vector3 viewNormal = viewId == _context.ActiveId ? _context.StoredViewNormal : _appData.ViewDirection;
			ret |= GizmoBehavior.PlaneTranslationBehavior(_context, draw, viewId, drawAt, viewNormal, _appData.SnapTranslation, worldSize, ref outPos);
			if (previousActive != _context.ActiveId)
			{
				_context.StoredViewNormal = viewNormal;
			}

			// Highlight the two axes spanned by whichever plane is hot.
			if (isHotHandle(HandlePlaneYZ))
			{
				axisColors[1] = GizmoColors.Highlight;
				axisColors[2] = GizmoColors.Highlight;
			}
			else if (isHotHandle(HandlePlaneXZ))
			{
				axisColors[0] = GizmoColors.Highlight;
				axisColors[2] = GizmoColors.Highlight;
			}
			else if (isHotHandle(HandlePlaneXY))
			{
				axisColors[0] = GizmoColors.Highlight;
				axisColors[1] = GizmoColors.Highlight;
			}
			else if (isHotHandle(HandlePlaneView))
			{
				axisColors[0] = GizmoColors.Highlight;
				axisColors[1] = GizmoColors.Highlight;
				axisColors[2] = GizmoColors.Highlight;
			}
		}

		// View-plane handle dot. Im3d draws it outside the intersects gate.
		draw.Dot(drawAt, _context.GizmoSizePixels * 2.0f, isHotHandle(HandlePlaneView) ? GizmoColors.Highlight : GizmoColors.White);

		// Axes.
		for (int i = 0; i < 3; i++)
		{
			uint axisId = GizmoContext.MakeHandleId(GizmoId, HandleAxisX + i);
			GizmoBehavior.AxisTranslationDraw(_context, draw, axisId, drawAt, axes[i], worldHeight, worldSize, axisColors[i]);
			if (intersects)
			{
				ret |= GizmoBehavior.AxisTranslationBehavior(_context, draw, axisId, drawAt, axes[i], _appData.SnapTranslation, worldHeight, worldSize, ref outPos);
			}
		}

		_context.EndGizmo();

		WantsMouse = _context.HotId != GizmoContext.IdInvalid || _context.ActiveId != GizmoContext.IdInvalid;

		if (GizmoBehavior.DebugDraw)
		{
			DrawDebugReadout(draw, intersects, worldHeight);
		}

		if (ret)
		{
			target.GlobalPosition = outPos;
		}

		return ret;
	}

	/// <summary>
	/// Im3d::GizmoRotation (im3d.cpp:1075), world-space path only (_local == false): the axes are the
	/// fixed world X/Y/Z rings and the drag result is pre-multiplied onto the rotation captured at
	/// activation, so the rotation happens in world space. The local-space branch needs Im3d's
	/// matrix stack and the toolbox has no local/global toggle yet.
	/// </summary>
	private bool RunRotationGizmo(Node3D target, ref Basis rotation, IGizmoDraw draw)
	{
		Vector3 origin = target.GlobalPosition;
		float worldRadius = _context.PixelsToWorldSize(origin, _context.GizmoHeightPixels);
		float worldSize = _context.PixelsToWorldSize(origin, _context.GizmoSizePixels);

		uint[] axisIds =
		{
			GizmoContext.MakeHandleId(GizmoId, HandleAxisX),
			GizmoContext.MakeHandleId(GizmoId, HandleAxisY),
			GizmoContext.MakeHandleId(GizmoId, HandleAxisZ)
		};
		Vector3[] axes =
		{
			new Vector3(1.0f, 0.0f, 0.0f), // Im3d "axisX"
			new Vector3(0.0f, 1.0f, 0.0f), // Im3d "axisY"
			new Vector3(0.0f, 0.0f, 1.0f)  // Im3d "axisZ"
		};
		Color[] axisColors = { GizmoColors.Red, GizmoColors.Green, GizmoColors.Blue };
		uint viewId = GizmoContext.MakeHandleId(GizmoId, HandleAxisAngleView);

		Vector3 euler = GizmoMath.ToEulerXYZ(rotation);

		_context.BeginGizmo(GizmoId);
		uint currentId = _context.ActiveId;

		GizmoSphere boundingSphere = new GizmoSphere(origin, worldRadius);
		GizmoRay ray = new GizmoRay(_appData.CursorRayOrigin, _appData.CursorRayDirection);
		bool cursorCanPick = ImGui.IsWindowHovered()
			|| _context.ActiveId != GizmoContext.IdInvalid
			|| _context.HotId != GizmoContext.IdInvalid;
		bool intersects = cursorCanPick
			&& (_context.AppHotId == GizmoId || GizmoMath.Intersects(ray, boundingSphere));

		bool ret = false;

		for (int i = 0; i < 3; i++)
		{
			// Im3d skips the non-active rings while one axis or the view ring is being dragged.
			if (_context.ActiveId == viewId
				|| (i != 0 && _context.ActiveId == axisIds[0])
				|| (i != 1 && _context.ActiveId == axisIds[1])
				|| (i != 2 && _context.ActiveId == axisIds[2]))
			{
				continue;
			}

			uint axisId = axisIds[i];
			float angle = euler[i];
			GizmoBehavior.AxisAngleDraw(_context, draw, axisId, origin, axes[i], worldRadius * 0.9f, angle, axisColors[i], 0.0f);
			if (intersects && GizmoBehavior.AxisAngleBehavior(_context, draw, axisId, origin, axes[i], _appData.SnapRotation, worldRadius * 0.9f, worldSize, ref angle))
			{
				rotation = new Basis(axes[i], angle - _context.GizmoStateFloat) * _context.StoredRotation;
				ret = true;
			}
		}

		if (_context.ActiveId != axisIds[0] && _context.ActiveId != axisIds[1] && _context.ActiveId != axisIds[2])
		{
			Vector3 viewNormal = _appData.ViewDirection;
			float angle = 0.0f;
			if (intersects && GizmoBehavior.AxisAngleBehavior(_context, draw, viewId, origin, viewNormal, _appData.SnapRotation, worldRadius, worldSize, ref angle))
			{
				rotation = new Basis(viewNormal, angle) * _context.StoredRotation;
				ret = true;
			}
			GizmoBehavior.AxisAngleDraw(_context, draw, viewId, origin, viewNormal, worldRadius, angle, viewId == _context.ActiveId ? GizmoColors.Highlight : GizmoColors.White, 1.0f);
		}

		_context.EndGizmo();

		// Im3d: if (currentId != m_activeId) storedRotation = *outMat3;  — captured on activation.
		if (currentId != _context.ActiveId)
		{
			_context.StoredRotation = rotation;
		}

		WantsMouse = _context.HotId != GizmoContext.IdInvalid || _context.ActiveId != GizmoContext.IdInvalid;

		if (GizmoBehavior.DebugDraw)
		{
			DrawDebugReadout(draw, intersects, worldRadius);
		}

		return ret;
	}

	/// <summary>
	/// Im3d::GizmoScale (im3d.cpp:1181) for targets without a mesh, plus the OpenLot face handles
	/// (milestone 2.7) for parts. The axis handles scale along the object's own axes; the six face
	/// spheres move ONE face while the opposite face stays put, and the center dot is the uniform
	/// handle. Face handle positions are re-derived every frame from the mesh bounds, so a rotated,
	/// non-uniformly scaled part's spheres sit exactly on its visual faces.
	/// </summary>
	private bool RunScaleGizmo(Node3D target, ref Vector3 scale, ref Vector3 position, IGizmoDraw draw)
	{
		Vector3 origin = target.GlobalPosition;
		float worldHeight = _context.PixelsToWorldSize(origin, _context.GizmoHeightPixels);
		float worldSize = _context.PixelsToWorldSize(origin, _context.GizmoSizePixels);

		// A target without a mesh (a model group) has no faces to put spheres on and keeps the Im3d
		// axis lines; a decal never reaches the gizmo at all (its transform is derived — see Update).
		LotObject lotTarget = target as LotObject;
		Mesh mesh = lotTarget != null && lotTarget.MeshInstance != null ? lotTarget.MeshInstance.Mesh : null;
		bool facesAvailable = mesh != null;
		Vector3 meshMin = Vector3.Zero;
		Vector3 meshMax = Vector3.Zero;
		if (facesAvailable)
		{
			Aabb bounds = mesh.GetAabb();
			meshMin = bounds.Position;
			meshMax = bounds.End;
		}

		Basis basis = target.GlobalTransform.Basis;
		Vector3[] axes =
		{
			basis.X.Normalized(), // Im3d "axisX" — Normalize(ctx.getMatrix().getCol(0))
			basis.Y.Normalized(), // Im3d "axisY"
			basis.Z.Normalized()  // Im3d "axisZ"
		};
		Color[] axisColors = { GizmoColors.Red, GizmoColors.Green, GizmoColors.Blue };

		// invert axes if viewing from behind
		if (_appData.FlipGizmoWhenBehind)
		{
			Vector3 viewDir = _appData.ProjectionOrtho
				? _appData.ViewDirection
				: (_appData.ViewOrigin - origin).Normalized();
			for (int i = 0; i < 3; i++)
			{
				if (axes[i].Dot(viewDir) < 0.0f)
				{
					axes[i] = -axes[i];
				}
			}
		}

		_context.BeginGizmo(GizmoId);

		// Pick bound: Im3d's sphere covers only its own handles next to the origin, while the face
		// spheres sit on the part's surfaces — far outside it on a large part. The bound therefore
		// grows to the mesh's world half-extent (plus a handle's radius of slack) when faces exist.
		float pickRadius = worldHeight;
		if (facesAvailable)
		{
			Vector3 halfExtents = FaceScaleMath.WorldHalfExtents(meshMin, meshMax, target.GlobalTransform);
			pickRadius = Mathf.Max(worldHeight,
				Mathf.Max(halfExtents.X, Mathf.Max(halfExtents.Y, halfExtents.Z)) + worldSize * 4.0f);
		}
		GizmoSphere boundingSphere = new GizmoSphere(origin, pickRadius);
		GizmoRay ray = new GizmoRay(_appData.CursorRayOrigin, _appData.CursorRayDirection);
		bool cursorCanPick = ImGui.IsWindowHovered()
			|| _context.ActiveId != GizmoContext.IdInvalid
			|| _context.HotId != GizmoContext.IdInvalid;
		bool intersects = cursorCanPick
			&& (_context.AppHotId == GizmoId || GizmoMath.Intersects(ray, boundingSphere));

		bool ret = false;

		// Uniform scale: a sphere at the center, dragged on the view plane.
		uint uniformId = GizmoContext.MakeHandleId(GizmoId, HandleUniformScale);
		if (intersects)
		{
			GizmoSphere handle = new GizmoSphere(origin, _context.PixelsToWorldSize(origin, _context.GizmoSizePixels * 4.0f));
			float t0, t1;
			bool handleHit = GizmoMath.Intersect(ray, handle, out t0, out t1);
			if (uniformId == _context.ActiveId)
			{
				if (_context.IsKeyDown(GizmoKeys.ActionSelect))
				{
					GizmoPlane plane = new GizmoPlane((origin - _appData.ViewOrigin).Normalized(), origin);
					GizmoMath.Intersect(ray, plane, out t0);
					Vector3 intersection = ray.Origin + ray.Direction * t0;
					float sign = (intersection - origin).Dot(_context.StoredScalePosition - origin);
					float uniformScale = System.MathF.CopySign((intersection - origin).Length(), sign) / worldHeight;
					uniformScale = GizmoMath.Snap(uniformScale, _appData.SnapScale);
					scale = _context.GizmoStateVec3 * Mathf.Max(1.0f + System.MathF.CopySign(uniformScale, sign), 1e-4f);
					ret = true;
				}
				else
				{
					_context.MakeActive(GizmoContext.IdInvalid);
				}
			}
			else if (uniformId == _context.HotId)
			{
				if (handleHit)
				{
					if (_context.IsKeyDown(GizmoKeys.ActionSelect))
					{
						_context.MakeActive(uniformId);
						_context.GizmoStateVec3 = scale;
						_context.StoredScalePosition = ray.Origin + ray.Direction * t0;
					}
				}
				else
				{
					_context.ResetId();
				}
			}
			else
			{
				float depth = (origin - _appData.ViewOrigin).LengthSquared();
				_context.MakeHot(uniformId, depth, handleHit);
			}
		}

		bool activeOrHot = _context.ActiveId == uniformId || _context.HotId == uniformId;
		if (activeOrHot)
		{
			for (int i = 0; i < 3; i++)
			{
				axisColors[i] = GizmoColors.Highlight;
			}
			DrawCircle(draw, origin, (origin - _appData.ViewOrigin).Normalized(), worldSize * 2.0f, GizmoColors.Highlight);
		}
		draw.Dot(origin, _context.GizmoSizePixels * 2.0f, activeOrHot ? GizmoColors.Highlight : GizmoColors.White);

		if (facesAvailable)
		{
			// The six face spheres (milestone 2.7). Positions come from the live transform every
			// frame, so they follow the visual faces of a rotated, non-uniformly scaled part. The
			// Im3d "flip axes when viewing from behind" handling is deliberately NOT applied here:
			// a sphere that teleported to the far side of the part when the camera crossed the
			// horizon would be unusable, and the milestone pins that as a contract.
			for (int face = 0; face < FaceScaleMath.FaceCount; face++)
			{
				uint id = GizmoContext.MakeHandleId(GizmoId, FaceScaleMath.HandleId(face));
				Vector3 centre = FaceScaleMath.WorldFaceCenter(face, meshMin, meshMax, target.GlobalTransform);
				Vector3 normal = FaceScaleMath.WorldFaceNormal(face, target.GlobalTransform);
				float handleRadius = _context.PixelsToWorldSize(centre, _context.GizmoSizePixels * 8.0f);

				DrawFaceHandle(draw, id, centre, normal, face);

				if (intersects)
				{
					int axis = FaceScaleMath.AxisForFace(face);
					if (FaceScaleBehavior.Apply(_context, id, face, centre, normal, origin, handleRadius,
						scale[axis], FaceScaleMath.MeshHalfAlongAxis(face, meshMin, meshMax),
						_appData.SnapScale, ref _faceDrag, out float newComponent, out Vector3 newOrigin))
					{
						// Both values are absolute (measured from the press-time part), so they are
						// assigned — adding them to the live values here is what made the part drift.
						scale[axis] = newComponent;
						position = newOrigin;
						ret = true;
					}
				}
			}
		}
		else
		{
			// Axes. Im3d passes &(*outVec3)[i]; a stack Span gives the same single-component ref
			// without a per-frame heap allocation.
			System.Span<float> components = stackalloc float[3];
			components[0] = scale.X;
			components[1] = scale.Y;
			components[2] = scale.Z;
			for (int i = 0; i < 3; i++)
			{
				uint axisId = GizmoContext.MakeHandleId(GizmoId, HandleAxisX + i);
				GizmoBehavior.AxisScaleDraw(_context, draw, axisId, origin, axes[i], worldHeight, worldSize, axisColors[i]);
				if (intersects)
				{
					ret |= GizmoBehavior.AxisScaleBehavior(_context, draw, axisId, origin, axes[i], _appData.SnapScale, worldHeight, worldSize, ref components[i]);
				}
			}
			scale = new Vector3(components[0], components[1], components[2]);
		}

		_context.EndGizmo();

		WantsMouse = _context.HotId != GizmoContext.IdInvalid || _context.ActiveId != GizmoContext.IdInvalid;

		if (GizmoBehavior.DebugDraw)
		{
			DrawDebugReadout(draw, intersects, worldHeight);
		}

		return ret;
	}

	/// <summary>
	/// One face sphere (milestone 2.7): a screen-constant dot in the axis's colour, highlighted on
	/// hot/active. A sphere whose face points straight at the viewer fades toward
	/// <see cref="FaceHandleMinAlpha"/> rather than out — that keeps it from swallowing the centre
	/// uniform dot when both project onto the same spot, while staying visible enough to grab.
	/// </summary>
	private void DrawFaceHandle(IGizmoDraw draw, uint id, Vector3 centre, Vector3 normal, int face)
	{
		Vector3 viewDir = _appData.ProjectionOrtho
			? _appData.ViewDirection
			: (_appData.ViewOrigin - centre).Normalized();
		Color color = FaceHandleColor(face);

		if (_context.IsActive(id) || _context.IsHot(id))
		{
			color = GizmoColors.Highlight;
		}
		else
		{
			float aligned = GizmoMath.Remap(1.0f - Mathf.Abs(normal.Dot(viewDir)), 0.05f, 0.1f);
			color = GizmoBehavior.WithAlpha(color, Mathf.Max(FaceHandleMinAlpha, aligned));
		}

		draw.Dot(centre, _context.GizmoSizePixels * 4.0f, color);
	}

	/// <summary>Alpha floor for a face sphere facing the viewer; see <see cref="DrawFaceHandle"/>.</summary>
	private const float FaceHandleMinAlpha = 0.2f;

	/// <summary>The axis colour for a face's sphere: X red, Y green, Z blue — the same mapping the
	/// axis handles use, so the six spheres read as three axis pairs.</summary>
	private static Color FaceHandleColor(int face)
	{
		switch (FaceScaleMath.AxisForFace(face))
		{
			case 0: return GizmoColors.Red;
			case 1: return GizmoColors.Green;
			default: return GizmoColors.Blue;
		}
	}

	/// <summary>Im3d::DrawCircle — im3d.cpp:224. A LineLoop ring in the plane with the given normal.</summary>
	private void DrawCircle(IGizmoDraw draw, Vector3 origin, Vector3 normal, float radius, Color color)
	{
		int detail = _context.EstimateLevelOfDetail(origin, radius, 8, 48);
		if (detail < 3)
		{
			detail = 3;
		}

		Basis basis = GizmoMath.AlignZ(normal.Normalized(), _appData.WorldUp);
		for (int i = 0; i < detail; i++)
		{
			float rad = Mathf.Tau * ((float)i / detail);
			float radNext = Mathf.Tau * ((float)((i + 1) % detail) / detail);
			Vector3 a = origin + basis * new Vector3(Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius, 0.0f);
			Vector3 b = origin + basis * new Vector3(Mathf.Cos(radNext) * radius, Mathf.Sin(radNext) * radius, 0.0f);
			// Im3d: pushSize(2.0f) around the circle.
			draw.Line(a, b, 2.0f, color);
		}
	}

	/// <summary>
	/// V3 verification overlay: the values that actually decide hover and drag, so picking can be
	/// judged directly instead of inferred from highlight colors. Enabled with GizmoBehavior.DebugDraw.
	/// </summary>
	private void DrawDebugReadout(IGizmoDraw draw, bool intersects, float worldHeight)
	{
		string hotDepth = _context.HotId == GizmoContext.IdInvalid ? "-" : _context.HotDepth.ToString("0.###");
		string line = "gizmo  hot=" + HandleName(_context.HotId)
			+ "  active=" + HandleName(_context.ActiveId)
			+ "  hotDepth=" + hotDepth
			+ "  intersects=" + (intersects ? "true" : "false")
			+ "  worldHeight=" + worldHeight.ToString("0.####");
		draw.Text(draw.ImageOrigin + new Vector2(8f, 24f), GizmoColors.Magenta, line);
	}

	/// <summary>Handle index -> Im3d's handle name, for the debug readout.</summary>
	private static string HandleName(uint id)
	{
		if (id == GizmoContext.IdInvalid)
		{
			return "none";
		}
		int index = (int)(id & 0xFF);
		if (index >= FaceScaleMath.HandleBase && index < FaceScaleMath.HandleBase + FaceScaleMath.FaceCount)
		{
			return "face" + DecalMath.FaceNames[index - FaceScaleMath.HandleBase];
		}
		switch (index)
		{
			case HandleAxisX: return "axisX";
			case HandleAxisY: return "axisY";
			case HandleAxisZ: return "axisZ";
			case HandlePlaneYZ: return "planeYZ";
			case HandlePlaneXZ: return "planeXZ";
			case HandlePlaneXY: return "planeXY";
			case HandlePlaneView: return "planeV";
			case HandleAxisAngleView: return "axisV";
			case HandleUniformScale: return "uniform";
			default: return "unknown";
		}
	}

	/// <summary>Mirrors Im3d's planes[j].m_origin[i] = -planes[j].m_origin[i].</summary>
	private static Vector3 NegateComponent(Vector3 v, int index)
	{
		if (index == 0) return new Vector3(-v.X, v.Y, v.Z);
		if (index == 1) return new Vector3(v.X, -v.Y, v.Z);
		return new Vector3(v.X, v.Y, -v.Z);
	}
}