using System.Collections.Generic;
using Godot;

/// <summary>
/// Verification for milestone 2.6 (part decals): the face/offset/scale placement math
/// (<see cref="DecalMath"/>), a decal's derived transform against a live host (including a scaled
/// one), the property declarations the Inspector and scripts share, and a capture/restore of a
/// decal with its host link and image reference intact.
///
/// Follows the project's self-test convention: no framework, runs from <c>BuilderScene._Ready</c>
/// under <c>#if DEBUG</c> and prints pass/fail lines. Every fixture it builds is freed before it
/// returns, and every texture reference it takes is released, so the suites that run after it see
/// the same shared state they would otherwise.
/// </summary>
public static class DecalSelfTest
{
	private static int _failures;
	private static int _checks;

	public static int Run()
	{
		_failures = 0;
		_checks = 0;

		TestFaceTable();
		TestFaceTools();
		TestPlacementMath();
		TestLiveHost();
		TestPropertyDeclarations();
		TestCaptureAndRestore();
		TestLuaBindings();

		GD.Print("[DecalSelfTest] " + _checks + " checks, " + _failures + " failure(s).");
		return _failures;
	}

	/// <summary>The face vocabulary: names, indices, and the handedness contract every placement
	/// depends on (u x v == normal, so a patch's basis is a proper rotation and images never mirror).</summary>
	private static void TestFaceTable()
	{
		Check("faces: there are six of them", DecalMath.FaceNames.Length == 6);
		Check("faces: the default is +Z", DecalMath.FaceNames[DecalMath.DefaultFace] == "+Z");

		bool namesRoundTrip = true;
		for (int i = 0; i < DecalMath.FaceNames.Length; i++)
		{
			if (DecalMath.FaceIndex(DecalMath.FaceNames[i]) != i) namesRoundTrip = false;
		}
		Check("faces: every name round-trips to its index", namesRoundTrip);

		Check("faces: an unknown name is refused", DecalMath.FaceIndex("+W") == -1);
		Check("faces: an empty name is refused", DecalMath.FaceIndex("") == -1);
		Check("faces: an out-of-range index falls back to the default",
			DecalMath.ClampFace(-1) == DecalMath.DefaultFace && DecalMath.ClampFace(9) == DecalMath.DefaultFace);

		bool rightHanded = true;
		for (int i = 0; i < DecalMath.FaceNames.Length; i++)
		{
			DecalMath.Axes(i, out Vector3 u, out Vector3 v);
			if (u.Cross(v).DistanceTo(DecalMath.Normal(i)) > 1e-5f) rightHanded = false;
			if (!Mathf.IsEqualApprox(u.Length(), 1f) || !Mathf.IsEqualApprox(v.Length(), 1f)) rightHanded = false;
		}
		Check("faces: u x v == normal for every face (no mirrored patch)", rightHanded);

		Vector3 size = new Vector3(1f, 2f, 3f);
		Check("faces: the +Z face measures X by Y and sits along Z",
			Near(DecalMath.Extents(DecalMath.FaceIndex("+Z"), size), new Vector2(1f, 2f))
			&& Mathf.IsEqualApprox(DecalMath.NormalExtent(DecalMath.FaceIndex("+Z"), size), 3f));
		Check("faces: the +X face measures Z by Y and sits along X",
			Near(DecalMath.Extents(DecalMath.FaceIndex("+X"), size), new Vector2(3f, 2f))
			&& Mathf.IsEqualApprox(DecalMath.NormalExtent(DecalMath.FaceIndex("+X"), size), 1f));
		Check("faces: the +Y face measures X by Z and sits along Y",
			Near(DecalMath.Extents(DecalMath.FaceIndex("+Y"), size), new Vector2(1f, 3f))
			&& Mathf.IsEqualApprox(DecalMath.NormalExtent(DecalMath.FaceIndex("+Y"), size), 2f));

		Check("faces: visual size is the mesh size times the absolute scale",
			Near(DecalMath.VisualSize(size, new Vector3(2f, -3f, 0.5f)), new Vector3(2f, 6f, 1.5f)));
	}

	/// <summary>The placement math on its own: clamps, the unit-cube transform, the scaled-host
	/// compensation, and the face-point round trip the drag depends on.</summary>
	private static void TestPlacementMath()
	{
		Check("math: the scale clamps to the declared range",
			Mathf.IsEqualApprox(DecalMath.ClampScale(0.001f), DecalMath.MinScale)
			&& Mathf.IsEqualApprox(DecalMath.ClampScale(3f), DecalMath.MaxScale)
			&& Mathf.IsEqualApprox(DecalMath.ClampScale(0.5f), 0.5f));
		Check("math: the opacity clamps to 0..1",
			Mathf.IsEqualApprox(DecalMath.ClampOpacity(-1f), 0f) && Mathf.IsEqualApprox(DecalMath.ClampOpacity(2f), 1f));

		Check("math: a full-face patch has no room to move",
			Near(DecalMath.ClampOffset(1f, 1f, 0.4f, -0.4f), Vector2.Zero));
		Check("math: a half-size patch may move a quarter of the face",
			Near(DecalMath.ClampOffset(0.5f, 0.5f, 0.4f, -0.4f), new Vector2(0.25f, -0.25f)));

		Vector3 unit = Vector3.One;
		float lift = DecalMath.Lift(unit);
		Check("math: the lift grows with the host it decorates", DecalMath.Lift(new Vector3(20f, 0f, 20f)) > lift);

		int plusZ = DecalMath.FaceIndex("+Z");
		Transform3D patch = DecalMath.LocalTransform(unit, Vector3.One, plusZ, 0f, 0f, 0.5f, 0.5f);
		Check("math: an unshifted patch is centred on the face, floated clear of it",
			Near(patch.Origin, new Vector3(0f, 0f, 0.5f + lift)));
		Check("math: the patch's basis faces the way the face does",
			Near(patch.Basis.Z.Normalized(), new Vector3(0f, 0f, 1f)));
		Check("math: the basis columns carry the patch's size",
			Near(patch.Basis.X.Length(), 0.5f) && Near(patch.Basis.Y.Length(), 0.5f));

		Transform3D shifted = DecalMath.LocalTransform(unit, Vector3.One, plusZ, 0.25f, -0.25f, 0.5f, 0.5f);
		Check("math: the offset shifts the patch across the face",
			Near(shifted.Origin, new Vector3(0.25f, -0.25f, 0.5f + lift)));
		Transform3D overreached = DecalMath.LocalTransform(unit, Vector3.One, plusZ, 5f, 0f, 0.5f, 0.5f);
		Check("math: an out-of-range offset clamps to the face edge",
			Near(overreached.Origin, new Vector3(0.25f, 0f, 0.5f + lift)));
		Check("math: the clamp leaves the patch fully on the face",
			Mathf.Abs(overreached.Origin.X) + 0.5f * 0.5f <= 0.5f + 1e-5f);

		// A non-uniformly scaled host: the local numbers divide the scale out, so rendering
		// multiplies it back exactly and the patch keeps its fraction of the face.
		Vector3 scale = new Vector3(2f, 1f, 0.5f);
		Transform3D scaled = DecalMath.LocalTransform(unit, scale, plusZ, 0f, 0f, 0.5f, 0.5f);
		float scaledLift = DecalMath.Lift(DecalMath.VisualSize(unit, scale));
		Check("math: a scaled host's face is measured in visual space",
			Near(scaled.Origin, new Vector3(0f, 0f, (0.5f * 0.5f + scaledLift) / 0.5f)));
		Vector3 renderedX = new Vector3(scaled.Basis.X.X * scale.X, scaled.Basis.X.Y * scale.Y, scaled.Basis.X.Z * scale.Z);
		Vector3 renderedY = new Vector3(scaled.Basis.Y.X * scale.X, scaled.Basis.Y.Y * scale.Y, scaled.Basis.Y.Z * scale.Z);
		Check("math: rendering the local basis through the host scale restores the intended size",
			Near(renderedX.Length(), 1f) && Near(renderedY.Length(), 0.5f));

		// The round trip the drag depends on: a point on the face maps back to its own (u, v).
		Vector3 visualPoint = new Vector3(0.2f, -0.1f, 0.5f);
		Check("math: a point on the face inverts back to its own offset",
			Near(DecalMath.OffsetsFromPoint(unit, Vector3.One, plusZ, visualPoint), new Vector2(0.2f, -0.1f)));

		int minusY = DecalMath.FaceIndex("-Y");
		Transform3D bottom = DecalMath.LocalTransform(unit, Vector3.One, minusY, 0f, 0f, 0.5f, 0.5f);
		Check("math: the -Y face places the patch below the part and faces it down",
			Near(bottom.Origin, new Vector3(0f, -(0.5f + lift), 0f))
			&& Near(bottom.Basis.Z.Normalized(), new Vector3(0f, -1f, 0f)));
	}

	/// <summary>
	/// A decal against a live host: attach placement, face changes, clamping, riding the host's
	/// motion, and the derived-transform contract. The fixture is deliberately NOT inside the
	/// SceneTree — adding to it from <c>_Ready</c> is not allowed — so world placement is asserted
	/// by composing the host's transform with the patch's own. Attaching calls the same method the
	/// tree-entry hook calls (<see cref="LotObject.RefreshDecalTransform"/>), which is what the
	/// running client's attach path executes.
	/// </summary>
	private static void TestLiveHost()
	{
		LotObject host = LotObject.Create(LotObjectKind.Cube, "DecalHost", new Color(0.5f, 0.5f, 0.5f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "Decal", new Color(1f, 1f, 1f));
		Check("live: a decal carries no collision body or shape",
			decal.CollisionBody == null && decal.CollisionShape == null);
		Check("live: a decal's mesh is a quad", decal.MeshInstance != null && decal.MeshInstance.Mesh is QuadMesh);
		Check("live: a decal's material keeps transparency",
			decal.Material.Transparency == BaseMaterial3D.TransparencyEnum.Alpha);
		decal.SetOutline(LotOutlineState.Selected);
		Check("live: outlining a decal is a safe no-op", decal.OutlineState == LotOutlineState.None);

		float lift = DecalMath.Lift(Vector3.One);
		host.AddChild(decal);
		decal.RefreshDecalTransform();
		Check("live: attaching places the patch on the +Z face",
			Near(decal.Position, new Vector3(0f, 0f, 0.5f + lift), 1e-3f));
		Check("live: the default patch is half the face and the default face is +Z",
			Near(decal.DecalScaleU, 0.5f) && Near(decal.DecalScaleV, 0.5f)
			&& decal.DecalFace == DecalMath.FaceIndex("+Z"));

		Check("live: the face can be changed",
			decal.SetDecalFace(DecalMath.FaceIndex("+X"))
			&& Near(decal.Position, new Vector3(0.5f + lift, 0f, 0f), 1e-3f));
		Check("live: an out-of-range face is refused and leaves the face alone",
			!decal.SetDecalFace(9) && decal.DecalFace == DecalMath.FaceIndex("+X"));

		decal.SetDecalOffsetU(0.4f);
		Check("live: the offset is clamped so the patch stays on the face",
			Near(decal.DecalOffsetU, 0.25f));
		decal.SetDecalScaleU(1.2f);
		decal.SetDecalScaleV(0.001f);
		Check("live: the scale is clamped",
			Near(decal.DecalScaleU, DecalMath.MaxScale) && Near(decal.DecalScaleV, DecalMath.MinScale));
		Check("live: enlarging the patch re-clamps its offset", Near(decal.DecalOffsetU, 0f));

		decal.SetDecalOpacity(0.5f);
		decal.Color = new Color(0.2f, 0.3f, 0.4f);
		Check("live: a tint write keeps the opacity", Near(decal.DecalOpacity, 0.5f));
		decal.SetDecalOpacity(5f);
		Check("live: the opacity is clamped", Near(decal.DecalOpacity, 1f));

		// On-face UI content: the label and the thumb are the panel's two internal content nodes,
		// and the content decides which of the three (quad, label, thumb) is visible.
		Label3D label = decal.GetNodeOrNull<Label3D>("Label");
		MeshInstance3D thumb = decal.GetNodeOrNull<MeshInstance3D>("Thumb");
		Check("panel: the content nodes exist as internal children",
			label != null && thumb != null
			&& label.HasMeta(LotObject.InternalChildMeta) && thumb.HasMeta(LotObject.InternalChildMeta));
		Check("panel: an Image panel shows neither label nor thumb",
			label != null && !label.Visible && thumb != null && !thumb.Visible);

		Check("panel: the content can be switched", decal.SetDecalContent((int)DecalContent.Text));
		Check("panel: a Text panel hides its quad and shows the label",
			!decal.MeshInstance.Visible && label.Visible);
		decal.SetDecalText("Hello face");
		Check("panel: the text reaches the label", label.Text == "Hello face");
		decal.SetDecalFontSize(999f);
		Check("panel: the font size is clamped and applied",
			Near(decal.DecalFontSize, DecalMath.MaxFontSize) && label.FontSize == (int)DecalMath.MaxFontSize);

		decal.SetDecalContent((int)DecalContent.Button);
		Check("panel: a Button shows both its surface and its label", decal.MeshInstance.Visible && label.Visible);
		decal.Color = new Color(0.05f, 0.05f, 0.05f);
		Check("panel: a button label contrasts with its surface", label.Modulate.R > 0.5f);

		decal.SetDecalContent((int)DecalContent.Scrollbar);
		Check("panel: a Scrollbar hides the label and shows the thumb", !label.Visible && thumb.Visible);
		decal.SetDecalScroll(1f);
		Check("panel: the thumb rides the scroll value", Near(thumb.Position.X, DecalMath.ScrollThumbOffset(1f)));
		Check("panel: an unknown content is refused",
			!decal.SetDecalContent(99) && decal.Content == DecalContent.Scrollbar);

		// The patch rides the host: nothing recomputes while the host moves or rotates.
		Transform3D patchBefore = decal.Transform;
		host.Position = new Vector3(3f, 2f, 0f);
		host.RotationDegrees = new Vector3(0f, 180f, 0f);
		Check("live: host motion leaves the patch's own transform alone (it is parented)",
			decal.Transform.IsEqualApprox(patchBefore));
		Check("live: the patch's world normal follows the host's rotation",
			Near((host.Transform * decal.Transform).Basis.Z.Normalized(), new Vector3(-1f, 0f, 0f), 1e-3f));

		// Scaling the host needs no refresh: the stored local numbers are scale-free, so the patch
		// keeps its authored fraction of the face while the face itself scales.
		host.Position = Vector3.Zero;
		host.RotationDegrees = Vector3.Zero;
		host.Scale = new Vector3(1f, 1f, 2f);
		Check("live: an un-refreshed patch still spans the same fraction of a scaled face",
			Near((host.Transform * decal.Transform).Basis.X.Length(), 2f, 1e-3f));
		decal.RefreshDecalTransform();
		Check("live: refreshing after a host scale change reproduces the same fraction",
			Near((host.Transform * decal.Transform).Basis.X.Length(), 2f, 1e-3f));

		Free(host);
	}

	/// <summary>The declaration table the Inspector, undo, the session snapshot and the archive all
	/// read: a decal gets the decal set, and a part is not offered it (and vice versa).</summary>
	private static void TestPropertyDeclarations()
	{
		string[] ids = { "decal_face", "decal_texture", "decal_offset_u", "decal_offset_v",
			"decal_scale_u", "decal_scale_v", "decal_opacity",
			"decal_content", "decal_text", "decal_scroll", "decal_font_size" };
		bool allDeclared = true;
		for (int i = 0; i < ids.Length; i++)
		{
			if (LotPropertyRegistry.Find(ids[i]) == null) allDeclared = false;
		}
		Check("properties: the decal property set is declared", allDeclared);

		LotObject part = LotObject.Create(LotObjectKind.Cube, "__decal_props_host", new Color(1f, 1f, 1f));
		LotObject decal = LotObject.Create(LotObjectKind.Decal, "__decal_props_decal", new Color(1f, 1f, 1f));
		List<LotPropertyDescriptor> buffer = new List<LotPropertyDescriptor>();

		LotPropertyRegistry.CollectFor(decal, buffer);
		Check("properties: a decal collects the decal set", buffer.Count == ids.Length);
		bool decalSetOnly = true;
		for (int i = 0; i < buffer.Count; i++)
		{
			if (!buffer[i].Id.StartsWith("decal_")) decalSetOnly = false;
		}
		Check("properties: a decal is not offered the part properties", decalSetOnly);

		LotPropertyRegistry.CollectFor(part, buffer);
		bool partHasNoDecalSet = true;
		for (int i = 0; i < buffer.Count; i++)
		{
			if (buffer[i].Id.StartsWith("decal_")) partHasNoDecalSet = false;
		}
		Check("properties: a part is not offered the decal properties", partHasNoDecalSet);

		// The descriptors are the write path the Inspector, undo and the archive share.
		LotPropertyDescriptor face = LotPropertyRegistry.Find("decal_face");
		face.SetChoice(decal, "+Y");
		Check("properties: writing through the descriptor moves the face",
			decal.DecalFace == DecalMath.FaceIndex("+Y") && face.GetChoice(decal) == "+Y");

		LotPropertyDescriptor offsetU = LotPropertyRegistry.Find("decal_offset_u");
		offsetU.SetFloat(decal, 0.9f);
		Check("properties: a float write is clamped by the setter", Near(decal.DecalOffsetU, 0.25f));

		LotPropertyDescriptor contentDescriptor = LotPropertyRegistry.Find("decal_content");
		contentDescriptor.SetChoice(decal, "Scrollbar");
		Check("properties: writing the content through its descriptor switches the panel",
			decal.Content == DecalContent.Scrollbar && contentDescriptor.GetChoice(decal) == "Scrollbar");
		contentDescriptor.SetChoice(decal, "NotAContent");
		Check("properties: an unknown content name is refused", decal.Content == DecalContent.Scrollbar);

		LotPropertyDescriptor textDescriptor = LotPropertyRegistry.Find("decal_text");
		textDescriptor.SetText(decal, "Score: 0");
		Check("properties: a text write reaches the panel",
			decal.DecalText == "Score: 0" && textDescriptor.GetText(decal) == "Score: 0");

		Free(decal);
		Free(part);
	}

	/// <summary>
	/// The archive contract a decal has to satisfy: captured under its host as a "decal" record
	/// carrying its authored values and its image reference, and restored back under the host with
	/// all of it intact. The zip/byte path is LotArchiveSelfTest's business; this pins the record
	/// shape and the scene rebuild — the two places a new node kind can silently go missing.
	/// </summary>
	private static void TestCaptureAndRestore()
	{
		Node3D sourceRoot = new Node3D();
		LotObject host = LotObject.Create(LotObjectKind.Cube, "Host", new Color(0.5f, 0.5f, 0.5f));
		sourceRoot.AddChild(host);

		string imagePath = TestImages.WritePng("user://decalselftest_image.png", new Color(0.9f, 0.2f, 0.2f, 0.5f));
		Check("capture: the fixture image could be written", imagePath.Length > 0);
		string importError = "";
		string assetId = imagePath.Length > 0 ? LotTextureCache.ImportFromFile(imagePath, out importError) : "";
		Check("capture: the fixture image registers", assetId.Length > 0 && importError.Length == 0);

		LotObject decal = LotObject.Create(LotObjectKind.Decal, "Logo", new Color(1f, 1f, 1f, 1f));
		host.AddChild(decal);
		decal.SetDecalFace(DecalMath.FaceIndex("+X"));
		decal.SetDecalOffsetU(0.2f);
		decal.SetDecalOffsetV(-0.1f);
		decal.SetDecalScaleU(0.4f);
		decal.SetDecalScaleV(0.3f);
		decal.SetDecalOpacity(0.75f);
		decal.SetDecalContent((int)DecalContent.Button);
		decal.SetDecalText("Play");
		decal.SetDecalScroll(0.35f);
		if (assetId.Length > 0) decal.SetTexture(assetId);

		List<LotArchive.LotNodeRecord> records = new List<LotArchive.LotNodeRecord>();
		LotSceneWalk.CaptureChildren(sourceRoot, records);
		Check("capture: the host and its patch are both recorded", records.Count == 2);

		int hostIndex = -1;
		int decalIndex = -1;
		for (int i = 0; i < records.Count; i++)
		{
			if (records[i].Kind == "cube") hostIndex = i;
			if (records[i].Kind == "decal") decalIndex = i;
		}
		Check("capture: the decal is recorded as its own kind", decalIndex >= 0);
		Check("capture: the decal's parent points at its host",
			decalIndex >= 0 && hostIndex >= 0 && decalIndex > hostIndex && records[decalIndex].ParentIndex == hostIndex);

		LotArchive.LotNodeRecord record = decalIndex >= 0 ? records[decalIndex] : null;
		Check("capture: the authored face is recorded",
			record != null && record.Properties.ContainsKey("decal_face")
			&& record.Properties["decal_face"].AsString() == "+X");
		Check("capture: the authored offset is recorded",
			record != null && record.Properties.ContainsKey("decal_offset_u")
			&& Near((float)record.Properties["decal_offset_u"].AsDouble(), 0.2f));
		Check("capture: the authored scale is recorded",
			record != null && record.Properties.ContainsKey("decal_scale_v")
			&& Near((float)record.Properties["decal_scale_v"].AsDouble(), 0.3f));
		Check("capture: the opacity is recorded",
			record != null && record.Properties.ContainsKey("decal_opacity")
			&& Near((float)record.Properties["decal_opacity"].AsDouble(), 0.75f));
		Check("capture: the image reference is recorded",
			record != null && record.Properties.ContainsKey(LotArchiveCollect.DecalTexturePropertyId)
			&& record.Properties[LotArchiveCollect.DecalTexturePropertyId].AsString() == assetId);
		Check("capture: the panel content is recorded",
			record != null && record.Properties.ContainsKey("decal_content")
			&& record.Properties["decal_content"].AsString() == "Button");
		Check("capture: the panel text is recorded",
			record != null && record.Properties.ContainsKey("decal_text")
			&& record.Properties["decal_text"].AsString() == "Play");

		// Restore into fresh containers: the path a loaded lot takes, where the patch's derived
		// transform comes back from its properties rather than from the record's stored transform.
		LotArchive.LotDocument document = new LotArchive.LotDocument();
		document.Objects = records;
		Node3D destRoot = new Node3D();
		LotSceneLoad.LoadReport report = new LotSceneLoad.LoadReport();
		bool restored = LotSceneLoad.Restore(destRoot, null, document, null, report);
		Check("restore: the restore itself succeeds", restored && report.PartsCreated == 2);

		LotObject backHost = destRoot.GetChildCount() > 0 ? destRoot.GetChild(0) as LotObject : null;
		LotObject backDecal = null;
		if (backHost != null)
		{
			for (int i = 0; i < backHost.GetChildCount(); i++)
			{
				LotObject child = backHost.GetChild(i) as LotObject;
				if (child != null && child.Kind == LotObjectKind.Decal) backDecal = child;
			}
		}
		Check("restore: the patch came back under its host", backDecal != null);
		Check("restore: the face survived",
			backDecal != null && backDecal.DecalFace == DecalMath.FaceIndex("+X"));
		Check("restore: the offset survived",
			backDecal != null && Near(backDecal.DecalOffsetU, 0.2f) && Near(backDecal.DecalOffsetV, -0.1f));
		Check("restore: the scale survived",
			backDecal != null && Near(backDecal.DecalScaleU, 0.4f) && Near(backDecal.DecalScaleV, 0.3f));
		Check("restore: the opacity survived", backDecal != null && Near(backDecal.DecalOpacity, 0.75f, 1e-3f));
		Check("restore: the image reference survived", backDecal != null && backDecal.TextureId == assetId);
		Check("restore: the panel content survived",
			backDecal != null && backDecal.Content == DecalContent.Button);
		Check("restore: the panel text survived", backDecal != null && backDecal.DecalText == "Play");
		Check("restore: the scroll value survived", backDecal != null && Near(backDecal.DecalScroll, 0.35f));
		Check("restore: the patch sits on the restored host's face",
			backDecal != null && Near(backDecal.Position.X, 0.5f + DecalMath.Lift(Vector3.One), 1e-3f));

		// Drop every reference this fixture took, so the suites that follow see a clean cache.
		if (backDecal != null) backDecal.ReleaseTexture();
		decal.ReleaseTexture();
		if (assetId.Length > 0) LotTextureCache.Release(assetId);
		Check("capture: the fixture image was fully released",
			assetId.Length == 0 || !LotTextureCache.Contains(assetId));

		Free(destRoot);
		Free(sourceRoot);
		RemoveIfPresent(imagePath);
	}

	/// <summary>
	/// The bound Lua verbs through the real VM (the scratch path, like the collision suite's): a
	/// script spawns a host, adds a decal and drives every decal verb, so a binding that went
	/// missing or changed shape fails here rather than in a creator's lot.
	/// </summary>
	private static void TestLuaBindings()
	{
		LuaManager lua = LuaManager.Instance;
		if (!lua.IsRuntimeAvailable)
		{
			Check("lua: decal verbs are bound and behave (runtime unavailable, skipped)", true);
			return;
		}

		// The chunk deliberately passes an unknown face, an unknown image id and a decal host once:
		// each binding warns (a WARNING in the log, not an error) and must leave the value alone,
		// which the asserts right after them pin down.
		const string script =
			"local host = Lot.SpawnCube(44, 8, 44)\n" +
			"assert(type(host) == 'number' and host >= 0, 'host spawn failed')\n" +
			"local d = Lot.SpawnDecal(host)\n" +
			"assert(type(d) == 'number' and d >= 0, 'decal spawn failed')\n" +
			"assert(Lot.GetDecalFace(d) == '+Z', 'a new decal should sit on +Z')\n" +
			"assert(Lot.SetDecalFace(d, '+X') == true, 'SetDecalFace should accept +X')\n" +
			"assert(Lot.GetDecalFace(d) == '+X', 'the face should stick')\n" +
			"assert(Lot.SetDecalFace(d, 'sideways') == false, 'an unknown face must be refused')\n" +
			"assert(Lot.GetDecalFace(d) == '+X', 'a refused face must leave the value alone')\n" +
			"Lot.SetDecalOffset(d, 0.4, -0.4)\n" +
			"assert(math.abs(Lot.GetDecalOffsetU(d) - 0.25) < 1e-6, 'the offset should clamp to 0.25')\n" +
			"Lot.SetDecalScale(d, 1.5, 0.001)\n" +
			"assert(math.abs(Lot.GetDecalScaleU(d) - 1.0) < 1e-6, 'the scale should clamp to 1')\n" +
			"assert(math.abs(Lot.GetDecalScaleV(d) - 0.05) < 1e-6, 'the scale should clamp to 0.05')\n" +
			"Lot.SetDecalOpacity(d, 0.5)\n" +
			"assert(math.abs(Lot.GetDecalOpacity(d) - 0.5) < 1e-6, 'the opacity should stick')\n" +
			"assert(Lot.SetDecalTexture(d, 'no-such-image') == false, 'an unknown image id must be refused')\n" +
			"assert(Lot.GetDecalTexture(d) == '', 'a refused image must not register')\n" +
			"assert(Lot.GetDecalContent(d) == 'Image', 'a new panel is an Image panel')\n" +
			"assert(Lot.SetDecalContent(d, 'Text') == true, 'SetDecalContent should accept Text')\n" +
			"assert(Lot.GetDecalContent(d) == 'Text', 'the content should stick')\n" +
			"assert(Lot.SetDecalContent(d, 'nonsense') == false, 'an unknown content must be refused')\n" +
			"assert(Lot.GetDecalContent(d) == 'Text', 'a refused content must leave the value alone')\n" +
			"Lot.SetDecalText(d, 'Hello from Lua')\n" +
			"assert(Lot.GetDecalText(d) == 'Hello from Lua', 'the text should stick')\n" +
			"Lot.SetDecalFontSize(d, 999)\n" +
			"assert(math.abs(Lot.GetDecalFontSize(d) - 160) < 1e-6, 'the font size should clamp to 160')\n" +
			"Lot.SetDecalScroll(d, 0.75)\n" +
			"assert(math.abs(Lot.GetDecalScroll(d) - 0.75) < 1e-6, 'the scroll value should stick')\n" +
			"assert(Lot.SpawnDecal(d) == -1, 'a decal cannot host another decal')\n" +
			"Lot.DestroyObject(host)";

		bool ran = lua.RunString(script, "decal_selftest");
		Check("lua: decal verbs are bound and behave" + (ran ? "" : " (" + lua.LastError + ")"), ran);
	}

	/// <summary>
	/// The decal tool's math: which face a clicked surface normal names, whether a click landed
	/// inside an existing panel, and the small colour/size rules behind the panel contents.
	/// </summary>
	private static void TestFaceTools()
	{
		Check("tool: a normal names the face it leans on",
			DecalMath.FaceFromNormal(new Vector3(0.9f, 0.1f, 0f)) == DecalMath.FaceIndex("+X")
			&& DecalMath.FaceFromNormal(new Vector3(-0.9f, 0.1f, 0f)) == DecalMath.FaceIndex("-X")
			&& DecalMath.FaceFromNormal(new Vector3(0f, 1f, 0.2f)) == DecalMath.FaceIndex("+Y")
			&& DecalMath.FaceFromNormal(new Vector3(0f, -0.4f, -1f)) == DecalMath.FaceIndex("-Z"));
		Check("tool: a diagonal normal still resolves to one face",
			DecalMath.FaceFromNormal(new Vector3(1f, 1f, 1f)) == DecalMath.FaceIndex("+X"));
		Check("tool: a zero normal falls back to the default face",
			DecalMath.FaceFromNormal(Vector3.Zero) == DecalMath.DefaultFace);

		// A point inside the default half-size panel on the +Z face of a unit cube.
		Vector3 unit = Vector3.One;
		Vector3 inside = new Vector3(0.1f, 0.1f, 0.5f);
		Vector3 outside = new Vector3(0.4f, 0f, 0.5f);
		Check("tool: a click inside a panel is recognised",
			DecalMath.CoversPoint(unit, Vector3.One, DecalMath.FaceIndex("+Z"), 0f, 0f, 0.5f, 0.5f,
				DecalMath.FaceIndex("+Z"), inside));
		Check("tool: a click outside a panel is bare surface",
			!DecalMath.CoversPoint(unit, Vector3.One, DecalMath.FaceIndex("+Z"), 0f, 0f, 0.5f, 0.5f,
				DecalMath.FaceIndex("+Z"), outside));
		Check("tool: a panel on another face never covers this face",
			!DecalMath.CoversPoint(unit, Vector3.One, DecalMath.FaceIndex("+X"), 0f, 0f, 0.5f, 0.5f,
				DecalMath.FaceIndex("+Z"), inside));

		Check("panel: a light surface gets a dark label and a dark surface a light one",
			DecalMath.LabelColorFor(new Color(0.95f, 0.95f, 0.95f)).R < 0.5f
			&& DecalMath.LabelColorFor(new Color(0.1f, 0.1f, 0.1f)).R > 0.5f);
		Check("panel: the scrollbar thumb is lifted off its track",
			DecalMath.ThumbColorFor(new Color(0.2f, 0.2f, 0.2f)).R > 0.2f);
		Check("panel: the thumb stays inside the track at both ends",
			Near(DecalMath.ScrollThumbOffset(0f), -0.5f * (1f - DecalMath.ScrollThumbWidth))
			&& Near(DecalMath.ScrollThumbOffset(1f), 0.5f * (1f - DecalMath.ScrollThumbWidth)));
		Check("panel: the scroll value is clamped",
			Near(DecalMath.ClampScroll(-1f), 0f) && Near(DecalMath.ClampScroll(2f), 1f));
		Check("panel: font size maps to a line height and is clamped",
			Near(DecalMath.LineFraction(48f), 0.3f)
			&& Near(DecalMath.ClampFontSize(1f), DecalMath.MinFontSize)
			&& Near(DecalMath.ClampFontSize(999f), DecalMath.MaxFontSize));
		Check("panel: the content names round-trip",
			DecalMath.ContentIndex("Text") == (int)DecalContent.Text
			&& DecalMath.ContentIndex("Scrollbar") == (int)DecalContent.Scrollbar
			&& DecalMath.ContentIndex("nonsense") == -1
			&& DecalMath.ClampContent(9) == DecalMath.DefaultContent);
	}

	private static void Check(string label, bool ok)
	{
		_checks++;
		if (!ok)
		{
			_failures++;
			GD.Print("[DecalSelfTest] FAIL  " + label);
		}
	}

	private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-4f)
	{
		return a.DistanceTo(b) <= tolerance;
	}

	private static bool Near(Vector2 a, Vector2 b, float tolerance = 1e-5f)
	{
		return a.DistanceTo(b) <= tolerance;
	}

	private static bool Near(float a, float b, float tolerance = 1e-5f)
	{
		return Mathf.Abs(a - b) <= tolerance;
	}

	/// <summary>Frees a fixture node (and its subtree). Safe for a node that was never added to
	/// the tree, which is how the capture/restore fixtures live.</summary>
	private static void Free(Node node)
	{
		if (node != null && GodotObject.IsInstanceValid(node)) node.Free();
	}

	/// <summary>Deletes a fixture file if it exists; the tests write real PNGs on purpose.</summary>
	private static void RemoveIfPresent(string path)
	{
		if (string.IsNullOrEmpty(path)) return;
		if (Godot.FileAccess.FileExists(path)) Godot.DirAccess.RemoveAbsolute(path);
	}
}
