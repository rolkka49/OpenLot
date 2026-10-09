using System.Collections.Generic;
using Godot;

/// <summary>
/// V1 verification for the Im3d translation-gizmo port: hand-computed cases checked against the
/// ported math and the hot/active state machine. The project has no test framework (Openlot.csproj
/// is just Godot.NET.Sdk + NLua), so this runs from BuilderScene._Ready() under #if DEBUG and
/// prints pass/fail lines to the Godot console. Every expected value below is derived by hand from
/// the Im3d source, not from this port, so a mismatch means the port drifted.
/// </summary>
public static class GizmoSelfTest
{
	private const float Tolerance = 1e-4f;
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestRemap();
		TestSnap();
		TestSnapResolve();
		TestPlane();
		TestSphere();
		TestNearest();
		TestAlignZ();
		TestWithinBox();
		TestPixelConversions();
		TestStateMachine();
		TestAxisDrag();
		TestMatrixHelpers();
		TestRotationDrag();
		TestScaleDrag();
		TestFaceScale();
		TestFaceScaleDrag();
		TestSelectionBulk();
		TestMarqueeRect();
		TestUiRectClamp();
		TestUiHandleLayout();
		TestUiResize();
		TestUiSnap();

		GD.Print("[GizmoSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	// --- Assertions ---

	private static void Check(string name, bool condition)
	{
		_checks++;
		if (condition)
		{
			GD.Print("[GizmoSelfTest] PASS  " + name);
		}
		else
		{
			_failures++;
			GD.PrintErr("[GizmoSelfTest] FAIL  " + name);
		}
	}

	private static void CheckNear(string name, float actual, float expected)
	{
		Check(name + " (got " + actual.ToString("0.######") + ", want " + expected.ToString("0.######") + ")",
			Mathf.Abs(actual - expected) <= Tolerance);
	}

	private static void CheckVec(string name, Vector3 actual, Vector3 expected)
	{
		Check(name + " (got " + actual + ", want " + expected + ")",
			Mathf.Abs(actual.X - expected.X) <= Tolerance
			&& Mathf.Abs(actual.Y - expected.Y) <= Tolerance
			&& Mathf.Abs(actual.Z - expected.Z) <= Tolerance);
	}

	// --- Math ---

	private static void TestRemap()
	{
		// Remap(x, 0.05, 0.1): 1/(end-start) = 20, -start/(end-start) = -1
		CheckNear("Remap below range clamps to 0", GizmoMath.Remap(0.0f, 0.05f, 0.1f), 0.0f);
		CheckNear("Remap at start is 0", GizmoMath.Remap(0.05f, 0.05f, 0.1f), 0.0f);
		CheckNear("Remap midpoint is 0.5", GizmoMath.Remap(0.075f, 0.05f, 0.1f), 0.5f);
		CheckNear("Remap at end is 1", GizmoMath.Remap(0.1f, 0.05f, 0.1f), 1.0f);
		CheckNear("Remap above range clamps to 1", GizmoMath.Remap(2.0f, 0.05f, 0.1f), 1.0f);
	}

	private static void TestSnap()
	{
		// Im3d floors rather than rounds — Snap(-0.5, 1) is -1, not 0.
		CheckNear("Snap floors 2.7 to 2", GizmoMath.Snap(2.7f, 1.0f), 2.0f);
		CheckNear("Snap floors -0.5 to -1", GizmoMath.Snap(-0.5f, 1.0f), -1.0f);
		CheckNear("Snap floors 3.7 by 0.5 to 3.5", GizmoMath.Snap(3.7f, 0.5f), 3.5f);
		CheckNear("Snap with 0 is disabled", GizmoMath.Snap(5.25f, 0.0f), 5.25f);

		// Snap(Vector3, Plane, snap) decomposes along the plane's own tangents and floors each
		// length. For the y=0 plane, AlignZ's fallback gives tangents +Z and +X, so (1.9, 0.4, 2.5)
		// becomes floor(2.5)=2 on Z and floor(1.9)=1 on X.
		GizmoPlane groundPlane = new GizmoPlane(new Vector3(0f, 1f, 0f), Vector3.Zero);
		CheckVec("Snap(Vector3,Plane) floors each tangent length",
			GizmoMath.Snap(new Vector3(1.9f, 0.4f, 2.5f), groundPlane, 1.0f), new Vector3(1f, 0f, 2f));
	}

	/// <summary>
	/// The widget-to-gizmo wire for GizmoController.ResolveSnap (§2.2): the toolbox toggle, the
	/// hold-to-invert modifier, and the degrees-to-radians conversion the rotation gizmo needs.
	/// </summary>
	private static void TestSnapResolve()
	{
		// Toggle on, no modifier held: the increment reaches the gizmo unchanged.
		CheckNear("Snap resolve passes an enabled increment",
			GizmoController.ResolveSnap(true, 0.5f, false), 0.5f);

		// Toggle off, no modifier: 0, which Im3d's Snap() treats as disabled.
		CheckNear("Snap resolve is 0 when disabled",
			GizmoController.ResolveSnap(false, 0.5f, false), 0.0f);

		// Hold-to-invert: the modifier flips whichever the toggle says.
		CheckNear("Snap resolve lets the modifier free an enabled snap",
			GizmoController.ResolveSnap(true, 0.5f, true), 0.0f);
		CheckNear("Snap resolve lets the modifier enable a disabled snap",
			GizmoController.ResolveSnap(false, 0.5f, true), 0.5f);

		// The toolbox stores rotation in degrees; the gizmo works in radians (15 deg = pi/12).
		CheckNear("Snap resolve converts 15 degrees to radians",
			Mathf.DegToRad(15.0f), 0.2617994f);
	}

	private static void TestPlane()
	{
		// Ray from (0,5,0) straight down; plane y=0.
		GizmoRay ray = new GizmoRay(new Vector3(0f, 5f, 0f), new Vector3(0f, -1f, 0f));
		GizmoPlane plane = new GizmoPlane(new Vector3(0f, 1f, 0f), Vector3.Zero);
		Check("Plane ray faces the plane", GizmoMath.Intersects(ray, plane));
		float t;
		Check("Plane intersect hits", GizmoMath.Intersect(ray, plane, out t));
		CheckNear("Plane intersect t is 5", t, 5.0f);

		// Same ray started below the plane pointing further down: the hit is behind the ray.
		GizmoRay away = new GizmoRay(new Vector3(0f, -5f, 0f), new Vector3(0f, -1f, 0f));
		float t2;
		Check("Plane intersect behind the ray is false", !GizmoMath.Intersect(away, plane, out t2));
	}

	private static void TestSphere()
	{
		GizmoSphere sphere = new GizmoSphere(Vector3.Zero, 1.0f);

		GizmoRay hit = new GizmoRay(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, -1f));
		Check("Sphere ray hits", GizmoMath.Intersects(hit, sphere));
		float t0, t1;
		Check("Sphere intersect hits", GizmoMath.Intersect(hit, sphere, out t0, out t1));
		CheckNear("Sphere entry t is 9", t0, 9.0f);
		CheckNear("Sphere exit t is 11", t1, 11.0f);

		// Pointing away from a sphere that is entirely in front.
		GizmoRay miss = new GizmoRay(new Vector3(0f, 0f, 10f), new Vector3(0f, 0f, 1f));
		Check("Sphere ray pointing away misses", !GizmoMath.Intersects(miss, sphere));

		// Origin inside the sphere but facing away: Im3d's Intersects says yes, Intersect says no
		// (it rejects q < 0 outright). Preserved as-is — this is a quirk, not a bug to fix.
		GizmoRay inside = new GizmoRay(new Vector3(0f, 0f, 0.5f), new Vector3(0f, 0f, 1f));
		Check("Sphere from inside intersects", GizmoMath.Intersects(inside, sphere));
		float t3, t4;
		Check("Sphere Intersect rejects q < 0 even from inside", !GizmoMath.Intersect(inside, sphere, out t3, out t4));
	}

	private static void TestNearest()
	{
		// Ray along +X through the origin; line through (5,1,0) along +Y. Hand-derived: they meet
		// at (5,0,0), which is ray t = 5 and line t = -1.
		GizmoRay ray = new GizmoRay(Vector3.Zero, new Vector3(1f, 0f, 0f));
		GizmoLine line = new GizmoLine(new Vector3(5f, 1f, 0f), new Vector3(0f, 1f, 0f));
		float tr, tl;
		GizmoMath.Nearest(ray, line, out tr, out tl);
		CheckNear("Nearest(Ray,Line) ray param is 5", tr, 5.0f);
		CheckNear("Nearest(Ray,Line) line param is -1", tl, -1.0f);

		// Segment (5,1,0)-(5,3,0): the closest point on it to the ray is its start, (5,1,0).
		GizmoLineSegment segment = new GizmoLineSegment(new Vector3(5f, 1f, 0f), new Vector3(5f, 3f, 0f));
		float tr2;
		Vector3 closest = GizmoMath.Nearest(ray, segment, out tr2);
		CheckVec("Nearest(Ray,LineSegment) clamps to the segment start", closest, new Vector3(5f, 1f, 0f));
		CheckNear("Nearest(Ray,LineSegment) ray param is 5", tr2, 5.0f);
		CheckNear("DistanceSquared to that segment is 1", GizmoMath.DistanceSquared(ray, segment), 1.0f);

		// A segment that crosses the ray's axis: it passes through (3,0,0), so the distance is 0.
		GizmoLineSegment crossing = new GizmoLineSegment(new Vector3(3f, 0f, 3f), new Vector3(3f, 0f, -3f));
		CheckNear("DistanceSquared is 0 for a segment crossing the ray axis", GizmoMath.DistanceSquared(ray, crossing), 0.0f);

		// A genuinely parallel segment (denominator branch): along +X like the ray, offset to y=1.
		// Hand-derived: q = 16, r = 4, so denom = q - r*r = 0, and the closest point is (0,1,0)
		// at ray t = 0, giving a distance of 1.
		GizmoLineSegment parallel = new GizmoLineSegment(new Vector3(0f, 1f, 0f), new Vector3(4f, 1f, 0f));
		CheckNear("DistanceSquared handles the parallel branch", GizmoMath.DistanceSquared(ray, parallel), 1.0f);
	}

	private static void TestAlignZ()
	{
		// Aligning to +Z with an up of +Y is the identity basis.
		Basis identity = GizmoMath.AlignZ(new Vector3(0f, 0f, 1f), Vector3.Up);
		CheckVec("AlignZ(+Z, up=+Y) has Z column +Z", identity.Z, new Vector3(0f, 0f, 1f));
		CheckVec("AlignZ(+Z, up=+Y) has Y column +Y", identity.Y, new Vector3(0f, 1f, 0f));
		CheckVec("AlignZ(+Z, up=+Y) has X column +X", identity.X, new Vector3(1f, 0f, 0f));

		// Up parallel to the axis: Im3d falls back to (1,0,0), giving y=(1,0,0) and x=y x axis=(0,0,1).
		Basis fallback = GizmoMath.AlignZ(new Vector3(0f, 1f, 0f), Vector3.Up);
		CheckVec("AlignZ(+Y, up=+Y) falls back and keeps Z on the axis", fallback.Z, new Vector3(0f, 1f, 0f));
		CheckVec("AlignZ(+Y, up=+Y) X column is +Z", fallback.X, new Vector3(0f, 0f, 1f));
		CheckVec("AlignZ(+Y, up=+Y) Y column is +X", fallback.Y, new Vector3(1f, 0f, 0f));

		// An arbitrary axis: Z must land exactly on the axis and the basis must stay orthonormal.
		Vector3 axis = new Vector3(1f, 2f, 3f).Normalized();
		Basis basis = GizmoMath.AlignZ(axis, Vector3.Up);
		CheckVec("AlignZ puts the axis on the Z column", basis.Z, axis);
		CheckNear("AlignZ X column is unit length", basis.X.Length(), 1.0f);
		CheckNear("AlignZ Y column is unit length", basis.Y.Length(), 1.0f);
		CheckNear("AlignZ X is perpendicular to Z", basis.X.Dot(basis.Z), 0.0f);
		CheckNear("AlignZ Y is perpendicular to Z", basis.Y.Dot(basis.Z), 0.0f);
	}

	private static void TestWithinBox()
	{
		Check("WithinBox accepts a point inside", GizmoMath.WithinBox(new Vector3(0.5f, -0.5f, 0.9f), Vector3.Zero, 1.0f));
		Check("WithinBox rejects a point outside on one axis", !GizmoMath.WithinBox(new Vector3(1.5f, 0f, 0f), Vector3.Zero, 1.0f));
		Check("WithinBox is measured against the box centre", GizmoMath.WithinBox(new Vector3(10.5f, 0f, 0f), new Vector3(10f, 0f, 0f), 1.0f));
	}

	private static void TestPixelConversions()
	{
		GizmoAppData appData = MakeTestAppData();
		GizmoContext ctx = new GizmoContext(appData);

		// A 64px handle 10 units away spans 64/540 of the viewport's world height at that distance,
		// and for a 75-degree vertical FOV that height is 2*d*tan(fov/2). Expressing the expectation
		// geometrically (rather than reusing ProjectionScaleY) is what catches the tan(fov) vs
		// 2*tan(fov/2) mistake, which would make every handle twice its intended size.
		float viewportWorldHeight = 2.0f * 10.0f * Mathf.Tan(Mathf.DegToRad(75.0f) * 0.5f);
		CheckNear("PixelsToWorldSize(64px @ d=10, fov=75) is 64/540 of the world height",
			ctx.PixelsToWorldSize(new Vector3(0f, 0f, -10f), 64.0f),
			viewportWorldHeight * (64.0f / 540.0f));

		float world = ctx.PixelsToWorldSize(new Vector3(0f, 0f, -10f), 5.0f);
		CheckNear("WorldSizeToPixels inverts PixelsToWorldSize",
			ctx.WorldSizeToPixels(new Vector3(0f, 0f, -10f), world), 5.0f);

		// Ortho has no distance falloff.
		appData.ProjectionOrtho = true;
		appData.ProjectionScaleY = 20.0f; // Camera3D.Size in the ortho case
		float near = ctx.PixelsToWorldSize(new Vector3(0f, 0f, -1f), 64.0f);
		float far = ctx.PixelsToWorldSize(new Vector3(0f, 0f, -100f), 64.0f);
		CheckNear("Ortho world size ignores distance", near, far);
		CheckNear("Ortho world size is size * pixels / viewport height", near, 20.0f * (64.0f / 540.0f));
	}

	/// <summary>Camera at the origin looking down -Z with Godot's default 75-degree vertical FOV.</summary>
	private static GizmoAppData MakeTestAppData()
	{
		GizmoAppData appData = new GizmoAppData();
		appData.WorldUp = Vector3.Up;
		appData.ViewOrigin = Vector3.Zero;
		appData.ViewDirection = new Vector3(0f, 0f, -1f);
		appData.ViewportSize = new Vector2(960f, 540f);
		appData.ProjectionOrtho = false;
		appData.ProjectionScaleY = 2.0f * Mathf.Tan(Mathf.DegToRad(75.0f) * 0.5f);
		return appData;
	}

	private static void TestStateMachine()
	{
		GizmoAppData appData = MakeTestAppData();
		GizmoContext ctx = new GizmoContext(appData);
		uint idA = GizmoContext.MakeHandleId(1, 0);
		uint idB = GizmoContext.MakeHandleId(1, 1);

		Check("Handle ids are distinct and non-zero",
			idA != idB && idA != GizmoContext.IdInvalid && idB != GizmoContext.IdInvalid);
		Check("Handle ids are stable across calls", GizmoContext.MakeHandleId(1, 0) == idA);
		Check("Handle ids differ between gizmos", GizmoContext.MakeHandleId(2, 0) != idA);

		ctx.BeginGizmo(1);

		// Hotness is sticky across frames; only a strictly smaller depth steals it. This is what
		// makes hover stable rather than flickering, so it is the most important thing to pin down.
		Check("MakeHot takes hotness", ctx.MakeHot(idA, 5.0f, true));
		Check("HotId is the handle that took it", ctx.HotId == idA);
		ctx.BeginFrame();
		Check("Hot survives a frame with no input", ctx.HotId == idA);
		Check("A farther handle cannot steal hotness", !ctx.MakeHot(idB, 10.0f, true));
		Check("HotId is unchanged after the failed steal", ctx.HotId == idA);
		Check("A nearer handle steals hotness", ctx.MakeHot(idB, 3.0f, true));
		Check("HotId is the nearer handle", ctx.HotId == idB);
		Check("A non-intersecting handle cannot take hotness", !ctx.MakeHot(idA, 1.0f, false));
		CheckNear("HotDepth tracks the winner", ctx.HotDepth, 3.0f);

		// Im3d refuses makeHot while the select key is held, so hover must be established on an
		// earlier frame than the press. That is why a handle cannot be grabbed by its first click.
		ctx.ResetId();
		appData.KeyDown[GizmoKeys.ActionSelect] = true;
		ctx.BeginFrame();
		Check("MakeHot is refused while the select key is held", !ctx.MakeHot(idA, 1.0f, true));
		Check("HotId is still invalid", ctx.HotId == GizmoContext.IdInvalid);
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		Check("MakeHot works again once the key is released", ctx.MakeHot(idA, 1.0f, true));

		// While a handle is active nothing else can become hot.
		ctx.MakeActive(idA);
		Check("ActiveId is set", ctx.ActiveId == idA);
		Check("MakeHot is refused while a handle is active", !ctx.MakeHot(idB, 0.5f, true));
		Check("AppIdActivated reports the owning gizmo", ctx.AppIdActivated == 1);
		ctx.BeginFrame();
		Check("AppIdActivated is cleared by BeginFrame", ctx.AppIdActivated == GizmoContext.IdInvalid);

		ctx.ResetId();
		Check("ResetId clears hot", ctx.HotId == GizmoContext.IdInvalid);
		Check("ResetId clears active", ctx.ActiveId == GizmoContext.IdInvalid);
		Check("ResetId restores HotDepth to float.MaxValue", ctx.HotDepth == float.MaxValue);

		// The prev-frame key snapshot that WasKeyPressed reads (Im3d's wasKeyPressed). Nothing in
		// the translation path calls it, because the T/R/S/L tool hotkeys are deliberately left to
		// ToolboxPanel, but BeginFrame's rising-edge detection is pinned down here so the ported
		// delta logic is verified for whenever those hotkeys are wired up.
		appData.KeyDown[GizmoKeys.ActionGizmoTranslation] = false;
		ctx.BeginFrame();
		Check("WasKeyPressed is false while the key is up", !ctx.WasKeyPressed(GizmoKeys.ActionGizmoTranslation));
		appData.KeyDown[GizmoKeys.ActionGizmoTranslation] = true;
		ctx.BeginFrame();
		Check("WasKeyPressed is true on the rising edge", ctx.WasKeyPressed(GizmoKeys.ActionGizmoTranslation));
		ctx.BeginFrame();
		Check("WasKeyPressed is false once the key is merely held", !ctx.WasKeyPressed(GizmoKeys.ActionGizmoTranslation));
		appData.KeyDown[GizmoKeys.ActionGizmoTranslation] = false;
		ctx.BeginFrame();
		Check("WasKeyPressed is false on the falling edge", !ctx.WasKeyPressed(GizmoKeys.ActionGizmoTranslation));

		ctx.EndGizmo();
	}

	/// <summary>
	/// End-to-end exercise of the X-axis drag across four frames: hover, press, drag, release.
	/// Camera at the origin, object at (0,0,-10), cursor rays straight down -Z through the X axis.
	/// All expected positions are derived by hand from Im3d's Nearest + snap math.
	/// </summary>
	private static void TestAxisDrag()
	{
		GizmoAppData appData = MakeTestAppData();
		GizmoContext ctx = new GizmoContext(appData);
		GizmoSelfTestDraw draw = new GizmoSelfTestDraw();

		Vector3 outPos = new Vector3(0f, 0f, -10f);
		float worldHeight = ctx.PixelsToWorldSize(outPos, ctx.GizmoHeightPixels);
		float worldSize = ctx.PixelsToWorldSize(outPos, ctx.GizmoSizePixels);
		Vector3 axis = new Vector3(1f, 0f, 0f);
		uint axisId = GizmoContext.MakeHandleId(1, 0);

		ctx.BeginGizmo(1);

		// Frame 1 — hover. The ray through (1,0,0) runs along the capsule axis, so it hits.
		appData.CursorRayOrigin = new Vector3(1f, 0f, 0f);
		appData.CursorRayDirection = new Vector3(0f, 0f, -1f);
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		bool changed = GizmoBehavior.AxisTranslationBehavior(ctx, draw, axisId, outPos, axis, 0f, worldHeight, worldSize, ref outPos);
		Check("Axis drag: hover frame reports no change", !changed);
		Check("Axis drag: handle is hot after hover", ctx.HotId == axisId);

		// Frame 2 — press. Nearest(ray, axisLine) gives tl = 1, so the drag anchor is (1,0,0).
		appData.KeyDown[GizmoKeys.ActionSelect] = true;
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisTranslationBehavior(ctx, draw, axisId, outPos, axis, 0f, worldHeight, worldSize, ref outPos);
		Check("Axis drag: press frame activates the handle", ctx.ActiveId == axisId);
		Check("Axis drag: press frame does not move the object", !changed);

		// Frame 3 — drag to where the cursor maps to x = 3. delta = axis*3 - anchor = +2 on X.
		appData.CursorRayOrigin = new Vector3(3f, 0f, 0f);
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisTranslationBehavior(ctx, draw, axisId, outPos, axis, 0f, worldHeight, worldSize, ref outPos);
		Check("Axis drag: drag frame reports a change", changed);
		CheckVec("Axis drag: object moved +2 on X and nowhere else", outPos, new Vector3(2f, 0f, -10f));

		// Frame 4 — release. Im3d clears the active id; hotness deliberately remains so the handle
		// can be re-grabbed immediately.
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisTranslationBehavior(ctx, draw, axisId, outPos, axis, 0f, worldHeight, worldSize, ref outPos);
		Check("Axis drag: release frame reports no change", !changed);
		Check("Axis drag: release clears the active handle", ctx.ActiveId == GizmoContext.IdInvalid);
		Check("Axis drag: handle stays hot after release", ctx.HotId == axisId);

		ctx.EndGizmo();
	}

	// --- V5: rotation / scale helpers and drags ---

	private static void TestMatrixHelpers()
	{
		// A diagonal basis: GetScale is the column lengths and GetRotation divides them out.
		Basis scaled = new Basis(new Vector3(2f, 0f, 0f), new Vector3(0f, 3f, 0f), new Vector3(0f, 0f, 4f));
		CheckVec("GetScale reads column lengths", GizmoMath.GetScale(scaled), new Vector3(2f, 3f, 4f));
		Basis rotation = GizmoMath.GetRotation(scaled);
		CheckVec("GetRotation X column is unit", rotation.X, new Vector3(1f, 0f, 0f));
		CheckVec("GetRotation Y column is unit", rotation.Y, new Vector3(0f, 1f, 0f));
		CheckVec("GetRotation Z column is unit", rotation.Z, new Vector3(0f, 0f, 1f));

		// +90 degrees about Z: X -> +Y, Y -> -X. Built from columns so the convention is explicit.
		Basis rotZ90 = new Basis(new Vector3(0f, 1f, 0f), new Vector3(-1f, 0f, 0f), new Vector3(0f, 0f, 1f));
		CheckVec("ToEulerXYZ of identity is zero", GizmoMath.ToEulerXYZ(Basis.Identity), Vector3.Zero);
		CheckVec("ToEulerXYZ of +90 about Z is (0,0,pi/2)",
			GizmoMath.ToEulerXYZ(rotZ90), new Vector3(0f, 0f, Mathf.Pi * 0.5f));

		// SetRotation puts the rotation back and re-applies the original scale.
		Basis restored = GizmoMath.SetRotation(scaled, rotZ90);
		CheckVec("SetRotation scales the X column", restored.X, new Vector3(0f, 2f, 0f));
		CheckVec("SetRotation scales the Y column", restored.Y, new Vector3(-3f, 0f, 0f));
		CheckVec("SetRotation keeps the Z column", restored.Z, new Vector3(0f, 0f, 4f));

		// SetScale rescales each column by the ratio to its current length.
		Basis rescaled = GizmoMath.SetScale(scaled, new Vector3(4f, 9f, 16f));
		CheckVec("SetScale X column becomes 4 on X", rescaled.X, new Vector3(4f, 0f, 0f));
		CheckVec("SetScale Y column becomes 9 on Y", rescaled.Y, new Vector3(0f, 9f, 0f));
		CheckVec("SetScale Z column becomes 16 on Z", rescaled.Z, new Vector3(0f, 0f, 16f));
	}

	/// <summary>
	/// End-to-end rotation drag around +Z: hover, press, drag a quarter turn, release. Camera at
	/// (0,0,10) looking down -Z, object at the origin, ring radius 1. The cursor rays hit z=0 at
	/// (1,0,0) then (0,1,0), so the expected angle is acos(0) = pi/2 with a positive sign.
	/// </summary>
	private static void TestRotationDrag()
	{
		GizmoAppData appData = MakeTestAppData();
		GizmoContext ctx = new GizmoContext(appData);
		GizmoSelfTestDraw draw = new GizmoSelfTestDraw();

		appData.ViewOrigin = new Vector3(0f, 0f, 10f);
		appData.ViewDirection = new Vector3(0f, 0f, -1f);

		Vector3 origin = Vector3.Zero;
		Vector3 axis = new Vector3(0f, 0f, 1f);
		float worldRadius = 1.0f;
		float worldSize = 0.1f;
		uint axisId = GizmoContext.MakeHandleId(1, 0);
		float angle = 0f;

		ctx.BeginGizmo(1);

		// Frame 1 — hover at (1,0,0): the distance to the origin is the ring radius.
		appData.CursorRayOrigin = new Vector3(1f, 0f, 10f);
		appData.CursorRayDirection = new Vector3(0f, 0f, -1f);
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		bool changed = GizmoBehavior.AxisAngleBehavior(ctx, draw, axisId, origin, axis, 0f, worldRadius, worldSize, ref angle);
		Check("Rotation drag: hover reports no change", !changed);
		Check("Rotation drag: ring is hot after hover", ctx.HotId == axisId);

		// Frame 2 — press: the stored vector is (1,0,0) and the stored angle is the passed-in 0.
		appData.KeyDown[GizmoKeys.ActionSelect] = true;
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisAngleBehavior(ctx, draw, axisId, origin, axis, 0f, worldRadius, worldSize, ref angle);
		Check("Rotation drag: press activates the ring", ctx.ActiveId == axisId);
		CheckVec("Rotation drag: stored vector is the press direction", ctx.GizmoStateVec3, new Vector3(1f, 0f, 0f));

		// The controller captures the rotation on the activation frame; simulate that here.
		ctx.StoredRotation = Basis.Identity;

		// Frame 3 — drag to (0,1,0): delta is +90 degrees about +Z.
		appData.CursorRayOrigin = new Vector3(0f, 1f, 10f);
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisAngleBehavior(ctx, draw, axisId, origin, axis, 0f, worldRadius, worldSize, ref angle);
		Check("Rotation drag: drag reports a change", changed);
		CheckNear("Rotation drag: angle is +pi/2", angle, Mathf.Pi * 0.5f);

		// The controller recomposes with Rotation(axis, angle - storedAngle) * storedRotation.
		Basis result = new Basis(axis, angle - ctx.GizmoStateFloat) * ctx.StoredRotation;
		CheckVec("Rotation drag: result maps +X onto +Y", result.X, new Vector3(0f, 1f, 0f));

		// Frame 4 — release.
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisAngleBehavior(ctx, draw, axisId, origin, axis, 0f, worldRadius, worldSize, ref angle);
		Check("Rotation drag: release reports no change", !changed);
		Check("Rotation drag: release clears the active ring", ctx.ActiveId == GizmoContext.IdInvalid);

		ctx.EndGizmo();
	}

	/// <summary>
	/// End-to-end scale drag along +X: hover, press, drag from tl=0.5 to tl=1.5, release. With
	/// worldHeight 1 the relative delta is 1, so the scale factor is 1 + 1 = 2.
	/// </summary>
	private static void TestScaleDrag()
	{
		GizmoAppData appData = MakeTestAppData();
		GizmoContext ctx = new GizmoContext(appData);
		GizmoSelfTestDraw draw = new GizmoSelfTestDraw();

		Vector3 origin = Vector3.Zero;
		Vector3 axis = new Vector3(1f, 0f, 0f);
		float worldHeight = 1.0f;
		float worldSize = 0.1f;
		uint axisId = GizmoContext.MakeHandleId(1, 0);
		float scale = 1.0f;

		ctx.BeginGizmo(1);

		// Frame 1 — hover on the capsule axis at (0.5,0,0).
		appData.CursorRayOrigin = new Vector3(0.5f, 0f, 10f);
		appData.CursorRayDirection = new Vector3(0f, 0f, -1f);
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		bool changed = GizmoBehavior.AxisScaleBehavior(ctx, draw, axisId, origin, axis, 0f, worldHeight, worldSize, ref scale);
		Check("Scale drag: hover reports no change", !changed);
		Check("Scale drag: axis is hot after hover", ctx.HotId == axisId);

		// Frame 2 — press: Nearest gives tl = 0.5, so the stored position is (0.5,0,0).
		appData.KeyDown[GizmoKeys.ActionSelect] = true;
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisScaleBehavior(ctx, draw, axisId, origin, axis, 0f, worldHeight, worldSize, ref scale);
		Check("Scale drag: press activates the axis", ctx.ActiveId == axisId);
		CheckVec("Scale drag: stored position is (0.5,0,0)", ctx.GizmoStateVec3, new Vector3(0.5f, 0f, 0f));
		CheckNear("Scale drag: stored scale is 1", ctx.GizmoStateFloat, 1.0f);

		// Frame 3 — drag to tl = 1.5: |delta| / worldHeight = 1, so the factor is 2.
		appData.CursorRayOrigin = new Vector3(1.5f, 0f, 10f);
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisScaleBehavior(ctx, draw, axisId, origin, axis, 0f, worldHeight, worldSize, ref scale);
		Check("Scale drag: drag reports a change", changed);
		CheckNear("Scale drag: scale doubles", scale, 2.0f);

		// Frame 4 — release.
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		changed = GizmoBehavior.AxisScaleBehavior(ctx, draw, axisId, origin, axis, 0f, worldHeight, worldSize, ref scale);
		Check("Scale drag: release reports no change", !changed);
		Check("Scale drag: release clears the active axis", ctx.ActiveId == GizmoContext.IdInvalid);

		ctx.EndGizmo();
	}

	/// <summary>
	/// The face-handle math (milestone 2.7): the face table and handle ids, face centres on the
	/// mesh bounds (unrotated, scaled and rotated), the pick-bound half extents, and the drag
	/// arithmetic — including the opposite-face-fixed contract and the 1e-4 scale floor.
	/// </summary>
	private static void TestFaceScale()
	{
		int plusZ = DecalMath.FaceIndex("+Z");
		int minusZ = DecalMath.FaceIndex("-Z");
		int plusX = DecalMath.FaceIndex("+X");
		int minusX = DecalMath.FaceIndex("-X");
		int plusY = DecalMath.FaceIndex("+Y");
		int minusY = DecalMath.FaceIndex("-Y");

		Check("Faces: the axis mapping covers all six",
			FaceScaleMath.AxisForFace(plusZ) == 2 && FaceScaleMath.AxisForFace(minusZ) == 2
			&& FaceScaleMath.AxisForFace(plusX) == 0 && FaceScaleMath.AxisForFace(minusX) == 0
			&& FaceScaleMath.AxisForFace(plusY) == 1 && FaceScaleMath.AxisForFace(minusY) == 1);
		Check("Faces: the sign is the normal's own direction",
			FaceScaleMath.SignForFace(plusX) > 0f && FaceScaleMath.SignForFace(minusX) < 0f
			&& FaceScaleMath.SignForFace(plusY) > 0f && FaceScaleMath.SignForFace(minusY) < 0f
			&& FaceScaleMath.SignForFace(plusZ) > 0f && FaceScaleMath.SignForFace(minusZ) < 0f);
		Check("Faces: handle ids sit outside the ported handle range",
			FaceScaleMath.HandleBase > 8
			&& FaceScaleMath.HandleId(0) == FaceScaleMath.HandleBase
			&& FaceScaleMath.HandleId(5) == FaceScaleMath.HandleBase + 5
			&& FaceScaleMath.HandleId(99) == FaceScaleMath.HandleBase + plusZ);

		Vector3 min = new Vector3(-1f, -2f, -3f);
		Vector3 max = new Vector3(1f, 2f, 3f);
		CheckVec("Faces: +Z centre is the +Z bounds face", FaceScaleMath.LocalFaceCenter(plusZ, min, max), new Vector3(0f, 0f, 3f));
		CheckVec("Faces: -X centre is the -X bounds face", FaceScaleMath.LocalFaceCenter(minusX, min, max), new Vector3(-1f, 0f, 0f));
		CheckVec("Faces: +Y centre is the +Y bounds face", FaceScaleMath.LocalFaceCenter(plusY, min, max), new Vector3(0f, 2f, 0f));
		CheckNear("Faces: the mesh half along +Z is the Z extent", FaceScaleMath.MeshHalfAlongAxis(plusZ, min, max), 3f);
		CheckNear("Faces: the mesh half along +X is the X extent", FaceScaleMath.MeshHalfAlongAxis(plusX, min, max), 1f);

		// A scaled host: the face centre lands on the *visual* face.
		Transform3D scaled = new Transform3D(Basis.Identity.Scaled(new Vector3(2f, 1f, 0.5f)), Vector3.Zero);
		CheckVec("Faces: a scaled host's +Z sphere sits on the visual face",
			FaceScaleMath.WorldFaceCenter(plusZ, min, max, scaled), new Vector3(0f, 0f, 1.5f));
		CheckVec("Faces: a scaled host's +X sphere sits on the visual face",
			FaceScaleMath.WorldFaceCenter(plusX, min, max, scaled), new Vector3(2f, 0f, 0f));

		// A rotated host: +90 deg about Y sends the local +Z face to world +X.
		Vector3 unitMin = new Vector3(-1f, -1f, -1f);
		Vector3 unitMax = new Vector3(1f, 1f, 1f);
		Transform3D rotated = new Transform3D(new Basis(Vector3.Up, Mathf.Pi * 0.5f), Vector3.Zero);
		CheckVec("Faces: a rotated host's +Z sphere follows the face to world +X",
			FaceScaleMath.WorldFaceCenter(plusZ, unitMin, unitMax, rotated), new Vector3(1f, 0f, 0f));
		CheckVec("Faces: a rotated host's +Z normal follows the rotation",
			FaceScaleMath.WorldFaceNormal(plusZ, rotated), new Vector3(1f, 0f, 0f));
		CheckVec("Faces: a non-uniform scale does not stretch the normal",
			FaceScaleMath.WorldFaceNormal(plusZ, scaled), new Vector3(0f, 0f, 1f));

		CheckVec("Faces: the world half extents of an unscaled bounds are the bounds halves",
			FaceScaleMath.WorldHalfExtents(unitMin, unitMax, Transform3D.Identity), new Vector3(1f, 1f, 1f));
		Transform3D diagonal = new Transform3D(new Basis(Vector3.Back, Mathf.Pi * 0.25f), Vector3.Zero);
		Vector3 thinMin = new Vector3(-1f, 0f, 0f);
		Vector3 thinMax = new Vector3(1f, 0f, 0f);
		CheckVec("Faces: a 45-deg rotated sliver's half extents grow as expected",
			FaceScaleMath.WorldHalfExtents(thinMin, thinMax, diagonal),
			new Vector3(0.7071068f, 0.7071068f, 0f));

		// The direction a drag is measured along: the face normal projected into the view plane.
		CheckVec("Faces: a face-on view projects the normal to nothing",
			FaceScaleMath.ProjectedAxisDirection(new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f)), Vector3.Zero);
		CheckVec("Faces: a 45-deg view projects the normal to 45 deg",
			FaceScaleMath.ProjectedAxisDirection(new Vector3(0f, 0f, 1f), new Vector3(0f, -0.7071068f, -0.7071068f)),
			new Vector3(0f, -0.7071068f, 0.7071068f));
		CheckVec("Faces: a perpendicular view keeps the normal as it is",
			FaceScaleMath.ProjectedAxisDirection(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f)),
			new Vector3(0f, 1f, 0f));

		// Drag arithmetic: one face moves, the opposite face is fixed by construction. The
		// opposite-face check derives the new half extent from the NEW SCALE (not from the shift),
		// so the assertion has content: opposite = new origin - new half.
		Vector3 zNormal = new Vector3(0f, 0f, 1f);
		FaceScaleMath.FaceDrag forth = FaceScaleMath.ResolveFaceDrag(plusZ, 1f, 0.5f, zNormal, new Vector3(0f, 0f, 0.4f), 0f);
		Check("Faces: an outward drag changes the scale", forth.Changed);
		CheckNear("Faces: dragging 0.4 out adds 0.2 to the half over a 0.5 half", forth.ScaleAxis, 1.4f);
		CheckVec("Faces: the origin shifts by half the drag", forth.PositionShift, new Vector3(0f, 0f, 0.2f));
		CheckNear("Faces: the opposite face stays put",
			forth.PositionShift.Z - 0.5f * forth.ScaleAxis, -0.5f);

		Vector3 negativeXNormal = new Vector3(-1f, 0f, 0f);
		FaceScaleMath.FaceDrag mirrored = FaceScaleMath.ResolveFaceDrag(minusX, 1f, 0.5f, negativeXNormal, new Vector3(-0.6f, 0f, 0f), 0f);
		CheckNear("Faces: a -X drag grows its own side outward", mirrored.ScaleAxis, 1.6f);
		CheckVec("Faces: the -X shift follows the handle's own normal",
			mirrored.PositionShift, new Vector3(-0.3f, 0f, 0f));
		CheckNear("Faces: the +X face stays put on a -X drag",
			0.5f + mirrored.PositionShift.X + 0.5f * (mirrored.ScaleAxis - 1f), 0.5f);

		FaceScaleMath.FaceDrag snapped = FaceScaleMath.ResolveFaceDrag(plusZ, 1f, 0.5f, zNormal, new Vector3(0f, 0f, 0.26f), 0.1f);
		CheckVec("Faces: the drag snaps in world units",
			snapped.PositionShift, new Vector3(0f, 0f, 0.1f));

		FaceScaleMath.FaceDrag floored = FaceScaleMath.ResolveFaceDrag(plusZ, 1f, 0.5f, zNormal, new Vector3(0f, 0f, -100f), 0f);
		CheckNear("Faces: the scale floors at 1e-4", floored.ScaleAxis, FaceScaleMath.MinScale);
		CheckNear("Faces: the shift stays consistent at the floor so the opposite face cannot move",
			floored.PositionShift.Z - 0.5f * floored.ScaleAxis, -0.5f);

		Check("Faces: a flat face refuses the drag (no thickness)",
			!FaceScaleMath.ResolveFaceDrag(plusZ, 1f, 0f, zNormal, new Vector3(0f, 0f, 1f), 0f).Changed);
		Check("Faces: an unchanged cursor reports no change",
			!FaceScaleMath.ResolveFaceDrag(plusZ, 1f, 0.5f, zNormal, new Vector3(0.3f, 0f, 0f), 0f).Changed);
		Check("Faces: the face floor is the milestone's 1e-4",
			Mathf.IsEqualApprox(FaceScaleMath.MinScale, 1e-4f));
	}

	/// <summary>
	/// One face drag end to end through the behavior's state machine, on the +Y face with the view
	/// along -Z so every number is hand-checkable: hover, press (capture), drag 0.4 world units
	/// along the face normal, release. With a unit cube (half 0.5) and a start scale of 1, the drag
	/// must land on scale 1.4 and an origin of (0, 0.2, 0) — leaving the -Y face exactly where it
	/// was. Holding the cursor still and dragging further are covered explicitly: both outputs are
	/// absolute from press, so neither may accumulate (the drift regression).
	/// </summary>
	private static void TestFaceScaleDrag()
	{
		GizmoAppData appData = MakeTestAppData();
		GizmoContext ctx = new GizmoContext(appData);
		FaceScaleBehavior.DragState state = new FaceScaleBehavior.DragState();

		Vector3 meshMin = new Vector3(-0.5f, -0.5f, -0.5f);
		Vector3 meshMax = new Vector3(0.5f, 0.5f, 0.5f);
		Transform3D transform = Transform3D.Identity;
		int face = DecalMath.FaceIndex("+Y");
		uint id = GizmoContext.MakeHandleId(1, FaceScaleMath.HandleId(face));
		float scaleY = 1f;
		float handleRadius = 0.2f;
		float meshHalf = FaceScaleMath.MeshHalfAlongAxis(face, meshMin, meshMax);
		Vector3 centre = FaceScaleMath.WorldFaceCenter(face, meshMin, meshMax, transform);
		Vector3 normal = FaceScaleMath.WorldFaceNormal(face, transform);

		ctx.BeginGizmo(1);
		appData.SnapScale = 0f;
		appData.ViewOrigin = new Vector3(0f, 0.5f, 10f);

		// Frame 1 — hover: the ray is aimed at the +Y face centre.
		appData.CursorRayOrigin = new Vector3(0f, 0.5f, 10f);
		appData.CursorRayDirection = new Vector3(0f, 0f, -1f);
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		bool changed = FaceScaleBehavior.Apply(ctx, id, face, centre, normal, transform.Origin, handleRadius,
			scaleY, meshHalf, appData.SnapScale, ref state, out float newScale, out Vector3 newOrigin);
		Check("Face drag: hover reports no change", !changed);
		Check("Face drag: the sphere is hot after hover", ctx.HotId == id);

		// Frame 2 — press: the capture records the camera-facing plane through the face centre and
		// the in-plane direction the drag is measured along (here exactly +Y, since the view runs
		// along -Z).
		appData.KeyDown[GizmoKeys.ActionSelect] = true;
		ctx.BeginFrame();
		changed = FaceScaleBehavior.Apply(ctx, id, face, centre, normal, transform.Origin, handleRadius,
			scaleY, meshHalf, appData.SnapScale, ref state, out newScale, out newOrigin);
		Check("Face drag: press activates the sphere", ctx.ActiveId == id);
		Check("Face drag: the press is captured", state.Captured && state.Face == face);
		CheckVec("Face drag: the anchor sits on the face centre", state.Anchor, new Vector3(0f, 0.5f, 0f));
		CheckVec("Face drag: the measured direction is the face normal in-plane", state.AxisDir, new Vector3(0f, 1f, 0f));

		// Frame 3 — drag 0.4 world units along the normal (the cursor moves up on screen): the
		// origin shifts by half of it and the half-extent grows by the other half, so the -Y face
		// stays exactly where it was.
		appData.CursorRayOrigin = new Vector3(0f, 0.9f, 10f);
		ctx.BeginFrame();
		changed = FaceScaleBehavior.Apply(ctx, id, face, centre, normal, transform.Origin, handleRadius,
			scaleY, meshHalf, appData.SnapScale, ref state, out newScale, out newOrigin);
		Check("Face drag: the drag reports a change", changed);
		CheckNear("Face drag: the scale grows by half the drag over the half extent", newScale, 1.4f);
		CheckVec("Face drag: the origin lands at the press origin plus half the drag",
			newOrigin, new Vector3(0f, 0.2f, 0f));
		CheckNear("Face drag: the opposite face stays put",
			newOrigin.Y - meshHalf * newScale, -0.5f);

		// Frame 3b — the same cursor position again. Absolute outputs mean nothing may accumulate:
		// this is the drift regression, where holding the cursor still walked the part away.
		ctx.BeginFrame();
		changed = FaceScaleBehavior.Apply(ctx, id, face, centre, normal, transform.Origin, handleRadius,
			scaleY, meshHalf, appData.SnapScale, ref state, out newScale, out newOrigin);
		Check("Face drag: holding still keeps the scale where it was", changed && Mathf.Abs(newScale - 1.4f) < 1e-4f);
		CheckVec("Face drag: holding still keeps the origin where it was",
			newOrigin, new Vector3(0f, 0.2f, 0f));

		// Frame 3c — drag further, 0.6 in total: the outputs stay absolute, so the origin is the
		// press origin plus 0.3 — not the sum of both frames' offsets.
		appData.CursorRayOrigin = new Vector3(0f, 1.1f, 10f);
		ctx.BeginFrame();
		changed = FaceScaleBehavior.Apply(ctx, id, face, centre, normal, transform.Origin, handleRadius,
			scaleY, meshHalf, appData.SnapScale, ref state, out newScale, out newOrigin);
		Check("Face drag: a further drag tracks the cursor", changed);
		CheckNear("Face drag: the further drag's scale is absolute", newScale, 1.6f);
		CheckVec("Face drag: the further drag's origin is absolute",
			newOrigin, new Vector3(0f, 0.3f, 0f));
		CheckNear("Face drag: the opposite face is still put after the further drag",
			newOrigin.Y - meshHalf * newScale, -0.5f);

		// Frame 4 — release.
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		changed = FaceScaleBehavior.Apply(ctx, id, face, centre, normal, transform.Origin, handleRadius,
			newScale, meshHalf, appData.SnapScale, ref state, out newScale, out newOrigin);
		Check("Face drag: release reports no change", !changed);
		Check("Face drag: release clears the active sphere", ctx.ActiveId == GizmoContext.IdInvalid);
		Check("Face drag: release drops the capture", !state.Captured);

		// Head-on view: with the camera on the face axis the projected direction is zero, so a drag
		// must produce NO motion. The Im3d axis-line measurement (closest approach) divides by a
		// vanishing sine there and would fling the scale to an extreme — the regression this
		// scheme exists to prevent.
		FaceScaleBehavior.DragState headOn = new FaceScaleBehavior.DragState();
		int plusZ = DecalMath.FaceIndex("+Z");
		uint headOnId = GizmoContext.MakeHandleId(1, FaceScaleMath.HandleId(plusZ));
		Vector3 zCentre = FaceScaleMath.WorldFaceCenter(plusZ, meshMin, meshMax, transform);
		Vector3 zNormal = FaceScaleMath.WorldFaceNormal(plusZ, transform);
		appData.ViewOrigin = new Vector3(0f, 0f, 10f);
		appData.CursorRayOrigin = new Vector3(0f, 0f, 10f);
		appData.CursorRayDirection = new Vector3(0f, 0f, -1f);
		appData.KeyDown[GizmoKeys.ActionSelect] = false;
		ctx.BeginFrame();
		FaceScaleBehavior.Apply(ctx, headOnId, plusZ, zCentre, zNormal, transform.Origin, handleRadius,
			1f, 0.5f, appData.SnapScale, ref headOn, out _, out _);
		appData.KeyDown[GizmoKeys.ActionSelect] = true;
		ctx.BeginFrame();
		FaceScaleBehavior.Apply(ctx, headOnId, plusZ, zCentre, zNormal, transform.Origin, handleRadius,
			1f, 0.5f, appData.SnapScale, ref headOn, out _, out _);
		Check("Face drag: a head-on press is captured", headOn.Captured && headOn.AxisDir == Vector3.Zero);
		appData.CursorRayOrigin = new Vector3(0.4f, 0f, 10f);
		ctx.BeginFrame();
		changed = FaceScaleBehavior.Apply(ctx, headOnId, plusZ, zCentre, zNormal, transform.Origin, handleRadius,
			1f, 0.5f, appData.SnapScale, ref headOn, out _, out _);
		Check("Face drag: a head-on drag produces no motion instead of a jump", !changed);

		ctx.EndGizmo();
	}

	// --- V7: direct manipulation support (bulk selection, marquee hit test) ---

	/// <summary>
	/// The hierarchy marquee replaces the selection in one go, so the bulk selection methods must
	/// raise OnSelectionChanged exactly once and ignore duplicates / nulls.
	/// </summary>
	private static void TestSelectionBulk()
	{
		SelectionManager selection = new SelectionManager();
		int raises = 0;
		selection.OnSelectionChanged += () => raises++;

		Node a = new Node();
		Node b = new Node();
		Node c = new Node();

		selection.SetSelection(new Node[] { a, b, b, null });
		Check("SetSelection keeps distinct valid nodes only", selection.Count == 2);
		Check("SetSelection raises once", raises == 1);

		selection.AddToSelection(new Node[] { b, c });
		Check("AddToSelection only adds nodes that are new", selection.Count == 3);
		Check("AddToSelection raises once", raises == 2);

		selection.AddToSelection(new Node[] { c });
		Check("AddToSelection with nothing new does not raise", raises == 2);

		selection.SetSelection(new Node[] { c });
		Check("SetSelection replaces the selection", selection.Count == 1 && selection.IsSelected(c));
		Check("SetSelection raises again", raises == 3);

		a.Free();
		b.Free();
		c.Free();
	}

	/// <summary>
	/// Frame clamping: the single rule behind "UI elements cannot cross outside the viewport picture
	/// frame". Hand-computed against an 800x600 frame and the 8px minimum element size.
	/// </summary>
	private static void TestUiRectClamp()
	{
		Vector2 bounds = new Vector2(800f, 600f);

		Vector2 pos = new Vector2(-20f, -10f);
		Vector2 size = new Vector2(100f, 50f);
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		CheckVec2("Clamp pushes a negative position to the frame origin", pos, new Vector2(0f, 0f));
		CheckVec2("Clamp leaves a fitting size alone", size, new Vector2(100f, 50f));

		pos = new Vector2(790f, 590f);
		size = new Vector2(100f, 50f);
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		CheckVec2("Clamp pulls the far corner back inside the frame", pos, new Vector2(700f, 550f));

		pos = new Vector2(50f, 50f);
		size = new Vector2(900f, 700f);
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		CheckVec2("Clamp caps an oversized element to the frame", size, new Vector2(800f, 600f));
		CheckVec2("Clamp repositions a frame-sized element to the origin", pos, new Vector2(0f, 0f));

		pos = new Vector2(100f, 100f);
		size = new Vector2(2f, 3f);
		UiGizmoMath.ClampRect(ref pos, ref size, bounds);
		CheckVec2("Clamp floors a collapsed element at the minimum size", size, new Vector2(8f, 8f));
		CheckVec2("Clamp leaves a fitting minimum-size element in place", pos, new Vector2(100f, 100f));

		// A frame narrower than the minimum element must not grow the element past the frame.
		pos = new Vector2(0f, 0f);
		size = new Vector2(40f, 40f);
		UiGizmoMath.ClampRect(ref pos, ref size, new Vector2(20f, 20f));
		CheckVec2("Clamp never grows an element past a tiny frame", size, new Vector2(20f, 20f));
	}

	/// <summary>Handle layout: the 8 handle points, edge ownership and the inclusive hit test.</summary>
	private static void TestUiHandleLayout()
	{
		Vector2 pos = new Vector2(100f, 200f);
		Vector2 size = new Vector2(60f, 40f);

		CheckVec2("Top-left handle sits on the corner", UiGizmoMath.HandlePoint(UiGizmoMath.HandleTopLeft, pos, size), new Vector2(100f, 200f));
		CheckVec2("Top handle sits on the top edge midpoint", UiGizmoMath.HandlePoint(UiGizmoMath.HandleTop, pos, size), new Vector2(130f, 200f));
		CheckVec2("Top-right handle sits on the corner", UiGizmoMath.HandlePoint(UiGizmoMath.HandleTopRight, pos, size), new Vector2(160f, 200f));
		CheckVec2("Right handle sits on the right edge midpoint", UiGizmoMath.HandlePoint(UiGizmoMath.HandleRight, pos, size), new Vector2(160f, 220f));
		CheckVec2("Bottom-right handle sits on the corner", UiGizmoMath.HandlePoint(UiGizmoMath.HandleBottomRight, pos, size), new Vector2(160f, 240f));
		CheckVec2("Bottom handle sits on the bottom edge midpoint", UiGizmoMath.HandlePoint(UiGizmoMath.HandleBottom, pos, size), new Vector2(130f, 240f));
		CheckVec2("Bottom-left handle sits on the corner", UiGizmoMath.HandlePoint(UiGizmoMath.HandleBottomLeft, pos, size), new Vector2(100f, 240f));
		CheckVec2("Left handle sits on the left edge midpoint", UiGizmoMath.HandlePoint(UiGizmoMath.HandleLeft, pos, size), new Vector2(100f, 220f));

		Check("Left handle owns the left edge", UiGizmoMath.AffectsLeft(UiGizmoMath.HandleLeft) && !UiGizmoMath.AffectsRight(UiGizmoMath.HandleLeft));
		Check("Top handle owns the top edge", UiGizmoMath.AffectsTop(UiGizmoMath.HandleTop) && !UiGizmoMath.AffectsBottom(UiGizmoMath.HandleTop));
		Check("Top-left corner owns both of its edges",
			UiGizmoMath.AffectsLeft(UiGizmoMath.HandleTopLeft) && UiGizmoMath.AffectsTop(UiGizmoMath.HandleTopLeft));
		Check("Corners are corners and edge midpoints are not",
			UiGizmoMath.IsCorner(UiGizmoMath.HandleBottomRight) && !UiGizmoMath.IsCorner(UiGizmoMath.HandleBottom));

		Check("Centre point is inside the rect", UiGizmoMath.ContainsPoint(pos, size, new Vector2(130f, 220f)));
		Check("Corner point counts as inside (edges inclusive)", UiGizmoMath.ContainsPoint(pos, size, new Vector2(160f, 240f)));
		Check("Point one pixel left of the rect is outside", !UiGizmoMath.ContainsPoint(pos, size, new Vector2(99f, 220f)));
		Check("Point below the rect is outside", !UiGizmoMath.ContainsPoint(pos, size, new Vector2(130f, 241f)));
	}

	/// <summary>Vector2 sibling of <see cref="CheckVec"/>, for the 2D UI editor checks.</summary>
	private static void CheckVec2(string name, Vector2 actual, Vector2 expected)
	{
		Check(name + " (got " + actual.ToString() + ", want " + expected.ToString() + ")",
			Mathf.Abs(actual.X - expected.X) <= Tolerance && Mathf.Abs(actual.Y - expected.Y) <= Tolerance);
	}

	/// <summary>
	/// Resize math: one edge follows the mouse, the opposite edge stays anchored, dragged-past edges
	/// normalize instead of inverting, and Shift aspect-locks corners only.
	/// </summary>
	private static void TestUiResize()
	{
		Vector2 startPos = new Vector2(100f, 200f);
		Vector2 startSize = new Vector2(60f, 40f);

		UiGizmoMath.ResizeRect(startPos, startSize, UiGizmoMath.HandleRight, new Vector2(20f, 5f), false, out Vector2 pos, out Vector2 size);
		CheckVec2("Right handle grows only the width", size, new Vector2(80f, 40f));
		CheckVec2("Right handle keeps the left edge anchored", pos, new Vector2(100f, 200f));

		UiGizmoMath.ResizeRect(startPos, startSize, UiGizmoMath.HandleTopLeft, new Vector2(10f, 10f), false, out pos, out size);
		CheckVec2("Top-left handle moves the corner", pos, new Vector2(110f, 210f));
		CheckVec2("Top-left handle shrinks both axes", size, new Vector2(50f, 30f));

		UiGizmoMath.ResizeRect(startPos, startSize, UiGizmoMath.HandleBottom, new Vector2(0f, 100f), false, out pos, out size);
		CheckVec2("Bottom handle grows only the height", size, new Vector2(60f, 140f));
		CheckVec2("Bottom handle keeps the top edge anchored", pos, new Vector2(100f, 200f));

		// Dragged 200px right: the left edge passes the right edge, so the rect flips cleanly.
		UiGizmoMath.ResizeRect(startPos, startSize, UiGizmoMath.HandleLeft, new Vector2(200f, 0f), false, out pos, out size);
		CheckVec2("Left handle dragged past the right edge flips the rect", pos, new Vector2(160f, 200f));
		CheckVec2("Flipped rect keeps a positive size", size, new Vector2(140f, 40f));

		// Start aspect 60/40 = 1.5; width 90 forces height 60, anchored at the top-left corner.
		UiGizmoMath.ResizeRect(startPos, startSize, UiGizmoMath.HandleBottomRight, new Vector2(30f, 0f), true, out pos, out size);
		CheckVec2("Aspect-locked corner derives the height from the width", size, new Vector2(90f, 60f));
		CheckVec2("Aspect-locked corner keeps the opposite corner anchored", pos, new Vector2(100f, 200f));

		// Edge handles have no corner to anchor, so aspect lock must not apply to them.
		UiGizmoMath.ResizeRect(startPos, startSize, UiGizmoMath.HandleRight, new Vector2(30f, 0f), true, out pos, out size);
		CheckVec2("Aspect lock is ignored on an edge handle", size, new Vector2(90f, 40f));
	}

	/// <summary>Snap search: nearest target within the threshold, and the guide it reports.</summary>
	private static void TestUiSnap()
	{
		List<float> targets = new List<float> { 0f, 100f, 200f };

		Check("Edge within the threshold snaps to the nearest target",
			UiSnap.TrySnapEdge(103f, targets, UiSnap.ThresholdPixels, out float delta, out float guide));
		CheckNear("Snap delta moves the edge onto the target", delta, -3f);
		CheckNear("Snap reports the target coordinate as the guide", guide, 100f);

		Check("Edge outside the threshold does not snap",
			!UiSnap.TrySnapEdge(107f, targets, UiSnap.ThresholdPixels, out delta, out guide));

		// Rect snap: edges 50/75/100 against targets 0/80/200 — the centre (75) is 5 from 80.
		List<float> source = new List<float> { 50f, 75f, 100f };
		List<float> rectTargets = new List<float> { 0f, 80f, 200f };
		Check("Rect snap finds the closest line pair",
			UiSnap.TrySnapLine(source, rectTargets, UiSnap.ThresholdPixels, out delta, out guide));
		CheckNear("Rect snap uses the closest pair's delta", delta, 5f);
		CheckNear("Rect snap reports the closest target as the guide", guide, 80f);

		List<float> xs = new List<float>();
		List<float> ys = new List<float>();
		UiSnap.AddRectLines(new Vector2(10f, 20f), new Vector2(40f, 60f), xs, ys);
		Check("Rect lines add both edges and the centre on X",
			xs.Count == 3 && xs[0] == 10f && xs[1] == 30f && xs[2] == 50f);
		Check("Rect lines add both edges and the centre on Y",
			ys.Count == 3 && ys[0] == 20f && ys[1] == 50f && ys[2] == 80f);

		Vector2 pos = new Vector2(0f, 0f);
		Vector2 size = new Vector2(100f, 100f);
		Check("Guide on the centre line still counts as touching", UiSnap.RectTouchesX(pos, size, 50f));
		Check("Guide on the far edge counts as touching", UiSnap.RectTouchesX(pos, size, 100f));
		Check("Guide off the rect does not count as touching", !UiSnap.RectTouchesX(pos, size, 25f));
	}

	/// <summary>Marquee hit test: overlap, non-overlap, and the zero-size (click) case.</summary>
	private static void TestMarqueeRect()
	{
		Vector2 rowMin = new Vector2(10f, 20f);
		Vector2 rowMax = new Vector2(110f, 40f);

		Check("Marquee overlapping the row hits",
			HierarchyPanel.RectsOverlap(rowMin, rowMax, new Vector2(50f, 0f), new Vector2(60f, 100f)));
		Check("Marquee to the left of the row misses",
			!HierarchyPanel.RectsOverlap(rowMin, rowMax, new Vector2(0f, 0f), new Vector2(5f, 100f)));
		Check("Marquee above the row misses",
			!HierarchyPanel.RectsOverlap(rowMin, rowMax, new Vector2(0f, 0f), new Vector2(200f, 15f)));
		Check("Marquee fully containing the row hits",
			HierarchyPanel.RectsOverlap(rowMin, rowMax, new Vector2(0f, 0f), new Vector2(200f, 200f)));
		Check("Marquee touching the row edge exactly does not overlap",
			!HierarchyPanel.RectsOverlap(rowMin, rowMax, new Vector2(110f, 20f), new Vector2(210f, 40f)));
		Check("Zero-size marquee away from any row selects nothing",
			!HierarchyPanel.RectsOverlap(rowMin, rowMax, new Vector2(50f, 90f), new Vector2(50f, 90f)));
	}

	/// <summary>
	/// V7 physics check: the pick raycast against the live lot space. Runs a frame after the lot
	/// scene is up (BuilderScene schedules it) because Godot only allows space-state queries during
	/// the physics step. Confirms the collision shapes are real and that the nearest hit wins.
	/// </summary>
	public static void RunPhysicsPick(BuilderScene scene, LotObject probe)
	{
		_failures = 0;
		_checks = 0;

		// Straight down onto the probe cube: nearest hit must be the cube, not the ground below it.
		LotObject hit = PartDragController.Pick(scene, new Vector3(3f, 5f, 0f), new Vector3(0f, -1f, 0f), out Vector3 hitPosition);
		Check("Physics pick hits the nearest part", hit == probe);
		CheckNear("Physics pick reports the cube's top face", hitPosition.Y, 2.5f);

		// Straight down onto the ground, away from the probe.
		hit = PartDragController.Pick(scene, new Vector3(-5f, 5f, 0f), new Vector3(0f, -1f, 0f), out hitPosition);
		Check("Physics pick hits the ground", hit != null && hit != probe);
		CheckNear("Physics pick reports the ground's top face", hitPosition.Y, 0.025f);

		// Past the edge of the 20x20 ground there is nothing to hit.
		hit = PartDragController.Pick(scene, new Vector3(100f, 5f, 0f), new Vector3(0f, -1f, 0f), out hitPosition);
		Check("Physics pick misses outside the lot", hit == null);

		GD.Print("[GizmoSelfTest] physics pick: " + _checks + " checks, " + _failures + " failure(s).");
	}

	/// <summary>
	/// V7 physics check for collide-and-slide: a part dragged hard into another part must stop at
	/// the contact instead of passing through it, and a diagonal drag into that same face must keep
	/// its sideways component (the slide). Both cubes are 1 unit wide, so with the blocker centred
	/// at x = 2 the dragged cube's +X face meets the blocker's -X face at x = 1.
	/// </summary>
	public static void RunPhysicsDrag(BuilderScene scene, CharacterBody3D mover, CollisionShape3D moverShape, LotObject dragged, LotObject blocker, LotObject ceiling)
	{
		_failures = 0;
		_checks = 0;

		// The drag plane is the surface behind the grabbed part: excluding the part itself, the ray
		// must reach the ground under it, giving a +Y plane at the ground's top face.
		bool foundBehind = PartDragController.PickBehind(scene, new Vector3(0f, 2.4f, 0f), new Vector3(0f, -1f, 0f), dragged, out Vector3 backPoint, out Vector3 backNormal);
		Check("PickBehind finds the surface behind the grabbed part", foundBehind);
		CheckVec("PickBehind reports the surface normal", backNormal, new Vector3(0f, 1f, 0f));
		CheckNear("PickBehind reports the surface point", backPoint.Y, 0.025f);

		// Bump: a 5-unit shove straight at the blocker must not reach x = 5.
		PartDragController.MovePartWithCollision(mover, moverShape, dragged, new Vector3(5f, 0f, 0f));
		Check("Drag bumps against a part instead of passing through it", dragged.GlobalPosition.X < 1.05f);
		Check("Drag stops at the blocker's face", dragged.GlobalPosition.X > 0.9f && dragged.GlobalPosition.X <= 1.0f);

		// Slide: a diagonal shove into the same face keeps the +Z component and still cannot creep
		// into the blocker.
		PartDragController.MovePartWithCollision(mover, moverShape, dragged, new Vector3(5f, 0f, 3f));
		Check("Drag slides along the contacted face", dragged.GlobalPosition.Z > 2.5f);
		Check("Drag does not creep into the blocker while sliding", dragged.GlobalPosition.X < 1.05f);

		// The mouse-up case: drag straight up into a part overhead. The cube must stop under it
		// (its top face at y = 2.5) instead of passing through.
		dragged.GlobalPosition = new Vector3(0f, 1f, 0f);
		PartDragController.MovePartWithCollision(mover, moverShape, dragged, new Vector3(0f, 5f, 0f));
		Check("Drag stops under a part overhead", dragged.GlobalPosition.Y > 1.9f && dragged.GlobalPosition.Y <= 2.0f);

		// Real-flow check: the controller creates its mover lazily on the first drag frame and
		// resolves that move in the same physics step, then moves in small per-frame deltas.
		dragged.GlobalPosition = new Vector3(0f, 1f, 0f);
		CharacterBody3D freshMover = PartDragController.CreateMover(scene, out CollisionShape3D freshShape);
		for (int i = 0; i < 10; i++)
		{
			PartDragController.MovePartWithCollision(freshMover, freshShape, dragged, new Vector3(0.3f, 0f, 0f));
		}
		Check("Incremental drag with a freshly created mover stops at the blocker",
			dragged.GlobalPosition.X > 0.9f && dragged.GlobalPosition.X < 1.05f);
		freshMover.QueueFree();

		// Resting on the surface it is stuck to: the part touches the ground, so every sweep sees a
		// grazing contact right at the start. It must still slide along that surface, not stall.
		dragged.GlobalPosition = new Vector3(0f, 0.525f, 0f);
		CharacterBody3D restMover = PartDragController.CreateMover(scene, out CollisionShape3D restShape);
		for (int i = 0; i < 10; i++)
		{
			PartDragController.MovePartWithCollision(restMover, restShape, dragged, new Vector3(0f, 0f, 0.5f));
		}
		Check("A part resting on a surface still slides along it", dragged.GlobalPosition.Z > 4.5f);
		CheckNear("A sliding part stays on the surface", dragged.GlobalPosition.Y, 0.525f);
		restMover.QueueFree();

		// Snap: a part grabbed while floating must drop flush onto the plane it is dragged against.
		// A 1-unit cube 3 units up, against a +Y plane at y = 0.025, has to fall by 3 - 0.5 - 0.025.
		dragged.GlobalPosition = new Vector3(0f, 3f, 0f);
		Vector3 snapDelta = PartDragController.ComputeSnapDelta(dragged, new Vector3(0f, 1f, 0f), new Vector3(0f, 0.025f, 0f));
		CheckVec("Snap closes the gap down to the surface", snapDelta, new Vector3(0f, -2.475f, 0f));

		// A part already resting on the plane must not be moved at all.
		dragged.GlobalPosition = new Vector3(0f, 0.525f, 0f);
		snapDelta = PartDragController.ComputeSnapDelta(dragged, new Vector3(0f, 1f, 0f), new Vector3(0f, 0.025f, 0f));
		CheckVec("Snap leaves a part that already touches alone", snapDelta, Vector3.Zero);

		// A brand-new mover must resolve on its very first sweep: the controller creates it during
		// the layout and moves with it in the next physics step.
		dragged.GlobalPosition = new Vector3(0f, 1f, 0f);
		CharacterBody3D firstMover = PartDragController.CreateMover(scene, out CollisionShape3D firstShape);
		PartDragController.MovePartWithCollision(firstMover, firstShape, dragged, new Vector3(1f, 0f, 0f));
		Check("A freshly created mover moves on its first sweep", dragged.GlobalPosition.X > 0.9f);
		firstMover.QueueFree();

		// The drag plane must be raised to the height the grabbed face ends up at. For a cube 3 up
		// grabbed on its top face, that is the resting top face: 0.025 (ground) + 1.0 (cube height).
		dragged.GlobalPosition = new Vector3(0f, 3f, 0f);
		Vector3 grabOffset = new Vector3(0f, 0.5f, 0f);
		Vector3 planePoint = PartDragController.ComputeDragPlanePoint(dragged, grabOffset, new Vector3(0f, 1f, 0f), new Vector3(0f, 0.025f, 0f));
		CheckNear("Drag plane sits at the grabbed face's resting height", planePoint.Y, 1.025f);

		// Following that plane from a real cursor ray must land the cube resting on the ground with
		// its grabbed top face back under the cursor - that is the no-drift guarantee. Ray from
		// (0,10,10) toward the origin, so the plane hit is (0, 1.025, 1.025); offsetting by the grab
		// offset (0, 0.5, 0) leaves the centre at y = 0.525.
		Vector3 rayDir = new Vector3(0f, -10f, -10f).Normalized();
		float rayT = (10f - planePoint.Y) / -rayDir.Y;
		Vector3 anchorPoint = new Vector3(0f, 10f, 10f) + rayDir * rayT;
		Vector3 grabTarget = anchorPoint - grabOffset;
		CheckNear("Grab target rests on the surface", grabTarget.Y, 0.525f);
		CheckNear("Grab target keeps the grabbed face on the cursor", grabTarget.Y + 0.5f, anchorPoint.Y);

		// Switching faces: the same grab against a surface one unit higher (a table top at y = 1.025)
		// must raise the plane by exactly that unit, which is what makes a mid-drag face switch work.
		Vector3 raisedPlane = PartDragController.ComputeDragPlanePoint(dragged, grabOffset, new Vector3(0f, 1f, 0f), new Vector3(0f, 1.025f, 0f));
		CheckNear("Drag plane follows the surface it is dragged against", raisedPlane.Y, 2.025f);

		GD.Print("[GizmoSelfTest] physics drag: " + _checks + " checks, " + _failures + " failure(s).");
	}

	/// <summary>No-op IGizmoDraw so the behaviors can be exercised without ImGui.</summary>
	private sealed class GizmoSelfTestDraw : IGizmoDraw
	{
		public void Line(Vector3 a, Vector3 b, float pixelThickness, Color color) { }
		public void Triangle(Vector3 a, Vector3 b, Vector3 c, Color color) { }
		public void QuadFilled(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color) { }
		public void QuadOutline(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float pixelThickness, Color color) { }
		public void Dot(Vector3 center, float pixelDiameter, Color color) { }
		public void Text(Vector2 screenPosition, Color color, string text) { }
		public Vector2 ImageOrigin { get { return Vector2.Zero; } }
		public bool WorldToScreen(Vector3 world, out Vector2 screen) { screen = Vector2.Zero; return true; }
	}
}