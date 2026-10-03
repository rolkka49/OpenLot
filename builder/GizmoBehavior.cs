using Godot;

/// <summary>
/// Im3d's gizmo color constants (im3d.h:423-434). Im3d packs colors as 0xRRGGBBAA.
/// </summary>
public static class GizmoColors
{
	public static readonly Color White = new Color(1.0f, 1.0f, 1.0f, 1.0f);
	public static readonly Color Red = new Color(1.0f, 0.0f, 0.0f, 1.0f);
	public static readonly Color Green = new Color(0.0f, 1.0f, 0.0f, 1.0f);
	public static readonly Color Blue = new Color(0.0f, 0.0f, 1.0f, 1.0f);
	public static readonly Color Magenta = new Color(1.0f, 0.0f, 1.0f, 1.0f);

	/// <summary>Im3d Color_Gold (0xffd700ff); Color_GizmoHighlight aliases it (im3d.cpp:85).</summary>
	public static readonly Color Highlight = new Color(1.0f, 0.8431373f, 0.0f, 1.0f);
}

/// <summary>
/// The translation gizmo's per-handle logic, ported from Im3d's Context methods. The *_Behavior
/// functions decide hover/drag and write the new position; the *_Draw functions render the handle.
/// They are separated exactly as in Im3d so the two can be read against im3d.cpp independently.
///
/// The Im3d parts with no counterpart here are omitted rather than faked: there is no matrix or
/// color stack (the caller passes world-space values and final colors), no sorting, no culling, and
/// no layer ids.
/// </summary>
public static class GizmoBehavior
{
	/// <summary>
	/// Enables the IM3D_GIZMO_DEBUG magenta hit-volume overlay (V2 of the port). Off in normal use.
	/// </summary>
	public static bool DebugDraw = false;

	// --- Axis handle ---

	/// <summary>
	/// Im3d Context::gizmoAxisTranslation_Draw — im3d.cpp:2494. <paramref name="worldSize"/> is
	/// carried for signature parity with Im3d, which also declares it without ever reading it
	/// (handle thickness comes from ctx.GizmoSizePixels instead).
	/// </summary>
	public static void AxisTranslationDraw(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 axis, float worldHeight, float worldSize, Color color)
	{
		GizmoAppData appData = ctx.AppData;
		Vector3 viewDir = appData.ProjectionOrtho
			? appData.ViewDirection
			: (appData.ViewOrigin - origin).Normalized();
		// Fade the handle out as the axis turns towards the viewer, so an axis you are looking
		// straight down does not sit on top of the gizmo origin.
		float aligned = 1.0f - Mathf.Abs(axis.Dot(viewDir));
		aligned = GizmoMath.Remap(aligned, 0.05f, 0.1f);
		Color axisColor = color;
		if (ctx.IsActive(id))
		{
			axisColor = GizmoColors.Highlight;
			// The "infinite" axis line, drawn with the original axis color — Im3d passes _color
			// here, not the highlight it just assigned to the local copy.
			draw.Line(origin - axis * 999.0f, origin + axis * 999.0f, ctx.GizmoSizePixels * 0.5f, color);
		}
		else if (ctx.IsHot(id))
		{
			axisColor = GizmoColors.Highlight;
			aligned = 1.0f;
		}
		axisColor = WithAlpha(axisColor, axisColor.A * aligned);

		// Im3d: pushColor(color); pushSize(m_gizmoSizePixels); DrawArrow(...)
		DrawArrow(ctx, draw, origin + axis * (0.2f * worldHeight), origin + axis * worldHeight, viewDir, axisColor);
	}

	/// <summary>Im3d Context::gizmoAxisTranslation_Behavior — im3d.cpp:2407.</summary>
	public static bool AxisTranslationBehavior(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 axis, float snap, float worldHeight, float worldSize, ref Vector3 outPos)
	{
		GizmoAppData appData = ctx.AppData;
		if (!ctx.IsHot(id))
		{
			// disable behavior when aligned
			Vector3 viewDir = appData.ProjectionOrtho
				? appData.ViewDirection
				: (appData.ViewOrigin - origin).Normalized();
			float aligned = 1.0f - Mathf.Abs(axis.Dot(viewDir));
			if (aligned < 0.01f)
			{
				return false;
			}
		}

		GizmoRay ray = new GizmoRay(appData.CursorRayOrigin, appData.CursorRayDirection);
		GizmoLine axisLine = new GizmoLine(origin, axis);
		GizmoCapsule axisCapsule = new GizmoCapsule(origin + axis * (0.2f * worldHeight), origin + axis * worldHeight, worldSize);

		if (DebugDraw && ctx.IsHot(id))
		{
			DrawCapsuleDebug(ctx, draw, axisCapsule);
		}

		if (ctx.IsActive(id))
		{
			if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
			{
				float tr, tl;
				GizmoMath.Nearest(ray, axisLine, out tr, out tl);
				Vector3 delta = axis * tl - ctx.GizmoStateVec3;
				// Im3d's IM3D_RELATIVE_SNAP is off by default, so the absolute branch is the live
				// one and the relative branch is deliberately not ported.
				Vector3 absPos = outPos + delta;
				float absDist = absPos.Dot(axis);
				float snappedAbs = GizmoMath.Snap(absDist, snap);
				Vector3 perp = outPos - axis * outPos.Dot(axis);
				outPos = perp + axis * snappedAbs;

				return true;
			}
			else
			{
				ctx.MakeActive(GizmoContext.IdInvalid);
			}
		}
		else if (ctx.IsHot(id))
		{
			if (GizmoMath.Intersects(ray, axisCapsule))
			{
				if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
				{
					ctx.MakeActive(id);
					float tr, tl;
					GizmoMath.Nearest(ray, axisLine, out tr, out tl);
					ctx.GizmoStateVec3 = axis * tl;
				}
			}
			else
			{
				ctx.ResetId();
			}
		}
		else
		{
			float t0, t1;
			bool intersects = GizmoMath.Intersect(ray, axisCapsule, out t0, out t1);
			ctx.MakeHot(id, t0, intersects);
		}

		return false;
	}

	// --- Axis rotation handle ---

	/// <summary>Im3d Context::gizmoAxislAngle_Behavior — im3d.cpp:2620.</summary>
	public static bool AxisAngleBehavior(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 axis, float snap, float worldRadius, float worldSize, ref float outAngle)
	{
		GizmoAppData appData = ctx.AppData;
		Vector3 viewDir = appData.ProjectionOrtho
			? appData.ViewDirection
			: (appData.ViewOrigin - origin).Normalized();
		float aligned = Mathf.Abs(axis.Dot(viewDir));
		float tr = 0.0f;
		GizmoRay ray = new GizmoRay(appData.CursorRayOrigin, appData.CursorRayDirection);
		bool intersects = false;
		Vector3 intersection;

		if (aligned < 0.05f)
		{
			// Ray-plane intersection fails at grazing angles, so Im3d picks the ring with a capsule
			// swept along the direction that is perpendicular to both the axis and the view.
			float t1;
			Vector3 capsuleAxis = viewDir.Cross(axis);
			GizmoCapsule capsule = new GizmoCapsule(origin + capsuleAxis * worldRadius, origin - capsuleAxis * worldRadius, worldSize * 0.5f);
			intersects = GizmoMath.Intersect(ray, capsule, out tr, out t1);
			intersection = ray.Origin + ray.Direction * tr;
			if (DebugDraw && ctx.IsHot(id))
			{
				DrawCapsuleDebug(ctx, draw, capsule);
			}
		}
		else
		{
			GizmoPlane plane = new GizmoPlane(axis, origin);
			intersects = GizmoMath.Intersect(ray, plane, out tr);
			intersection = ray.Origin + ray.Direction * tr;
			float dist = (intersection - origin).Length();
			intersects &= Mathf.Abs(dist - worldRadius) < (worldSize + worldSize * (1.0f - aligned) * 2.0f);
		}

		// The drag delta is measured on a view-aligned plane through the origin, not on the axis
		// plane. That is what keeps the rotation feeling smooth when the axis is nearly edge-on.
		GizmoPlane viewPlane = new GizmoPlane(viewDir, origin);
		GizmoMath.Intersect(ray, viewPlane, out tr);
		intersection = ray.Origin + ray.Direction * tr;

		if (ctx.IsActive(id))
		{
			if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
			{
				Vector3 delta = (intersection - origin).Normalized();
				float sign = ctx.GizmoStateVec3.Cross(delta).Dot(axis);
				float angle = Mathf.Acos(Mathf.Clamp(delta.Dot(ctx.GizmoStateVec3), -1.0f, 1.0f));
				// Im3d's IM3D_RELATIVE_SNAP is off by default, so the absolute branch is the live one.
				outAngle = GizmoMath.Snap(ctx.GizmoStateFloat + System.MathF.CopySign(angle, sign), snap);

				return true;
			}
			else
			{
				ctx.MakeActive(GizmoContext.IdInvalid);
			}
		}
		else if (ctx.IsHot(id))
		{
			if (intersects)
			{
				if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
				{
					ctx.MakeActive(id);
					ctx.GizmoStateVec3 = (intersection - origin).Normalized();
					ctx.GizmoStateFloat = GizmoMath.Snap(outAngle, appData.SnapRotation);
				}
			}
			else
			{
				ctx.ResetId();
			}
		}
		else
		{
			ctx.MakeHot(id, tr, intersects);
		}

		return false;
	}

	/// <summary>Im3d Context::gizmoAxislAngle_Draw — im3d.cpp:2711.</summary>
	public static void AxisAngleDraw(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 axis, float worldRadius, float angle, Color color, float minAlpha)
	{
		GizmoAppData appData = ctx.AppData;
		Vector3 viewDir = appData.ProjectionOrtho
			? appData.ViewDirection
			: (appData.ViewOrigin - origin).Normalized();
		float aligned = Mathf.Abs(axis.Dot(viewDir));

		Color ringColor = color;
		if (ctx.IsActive(id))
		{
			ringColor = GizmoColors.Highlight;
			GizmoRay ray = new GizmoRay(appData.CursorRayOrigin, appData.CursorRayDirection);
			GizmoPlane plane = new GizmoPlane(axis, origin);
			float tr;
			if (GizmoMath.Intersect(ray, plane, out tr))
			{
				Vector3 intersection = ray.Origin + ray.Direction * tr;
				Vector3 delta = (intersection - origin).Normalized();

				// Im3d: pushAlpha(Max(_minAlpha, Remap(aligned, 1.0f, 0.99f))) — the reversed range
				// is deliberate, fading the guide line in as the axis turns edge-on.
				float lineAlpha = Mathf.Max(minAlpha, GizmoMath.Remap(aligned, 1.0f, 0.99f));
				draw.Line(origin - axis * 999.0f, origin + axis * 999.0f, ctx.GizmoSizePixels * 0.5f, WithAlpha(color, color.A * lineAlpha));

				DrawArrow(ctx, draw, origin, origin + delta * worldRadius, viewDir, GizmoColors.Highlight);
				draw.Dot(origin, ctx.GizmoSizePixels * 2.0f, GizmoColors.Highlight);
			}
		}
		else if (ctx.IsHot(id))
		{
			ringColor = GizmoColors.Highlight;
		}

		aligned = Mathf.Max(GizmoMath.Remap(aligned, 0.9f, 1.0f), 0.1f);
		if (ctx.IsActive(id))
		{
			aligned = 1.0f;
		}

		// Im3d pushes LookAt(origin, origin + axis, worldUp) and emits (cos, sin, 0) * worldRadius,
		// i.e. AlignZ(axis, worldUp) applied to that local ring.
		Basis basis = GizmoMath.AlignZ(axis, appData.WorldUp);
		int detail = ctx.EstimateLevelOfDetail(origin, worldRadius, 32, 128);
		for (int i = 0; i < detail; i++)
		{
			float rad = Mathf.Tau * ((float)i / detail);
			float radNext = Mathf.Tau * ((float)((i + 1) % detail) / detail);
			Vector3 a = origin + basis * new Vector3(Mathf.Cos(rad) * worldRadius, Mathf.Sin(rad) * worldRadius, 0.0f);
			Vector3 b = origin + basis * new Vector3(Mathf.Cos(radNext) * worldRadius, Mathf.Sin(radNext) * worldRadius, 0.0f);

			// Im3d darkens the part of the ring that passes behind the origin. It does this per
			// vertex; ImGui lines are flat-shaded, so the segment takes its start vertex's value.
			float d = (origin - a).Normalized().Dot(appData.ViewDirection);
			d = Mathf.Max(minAlpha, Mathf.Max(GizmoMath.Remap(d, 0.1f, 0.2f), aligned));
			draw.Line(a, b, ctx.GizmoSizePixels, WithAlpha(ringColor, ringColor.A * d));
		}
	}

	// --- Axis scale handle ---

	/// <summary>Im3d Context::gizmoAxisScale_Behavior — im3d.cpp:2787.</summary>
	public static bool AxisScaleBehavior(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 axis, float snap, float worldHeight, float worldSize, ref float outScale)
	{
		GizmoAppData appData = ctx.AppData;
		GizmoRay ray = new GizmoRay(appData.CursorRayOrigin, appData.CursorRayDirection);
		GizmoLine axisLine = new GizmoLine(origin, axis);
		GizmoCapsule axisCapsule = new GizmoCapsule(origin + axis * (0.2f * worldHeight), origin + axis * worldHeight, worldSize);

		if (DebugDraw && ctx.IsHot(id))
		{
			DrawCapsuleDebug(ctx, draw, axisCapsule);
		}

		if (ctx.IsActive(id))
		{
			if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
			{
				float tr, tl;
				GizmoMath.Nearest(ray, axisLine, out tr, out tl);
				Vector3 intersection = axis * tl;
				Vector3 delta = intersection - ctx.GizmoStateVec3;
				float sign = delta.Dot(axis);
				// Im3d's #if 1 selects the relative-snap branch for scale (unlike translation).
				float scale = GizmoMath.Snap(delta.Length() / worldHeight, snap);
				outScale = ctx.GizmoStateFloat * Mathf.Max(1.0f + System.MathF.CopySign(scale, sign), 1e-3f);

				return true;
			}
			else
			{
				ctx.MakeActive(GizmoContext.IdInvalid);
			}
		}
		else if (ctx.IsHot(id))
		{
			if (GizmoMath.Intersects(ray, axisCapsule))
			{
				if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
				{
					ctx.MakeActive(id);
					float tr, tl;
					GizmoMath.Nearest(ray, axisLine, out tr, out tl);
					ctx.GizmoStateVec3 = axis * tl;
					ctx.GizmoStateFloat = outScale;
				}
			}
			else
			{
				ctx.ResetId();
			}
		}
		else
		{
			float t0, t1;
			bool intersects = GizmoMath.Intersect(ray, axisCapsule, out t0, out t1);
			ctx.MakeHot(id, t0, intersects);
		}

		return false;
	}

	/// <summary>
	/// Im3d Context::gizmoAxisScale_Draw — im3d.cpp:2862. <paramref name="worldSize"/> is carried
	/// for signature parity; Im3d declares it but only reads it in the debug block.
	/// </summary>
	public static void AxisScaleDraw(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 axis, float worldHeight, float worldSize, Color color)
	{
		GizmoAppData appData = ctx.AppData;
		Vector3 viewDir = appData.ProjectionOrtho
			? appData.ViewDirection
			: (appData.ViewOrigin - origin).Normalized();
		float aligned = 1.0f - Mathf.Abs(axis.Dot(viewDir));
		aligned = GizmoMath.Remap(aligned, 0.05f, 0.1f);
		Color scaleColor = color;
		if (ctx.IsActive(id))
		{
			scaleColor = GizmoColors.Highlight;
			draw.Line(origin - axis * 999.0f, origin + axis * 999.0f, ctx.GizmoSizePixels * 0.5f, color);
		}
		else if (ctx.IsHot(id))
		{
			scaleColor = GizmoColors.Highlight;
			aligned = 1.0f;
		}
		scaleColor = WithAlpha(scaleColor, scaleColor.A * aligned);

		// Im3d emits a two-vertex LineLoop (a single line) from 0.2*height to height, then a point
		// at the tip — no arrowhead, unlike the translation handle.
		Vector3 tip = origin + axis * worldHeight;
		draw.Line(origin + axis * (0.2f * worldHeight), tip, ctx.GizmoSizePixels, scaleColor);
		draw.Dot(tip, ctx.GizmoSizePixels * 2.0f, scaleColor);
	}

	// --- Shared helpers ---

	/// <summary>
	/// Im3d::DrawArrow (im3d.cpp:565) as the axis handle calls it (im3d.cpp:2521), i.e. with the
	/// default head length and thickness. Im3d emits a two-segment polyline whose second segment
	/// tapers from size*2 down to 2 pixels, and lets its shader expand it into geometry. ImGui only
	/// draws uniform-width lines, so the shaft is a line and the tapering head is a filled triangle
	/// billboarded to face the viewer. The on-screen silhouette is the same.
	/// </summary>
	private static void DrawArrow(GizmoContext ctx, IGizmoDraw draw, Vector3 start, Vector3 end, Vector3 viewDir, Color color)
	{
		Vector3 dir = end - start;
		float dirLen = dir.Length();
		if (dirLen <= 0.0f)
		{
			return;
		}
		dir = dir / dirLen;

		// Im3d: _headThickness = ctx.getSize() * 2.0f;
		//       _headLength = Min(dirlen / 2.0f, pixelsToWorldSize(end, _headThickness * 2.0f))
		float headThickness = ctx.GizmoSizePixels * 2.0f;
		float headLength = Mathf.Min(dirLen * 0.5f, ctx.PixelsToWorldSize(end, headThickness * 2.0f));
		Vector3 head = end - dir * headLength;

		draw.Line(start, head, ctx.GizmoSizePixels, color);

		// Billboard the head: take the part of the view direction perpendicular to the axis.
		// Looking straight down the axis leaves nothing to work with, so fall back to any
		// perpendicular of the axis.
		Vector3 perp = viewDir - dir * viewDir.Dot(dir);
		if (perp.LengthSquared() < 1e-12f)
		{
			perp = GizmoMath.AlignZ(dir, ctx.AppData.WorldUp).X;
		}
		perp = perp.Normalized();
		float halfWidth = ctx.PixelsToWorldSize(head, headThickness * 0.5f);
		draw.Triangle(head + perp * halfWidth, head - perp * halfWidth, end, color);
	}

	/// <summary>
	/// Rebuilds Im3d's LookAt(origin, origin + normal, worldUp) basis for the plane handles. Im3d
	/// normalizes the axis inside LookAt, so it is normalized here too.
	/// </summary>
	private static Basis PlaneBasis(Vector3 normal, Vector3 worldUp)
	{
		return GizmoMath.AlignZ(normal.Normalized(), worldUp);
	}

	/// <summary>Im3d Context::gizmoPlaneTranslation_Draw — im3d.cpp:2601.</summary>
	public static void PlaneTranslationDraw(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 normal, float worldSize, Color color)
	{
		GizmoAppData appData = ctx.AppData;
		Vector3 viewDir = appData.ProjectionOrtho
			? appData.ViewDirection
			: (appData.ViewOrigin - origin).Normalized();
		// Im3d multiplies _normal by the current matrix here because it may be in local space. The
		// port has no matrix stack, so the caller passes an already-world-space normal.
		float aligned = Mathf.Abs(normal.Dot(viewDir));
		aligned = GizmoMath.Remap(aligned, 0.1f, 0.2f);

		// Im3d: pushColor(color); pushAlpha(id == m_hotId ? 0.7f : 0.1f * getAlpha());
		// Context::vertex then multiplies each vertex color's alpha by the alpha stack, and the
		// ambient alpha is 1.0 (im3d.cpp Context ctor). The outline is drawn after popAlpha(), so
		// it keeps the un-scaled color alpha.
		float outlineAlpha = color.A * aligned;
		float fillAlpha = outlineAlpha * (ctx.IsHot(id) ? 0.7f : 0.1f);
		Color outlineColor = WithAlpha(color, outlineAlpha);
		Color fillColor = WithAlpha(color, fillAlpha);

		GetQuadCorners(origin, normal, worldSize, appData.WorldUp, out Vector3 q0, out Vector3 q1, out Vector3 q2, out Vector3 q3);
		draw.QuadFilled(q0, q1, q2, q3, fillColor);
		draw.QuadOutline(q0, q1, q2, q3, 1.0f, outlineColor);
	}

	/// <summary>Im3d Context::gizmoPlaneTranslation_Behavior — im3d.cpp:2529.</summary>
	public static bool PlaneTranslationBehavior(GizmoContext ctx, IGizmoDraw draw, uint id, Vector3 origin, Vector3 normal, float snap, float worldSize, ref Vector3 outPos)
	{
		GizmoAppData appData = ctx.AppData;
		GizmoRay ray = new GizmoRay(appData.CursorRayOrigin, appData.CursorRayDirection);
		GizmoPlane plane = new GizmoPlane(normal, origin);

		if (DebugDraw && ctx.IsHot(id))
		{
			DrawPlaneDebug(draw, origin, worldSize);
		}

		float tr;
		bool intersects = GizmoMath.Intersect(ray, plane, out tr);
		if (!intersects)
		{
			return false;
		}
		Vector3 intersection = ray.Origin + ray.Direction * tr;
		intersects &= GizmoMath.WithinBox(intersection, origin, worldSize);

		if (ctx.IsActive(id))
		{
			if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
			{
				// Im3d's IM3D_RELATIVE_SNAP is off by default, so this is the live branch.
				outPos = GizmoMath.Snap(intersection + ctx.GizmoStateVec3, plane, snap);
				return true;
			}
			else
			{
				ctx.MakeActive(GizmoContext.IdInvalid);
			}
		}
		else if (ctx.IsHot(id))
		{
			if (intersects)
			{
				if (ctx.IsKeyDown(GizmoKeys.ActionSelect))
				{
					ctx.MakeActive(id);
					ctx.GizmoStateVec3 = outPos - intersection;
				}
			}
			else
			{
				ctx.ResetId();
			}
		}
		else
		{
			ctx.MakeHot(id, tr, intersects);
		}

		return false;
	}

	/// <summary>
	/// Im3d DrawQuadFilled(origin, normal, size) corner layout (im3d.cpp:214-224): the four corners
	/// (+/-size, +/-size, 0) pushed through LookAt(origin, origin + normal, worldUp). The outline
	/// variant (im3d.cpp:188-198) visits the same four points in a different order; the square is
	/// identical, so one shared set of corners serves both.
	/// </summary>
	private static void GetQuadCorners(Vector3 origin, Vector3 normal, float size, Vector3 worldUp, out Vector3 q0, out Vector3 q1, out Vector3 q2, out Vector3 q3)
	{
		Basis basis = PlaneBasis(normal, worldUp);
		q0 = origin + basis * new Vector3(-size, -size, 0.0f);
		q1 = origin + basis * new Vector3(size, -size, 0.0f);
		q2 = origin + basis * new Vector3(size, size, 0.0f);
		q3 = origin + basis * new Vector3(-size, size, 0.0f);
	}

	/// <summary>
	/// IM3D_GIZMO_DEBUG stand-in: Im3d draws a wireframe capsule for the hot axis (im3d.cpp:2430-2436).
	/// The port draws a translucent thick line along the capsule axis with a cap dot at each end,
	/// which reads as the same hit volume without reproducing Im3d's capsule tessellation.
	/// </summary>
	private static void DrawCapsuleDebug(GizmoContext ctx, IGizmoDraw draw, GizmoCapsule capsule)
	{
		Vector3 mid = (capsule.Start + capsule.End) * 0.5f;
		float diameter = ctx.WorldSizeToPixels(mid, capsule.Radius * 2.0f);
		draw.Line(capsule.Start, capsule.End, diameter, WithAlpha(GizmoColors.Magenta, 0.35f));
		draw.Dot(capsule.Start, diameter, GizmoColors.Magenta);
		draw.Dot(capsule.End, diameter, GizmoColors.Magenta);
	}

	/// <summary>
	/// IM3D_GIZMO_DEBUG stand-in for the plane hit volume. Im3d's debug block (im3d.cpp:2534-2553)
	/// draws a quad at a fixed 2.0 world units, which does not match the region the behavior
	/// actually tests, so reproducing it would not explain a pick. The port instead outlines the
	/// real pick box — the axis-aligned world-space cube the behavior's WithinBox test uses — so
	/// the overlay answers "why did this pick?" directly.
	/// </summary>
	private static void DrawPlaneDebug(IGizmoDraw draw, Vector3 origin, float worldSize)
	{
		Vector3 a = origin - new Vector3(worldSize, worldSize, worldSize);
		Vector3 b = origin + new Vector3(worldSize, worldSize, worldSize);
		Color c = GizmoColors.Magenta;

		// Four edges along X, four along Y, four along Z.
		draw.Line(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, a.Y, a.Z), 1.0f, c);
		draw.Line(new Vector3(a.X, a.Y, b.Z), new Vector3(b.X, a.Y, b.Z), 1.0f, c);
		draw.Line(new Vector3(a.X, b.Y, a.Z), new Vector3(b.X, b.Y, a.Z), 1.0f, c);
		draw.Line(new Vector3(a.X, b.Y, b.Z), new Vector3(b.X, b.Y, b.Z), 1.0f, c);

		draw.Line(new Vector3(a.X, a.Y, a.Z), new Vector3(a.X, b.Y, a.Z), 1.0f, c);
		draw.Line(new Vector3(b.X, a.Y, a.Z), new Vector3(b.X, b.Y, a.Z), 1.0f, c);
		draw.Line(new Vector3(a.X, a.Y, b.Z), new Vector3(a.X, b.Y, b.Z), 1.0f, c);
		draw.Line(new Vector3(b.X, a.Y, b.Z), new Vector3(b.X, b.Y, b.Z), 1.0f, c);

		draw.Line(new Vector3(a.X, a.Y, a.Z), new Vector3(a.X, a.Y, b.Z), 1.0f, c);
		draw.Line(new Vector3(b.X, a.Y, a.Z), new Vector3(b.X, a.Y, b.Z), 1.0f, c);
		draw.Line(new Vector3(a.X, b.Y, a.Z), new Vector3(a.X, b.Y, b.Z), 1.0f, c);
		draw.Line(new Vector3(b.X, b.Y, a.Z), new Vector3(b.X, b.Y, b.Z), 1.0f, c);
	}

	private static Color WithAlpha(Color color, float alpha)
	{
		return new Color(color.R, color.G, color.B, alpha);
	}
}