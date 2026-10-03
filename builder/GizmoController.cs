using Godot;

/// <summary>
/// Drives the gizmo: fills Im3d's AppData from the Godot viewport, dispatches to the active tool
/// (Im3d::Gizmo, im3d.cpp:1332), runs the per-handle behaviors, draws the handles, and writes the
/// result back to the selected node's transform.
///
/// This is the port of Im3d::GizmoTranslation (im3d.cpp:896), GizmoRotation (im3d.cpp:1075) and
/// GizmoScale (im3d.cpp:1181), plus the application-side work Im3d deliberately leaves to its host.
/// Two Im3d features are intentionally not wired:
///   * Local space (_local == true) needs Im3d's matrix stack, and the toolbox has no local/global
///     toggle yet, so translation and rotation are always world-space. (Scale is inherently
///     local-axis in Im3d too, so it needs no toggle.)
///   * Snapping stays at Im3d's defaults of 0 (disabled) for all three tools; there is no snap UI.
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
				Vector3 scale = GizmoMath.GetScale(transform.Basis);
				changed = RunScaleGizmo(target, ref scale, draw);
				if (changed)
				{
					target.GlobalTransform = new Transform3D(GizmoMath.SetScale(transform.Basis, scale), transform.Origin);
				}
				break;
			}
			default:
				changed = RunTranslationGizmo(target, draw);
				break;
		}

		if (changed)
		{
			_builder.MarkDirty();
		}
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
		_appData.SnapTranslation = 0.0f;      // Im3d default (im3d.h:540) — disabled
		_appData.SnapRotation = 0.0f;         // Im3d default (im3d.h:541) — disabled
		_appData.SnapScale = 0.0f;            // Im3d default (im3d.h:542) — disabled
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
	/// Im3d::GizmoScale (im3d.cpp:1181). The axes are the object's own normalized columns, so the
	/// scale is applied along its local axes; Im3d has no world/local toggle for scale either. The
	/// center dot is the uniform handle, which scales all three components together.
	/// </summary>
	private bool RunScaleGizmo(Node3D target, ref Vector3 scale, IGizmoDraw draw)
	{
		Vector3 origin = target.GlobalPosition;
		float worldHeight = _context.PixelsToWorldSize(origin, _context.GizmoHeightPixels);
		float worldSize = _context.PixelsToWorldSize(origin, _context.GizmoSizePixels);

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

		GizmoSphere boundingSphere = new GizmoSphere(origin, worldHeight);
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

		_context.EndGizmo();

		WantsMouse = _context.HotId != GizmoContext.IdInvalid || _context.ActiveId != GizmoContext.IdInvalid;

		if (GizmoBehavior.DebugDraw)
		{
			DrawDebugReadout(draw, intersects, worldHeight);
		}

		return ret;
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
		switch ((int)(id & 0xFF))
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