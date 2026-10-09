using Godot;

/// <summary>
/// The creator-facing Lua API surface — the exact contract the xLua binding (roadmap milestone
/// 3.1) will register. Verb-first, primitives-only signatures so lot scripts never receive Godot
/// nodes; objects are addressed through integer handles from BuilderScene's registry. This class
/// is the ONLY sanctioned way for scripts to touch the lot tree (clinerules 1.1 / 2.2).
/// Coordinates: 3D position/rotation/scale are local to the lot (rotation in degrees), colors are
/// 0..1 floats, UI rects are pixels offset from the viewport's top-left corner (Y down).
/// </summary>
public class LotLuaApi
{
	private readonly BuilderScene _scene;

	public LotLuaApi(BuilderScene scene)
	{
		_scene = scene;
	}

	// --- 3D parts ---

	/// <summary>Spawns a unit cube at (x, y, z). Returns its object handle, or -1 on failure.</summary>
	public int SpawnCube(float x, float y, float z) { return Spawn3D(LotObjectKind.Cube, new Vector3(x, y, z)); }

	/// <summary>Spawns a unit sphere at (x, y, z). Returns its object handle, or -1 on failure.</summary>
	public int SpawnSphere(float x, float y, float z) { return Spawn3D(LotObjectKind.Sphere, new Vector3(x, y, z)); }

	/// <summary>Spawns a unit cylinder at (x, y, z). Returns its object handle, or -1 on failure.</summary>
	public int SpawnCylinder(float x, float y, float z) { return Spawn3D(LotObjectKind.Cylinder, new Vector3(x, y, z)); }

	/// <summary>
	/// Spawns the default capsule character (radius 0.5, total height 2.0) at (x, y, z).
	/// Returns its object handle, or -1 on failure.
	///
	/// Contract notes (milestone 3.4): the capsule's ORIGIN is its centre, so spawning at y = 1
	/// puts its feet on the ground plane. The spawned object is a plain LotObject with a static
	/// collision body — it does NOT move on its own. Making it controllable is the character
	/// controller's job (C# side, in the player camera's runtime), not this binding's: scripts
	/// drive it through SetPosition like any other part, which is what keeps the §1.1 boundary
	/// intact (no Godot node ever reaches Lua).
	/// </summary>
	public int SpawnCapsule(float x, float y, float z) { return Spawn3D(LotObjectKind.Capsule, new Vector3(x, y, z)); }

	/// <summary>
	/// Moves the object to (x, y, z), relative to the lot origin. Allowed on anchored parts too:
	/// anchoring controls gravity, not who may write a transform (a script can reposition an
	/// anchored part; it just will not fall afterwards).
	/// </summary>
	public void SetPosition(int handle, float x, float y, float z)
	{
		Node3D node = AsNode3D(handle, "SetPosition");
		if (node != null) node.Position = new Vector3(x, y, z);
	}

	public float GetPositionX(int handle) { Node3D n = AsNode3D(handle, "GetPositionX"); return n != null ? n.Position.X : 0f; }
	public float GetPositionY(int handle) { Node3D n = AsNode3D(handle, "GetPositionY"); return n != null ? n.Position.Y : 0f; }
	public float GetPositionZ(int handle) { Node3D n = AsNode3D(handle, "GetPositionZ"); return n != null ? n.Position.Z : 0f; }

	/// <summary>Sets the object's rotation in degrees, relative to the lot origin.</summary>
	public void SetRotationDeg(int handle, float x, float y, float z)
	{
		Node3D node = AsNode3D(handle, "SetRotationDeg");
		if (node != null) node.RotationDegrees = new Vector3(x, y, z);
	}

	public float GetRotationXDeg(int handle) { Node3D n = AsNode3D(handle, "GetRotationXDeg"); return n != null ? n.RotationDegrees.X : 0f; }
	public float GetRotationYDeg(int handle) { Node3D n = AsNode3D(handle, "GetRotationYDeg"); return n != null ? n.RotationDegrees.Y : 0f; }
	public float GetRotationZDeg(int handle) { Node3D n = AsNode3D(handle, "GetRotationZDeg"); return n != null ? n.RotationDegrees.Z : 0f; }

	/// <summary>Sets the object's scale (1 = natural size).</summary>
	public void SetScale(int handle, float x, float y, float z)
	{
		Node3D node = AsNode3D(handle, "SetScale");
		if (node != null) node.Scale = new Vector3(x, y, z);
	}

	public float GetScaleX(int handle) { Node3D n = AsNode3D(handle, "GetScaleX"); return n != null ? n.Scale.X : 1f; }
	public float GetScaleY(int handle) { Node3D n = AsNode3D(handle, "GetScaleY"); return n != null ? n.Scale.Y : 1f; }
	public float GetScaleZ(int handle) { Node3D n = AsNode3D(handle, "GetScaleZ"); return n != null ? n.Scale.Z : 1f; }

	// --- Part properties (milestone 2.3) ---
	// Verb-first, primitives-only bindings for the properties the Inspector edits, so creator
	// scripts read and write exactly the same state the editor shows (never a Godot object; a
	// texture is addressed by its opaque asset id). Names are kept in step with the descriptor ids
	// in LotPropertyRegistry, which is the single declaration table.

	/// <summary>
	/// Anchors or unanchors the part. Anchored parts stay in place; unanchored parts are affected
	/// by gravity in a player session (the dynamic-body layer lands with §3.6). Both states still
	/// accept transform writes from scripts — anchoring controls gravity, not who may move a part.
	/// </summary>
	public void SetAnchored(int handle, bool anchored)
	{
		LotObject part = AsPart(handle, "SetAnchored");
		if (part != null) part.SetAnchored(anchored);
	}

	/// <summary>True while the part is anchored. False for a missing handle.</summary>
	public bool IsAnchored(int handle)
	{
		LotObject part = AsPart(handle, "IsAnchored");
		return part != null && part.Anchored;
	}

	/// <summary>
	/// Sets whether the part blocks other objects. When off, solid things pass through it; the part
	/// stays selectable and draggable in the editor.
	/// </summary>
	public void SetCanCollide(int handle, bool canCollide)
	{
		LotObject part = AsPart(handle, "SetCanCollide");
		if (part != null) part.SetCanCollide(canCollide);
	}

	/// <summary>True while the part blocks other objects. False for a missing handle.</summary>
	public bool GetCanCollide(int handle)
	{
		LotObject part = AsPart(handle, "GetCanCollide");
		return part != null && part.CanCollide;
	}

	/// <summary>
	/// Assigns the part's named collision group (milestone 3.5). Valid names are those on
	/// <see cref="LotCollisionGroups.Names"/> ("Default", "Terrain", "Character", ...). An unknown
	/// name is ignored (the part keeps its group), so a typo cannot silently drop it off the physics
	/// layers. Which groups interact is set with <see cref="SetGroupsCollidable"/>, not here.
	///
	/// Side effect: the part's collision layer becomes the group's bit and its mask becomes the
	/// group's row of the interaction matrix.
	/// </summary>
	public void SetCollisionGroup(int handle, string group)
	{
		LotObject part = AsPart(handle, "SetCollisionGroup");
		if (part == null) return;
		if (!LotCollisionGroups.IsKnown(group))
		{
			Warn("[LotLuaApi] SetCollisionGroup: unknown group '" + group + "'; the part stays '" +
				part.CollisionGroup + "'");
			return;
		}
		part.SetCollisionGroup(group);
	}

	/// <summary>The part's named collision group, or "" for a missing handle.</summary>
	public string GetCollisionGroup(int handle)
	{
		LotObject part = AsPart(handle, "GetCollisionGroup");
		return part != null ? part.CollisionGroup : "";
	}

	/// <summary>
	/// Turns interaction between two named groups on or off, for the whole lot. Returns false when
	/// either name is unknown, so a script can tell a typo from a success.
	///
	/// Authority (§1.3): this changes what collides for everyone, so it is host-authoritative —
	/// over a network a client must send it through <c>net.server</c>, and only the host's copy is
	/// trusted. In single-player and Test mode there is no peer, so it applies directly.
	///
	/// Side effect: every part's collision mask is recomputed (an O(parts) walk, not per-frame).
	/// </summary>
	public bool SetGroupsCollidable(string groupA, string groupB, bool collides)
	{
		if (!LotCollisionGroups.SetCollidesByName(groupA, groupB, collides)) return false;
		if (_scene != null) _scene.RefreshCollision();
		return true;
	}

	/// <summary>True when two named groups interact. False when either name is unknown.</summary>
	public bool GetGroupsCollidable(string groupA, string groupB)
	{
		return LotCollisionGroups.GetCollidesByName(groupA, groupB);
	}

	// --- mechanical constraints (§3.6) -------------------------------------------------------------
	//
	// Welds and hinges are created/edited here exactly as the editor creates them; the records are
	// the source of truth and the engine builds joints from them on session entry (and rebuilds on
	// any change made while a session runs). Authority (§1.3): these mutate the lot, so over a
	// network the host is the writer — send them through net.server, never trust a client's copy.

	/// <summary>
	/// Welds two parts: they keep the relative position/orientation they have right now and move as
	/// one from then on. Returns the link's id, or -1 when a handle is not a part (or both handles
	/// are the same part). Welding an already-welded pair returns the existing link rather than
	/// stacking a second one.
	///
	/// Side effect: entering a player session rebuilds the lot's joints, so an unanchored pair
	/// starts moving as one from the next session on (and immediately if one is already running).
	/// </summary>
	public int WeldParts(int handleA, int handleB)
	{
		LotObject partA = AsPart(handleA, "WeldParts");
		LotObject partB = AsPart(handleB, "WeldParts");
		if (partA == null || partB == null) return -1;
		if (handleA == handleB)
		{
			Warn("[LotLuaApi] WeldParts: a part cannot be welded to itself");
			return -1;
		}

		LotConstraintRecord existing = LotConstraints.FindWeld(handleA, handleB);
		if (existing != null) return existing.Id;

		LotConstraints.CanonicalPair(handleA, handleB, out int first, out int second);
		LotConstraintRecord record = LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Weld,
			A = first,
			B = second
		});
		if (_scene != null) _scene.RebuildConstraints();
		return record.Id;
	}

	/// <summary>Removes the weld between two parts (pair order does not matter). False when there is none.</summary>
	public bool Unweld(int handleA, int handleB)
	{
		LotConstraintRecord record = LotConstraints.FindWeld(handleA, handleB);
		if (record == null) return false;
		LotConstraints.Remove(record.Id);
		if (_scene != null) _scene.RebuildConstraints();
		return true;
	}

	/// <summary>True when an enabled weld holds between the two parts.</summary>
	public bool IsWelded(int handleA, int handleB)
	{
		return LotConstraints.IsWelded(handleA, handleB);
	}

	private static bool TryParseHingeAxis(string axis, out Vector3 parsed)
	{
		parsed = Vector3.Up;
		string normalized = (axis ?? "").Trim().ToLowerInvariant();
		if (normalized == "x") { parsed = Vector3.Right; return true; }
		if (normalized == "y") { parsed = Vector3.Up; return true; }
		if (normalized == "z") { parsed = Vector3.Back; return true; }
		// The negative spellings come free with a free-direction axis, so they are accepted too.
		if (normalized == "-x") { parsed = Vector3.Left; return true; }
		if (normalized == "-y") { parsed = Vector3.Down; return true; }
		if (normalized == "-z") { parsed = Vector3.Forward; return true; }
		return false;
	}

	/// <summary>
	/// Creates a hinge between two parts, or between a part and the world (-1 as the second side) —
	/// the "door in an invisible wall" form. The pivot is a world-space point (usually where the
	/// ray hit the first part); it is stored relative to that part, so moving the part carries the
	/// hinge along. Returns the link's id, or -1 for a bad handle or an unknown axis.
	///
	/// Axis is "x", "y", "z" (or "-x"/"-y"/"-z") in the first part's frame. For any other direction
	/// — an angled hinge — create it and aim it with <see cref="SetHingeAxis"/>, or use the editor's
	/// sharp axis tools. The hinge starts free (no limits, no motor); shape it with
	/// <see cref="SetHingeLimits"/> and <see cref="SetHingeMotor"/>.
	/// </summary>
	public int CreateHinge(int handleA, int handleB, string axis, float px, float py, float pz)
	{
		LotObject partA = AsPart(handleA, "CreateHinge");
		if (partA == null) return -1;
		if (handleB != LotConstraintRecord.WorldHandle)
		{
			LotObject partB = AsPart(handleB, "CreateHinge");
			if (partB == null) return -1;
			if (handleA == handleB)
			{
				Warn("[LotLuaApi] CreateHinge: a part cannot be hinged to itself");
				return -1;
			}
		}

		Vector3 parsed;
		if (!TryParseHingeAxis(axis, out parsed))
		{
			Warn("[LotLuaApi] CreateHinge: unknown axis '" + axis + "' (use \"x\", \"y\" or \"z\")");
			return -1;
		}

		LotConstraintRecord record = LotConstraints.Add(new LotConstraintRecord
		{
			Kind = LotConstraintKind.Hinge,
			A = handleA,
			B = handleB,
			Axis = parsed,
			Pivot = partA.GlobalTransform.AffineInverse() * new Vector3(px, py, pz)
		});
		if (_scene != null) _scene.RebuildConstraints();
		return record.Id;
	}

	/// <summary>
	/// Sets a hinge's angular limits in degrees (relative to the pose captured when the session
	/// built the joint). Pass <paramref name="enabled"/> false for a free hinge. Lower/upper are
	/// swapped when handed in reversed, so a limit can never be built inside-out. False for an
	/// unknown id or a non-hinge link.
	/// </summary>
	public bool SetHingeLimits(int id, float lowerDeg, float upperDeg, bool enabled)
	{
		LotConstraintRecord record = LotConstraints.Find(id);
		if (record == null || record.Kind != LotConstraintKind.Hinge) return false;
		if (lowerDeg > upperDeg)
		{
			float swap = lowerDeg;
			lowerDeg = upperDeg;
			upperDeg = swap;
		}
		record.LowerDeg = lowerDeg;
		record.UpperDeg = upperDeg;
		record.LimitsEnabled = enabled;
		if (_scene != null && _scene.ConstraintSession != null) _scene.ConstraintSession.ApplyHingeParams(id);
		return true;
	}

	/// <summary>
	/// Drives a hinge: mode "off" is a free hinge, "spin" turns it at
	/// <paramref name="velocity"/> degrees/second with at most <paramref name="maxPush"/> impulse
	/// (0 = unlimited, per Godot). Negative velocity reverses. False for an unknown id, a non-hinge
	/// link, or an unknown mode — so a typo cannot silently stop a motor.
	/// </summary>
	public bool SetHingeMotor(int id, string mode, float velocity, float maxPush)
	{
		LotConstraintRecord record = LotConstraints.Find(id);
		if (record == null || record.Kind != LotConstraintKind.Hinge) return false;

		string normalized = (mode ?? "").Trim().ToLowerInvariant();
		if (normalized == "off") record.Motor = HingeMotorMode.Off;
		else if (normalized == "spin") record.Motor = HingeMotorMode.Spin;
		else
		{
			Warn("[LotLuaApi] SetHingeMotor: unknown mode '" + mode + "' (use \"off\" or \"spin\")");
			return false;
		}
		record.MotorVelocity = velocity;
		record.MotorMaxPush = Mathf.Max(0f, maxPush);
		if (_scene != null && _scene.ConstraintSession != null) _scene.ConstraintSession.ApplyHingeParams(id);
		return true;
	}

	/// <summary>
	/// Aims a hinge's axis at (x, y, z) in world space — any direction, which is the point of this
	/// verb: the string form on <see cref="CreateHinge"/> only spells the six axis directions, and
	/// an angled hinge needs this. The vector is normalized; a zero vector is refused (with a
	/// warning) rather than guessed at. False for an unknown id or a non-hinge link. Rebuilds the
	/// session's joints, so it applies live inside a running session too.
	/// </summary>
	public bool SetHingeAxis(int id, float x, float y, float z)
	{
		LotConstraintRecord record = LotConstraints.Find(id);
		if (record == null || record.Kind != LotConstraintKind.Hinge) return false;

		Vector3 axis;
		if (!HingeAxisMath.TryNormalize(new Vector3(x, y, z), out axis))
		{
			Warn("[LotLuaApi] SetHingeAxis: the axis must be a non-zero direction");
			return false;
		}
		record.Axis = axis;
		if (_scene != null) _scene.RebuildConstraints();
		return true;
	}

	/// <summary>Removes any constraint (weld or hinge) by id. False for an unknown id.</summary>
	public bool RemoveConstraint(int id)
	{
		if (!LotConstraints.Remove(id)) return false;
		if (_scene != null) _scene.RebuildConstraints();
		return true;
	}

	/// <summary>
	/// Applies a texture by asset id ("" clears it). Returns true when the id was applied, false
	/// when it is unknown — so a script can tell a typo from a success. Asset ids are opaque; get
	/// one from the Inspector's Texture dropdown or from <see cref="GetTexture"/>.
	/// </summary>
	public bool SetTexture(int handle, string assetId)
	{
		LotObject part = AsPart(handle, "SetTexture");
		if (part == null) return false;
		return part.SetTexture(assetId);
	}

	/// <summary>The part's texture asset id, or "" when it has none.</summary>
	public string GetTexture(int handle)
	{
		LotObject part = AsPart(handle, "GetTexture");
		return part != null ? part.TextureId : "";
	}

	// --- Decals (milestone 2.6) ---
	// A decal is an image patch on ONE face of a part. It is spawned into a host part and stays
	// parented to it, so the image follows the part. Every value is a primitive (a face name and an
	// opaque asset id), every write goes through the part's own clamping methods, and the same
	// state is what the Inspector's Decal section edits.

	/// <summary>
	/// Spawns an image decal on the part with the given handle, parented to it. Returns the decal's
	/// handle, or -1 when the handle is not a part (a decal cannot host another decal). The new
	/// decal starts as a plain white half-size square on the part's +Z (front) face until an image
	/// is set with <see cref="SetDecalTexture"/>.
	/// </summary>
	public int SpawnDecal(int hostHandle)
	{
		if (_scene == null) return -1;
		LotObject host = AsPart(hostHandle, "SpawnDecal");
		if (host == null) return -1;
		if (host.Kind == LotObjectKind.Decal)
		{
			Warn("[LotLuaApi] SpawnDecal: a decal cannot host another decal");
			return -1;
		}

		LotObject decal = _scene.SpawnDecal(host);
		int handle = _scene.RegisterHandle(decal);
		TagLoadTimeSpawn(decal);
		return handle;
	}

	/// <summary>
	/// Moves the decal to another face of its host: one of "+Z", "-Z", "+X", "-X", "+Y", "-Y" (the
	/// Inspector's Face list). Returns false for a bad handle or an unknown face name, so a script
	/// can tell a typo from a success.
	/// </summary>
	public bool SetDecalFace(int handle, string face)
	{
		LotObject decal = AsDecal(handle, "SetDecalFace");
		if (decal == null) return false;
		return decal.SetDecalFace(DecalMath.FaceIndex(face));
	}

	/// <summary>The decal's current face name ("+Z" .. "-Y"), or "" for a bad handle.</summary>
	public string GetDecalFace(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalFace");
		return decal != null ? DecalMath.FaceNames[decal.DecalFace] : "";
	}

	/// <summary>
	/// Sets the decal's image by asset id ("" clears it back to a plain square). Returns false when
	/// the id is unknown — the same contract as <see cref="SetTexture"/>. The image's transparent
	/// areas stay transparent on the part.
	/// </summary>
	public bool SetDecalTexture(int handle, string assetId)
	{
		LotObject decal = AsDecal(handle, "SetDecalTexture");
		return decal != null && decal.SetTexture(assetId);
	}

	/// <summary>The decal's image asset id, or "" when it has none.</summary>
	public string GetDecalTexture(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalTexture");
		return decal != null ? decal.TextureId : "";
	}

	/// <summary>
	/// Slides the image across its face, in fractions of the face: u is horizontal, v vertical
	/// (-0.5 .. 0.5). Both values are clamped so the image stays entirely on the face.
	/// </summary>
	public void SetDecalOffset(int handle, float u, float v)
	{
		LotObject decal = AsDecal(handle, "SetDecalOffset");
		if (decal == null) return;
		decal.SetDecalOffsetU(u);
		decal.SetDecalOffsetV(v);
	}

	/// <summary>Horizontal slide of the image (-0.5 .. 0.5 of the face width). 0 for a bad handle.</summary>
	public float GetDecalOffsetU(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalOffsetU");
		return decal != null ? decal.DecalOffsetU : 0f;
	}

	/// <summary>Vertical slide of the image (-0.5 .. 0.5 of the face height). 0 for a bad handle.</summary>
	public float GetDecalOffsetV(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalOffsetV");
		return decal != null ? decal.DecalOffsetV : 0f;
	}

	/// <summary>
	/// Sizes the image as a fraction of its face (0.05 .. 1); the offset is re-clamped because a
	/// bigger image has less room to move. Returns silently for a bad handle.
	/// </summary>
	public void SetDecalScale(int handle, float u, float v)
	{
		LotObject decal = AsDecal(handle, "SetDecalScale");
		if (decal == null) return;
		decal.SetDecalScaleU(u);
		decal.SetDecalScaleV(v);
	}

	/// <summary>Image width as a fraction of the face width (0.05 .. 1). 0 for a bad handle.</summary>
	public float GetDecalScaleU(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalScaleU");
		return decal != null ? decal.DecalScaleU : 0f;
	}

	/// <summary>Image height as a fraction of the face height (0.05 .. 1). 0 for a bad handle.</summary>
	public float GetDecalScaleV(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalScaleV");
		return decal != null ? decal.DecalScaleV : 0f;
	}

	/// <summary>
	/// Sets how solid the decal's image is (0 invisible .. 1 opaque, clamped). Transparent pixels
	/// of the image stay transparent at any opacity, so this fades the whole patch.
	/// </summary>
	public void SetDecalOpacity(int handle, float opacity)
	{
		LotObject decal = AsDecal(handle, "SetDecalOpacity");
		if (decal != null) decal.SetDecalOpacity(opacity);
	}

	/// <summary>The decal's opacity (0 .. 1). 0 for a bad handle.</summary>
	public float GetDecalOpacity(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalOpacity");
		return decal != null ? decal.DecalOpacity : 0f;
	}

	// --- Decal panel content (on-face UI) ---

	/// <summary>
	/// Switches what the panel draws: "Image", "Text", "Button" or "Scrollbar". False for a bad
	/// handle or an unknown name. Button and Scrollbar are drawn and configurable; their click
	/// handling waits on the event system (§3.7).
	/// </summary>
	public bool SetDecalContent(int handle, string content)
	{
		LotObject decal = AsDecal(handle, "SetDecalContent");
		if (decal == null) return false;
		return decal.SetDecalContent(DecalMath.ContentIndex(content));
	}

	/// <summary>The panel's content name, or "" for a bad handle.</summary>
	public string GetDecalContent(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalContent");
		return decal != null ? DecalMath.ContentNames[(int)decal.Content] : "";
	}

	/// <summary>Sets the text a Text or Button panel shows (wraps at the panel's edge).</summary>
	public void SetDecalText(int handle, string text)
	{
		LotObject decal = AsDecal(handle, "SetDecalText");
		if (decal != null) decal.SetDecalText(text);
	}

	/// <summary>The panel's text, or "" when it has none (or for a bad handle).</summary>
	public string GetDecalText(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalText");
		return decal != null ? decal.DecalText : "";
	}

	/// <summary>Sets the scrollbar thumb position, 0 (top) .. 1 (bottom), clamped.</summary>
	public void SetDecalScroll(int handle, float value)
	{
		LotObject decal = AsDecal(handle, "SetDecalScroll");
		if (decal != null) decal.SetDecalScroll(value);
	}

	/// <summary>The scrollbar thumb position (0 .. 1). 0 for a bad handle.</summary>
	public float GetDecalScroll(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalScroll");
		return decal != null ? decal.DecalScroll : 0f;
	}

	/// <summary>Sets the label font size (8 .. 160), clamped. One line takes FontSize/160 of the
	/// panel's height, so the text scales with the panel and with the part.</summary>
	public void SetDecalFontSize(int handle, float size)
	{
		LotObject decal = AsDecal(handle, "SetDecalFontSize");
		if (decal != null) decal.SetDecalFontSize(size);
	}

	/// <summary>The label font size (8 .. 160). 0 for a bad handle.</summary>
	public float GetDecalFontSize(int handle)
	{
		LotObject decal = AsDecal(handle, "GetDecalFontSize");
		return decal != null ? decal.DecalFontSize : 0f;
	}

	/// <summary>Sets the mesh color (0..1 floats). Only applies to 3D parts.</summary>
	public void SetColor(int handle, float r, float g, float b)
	{
		Node node = Resolve(handle, "SetColor");
		LotObject lotObject = node as LotObject;
		if (lotObject != null) lotObject.Color = new Color(r, g, b);
		else WarnMissing(handle, "SetColor");
	}

	/// <summary>Renames the object in the hierarchy.</summary>
	public void SetName(int handle, string name)
	{
		Node node = Resolve(handle, "SetName");
		if (node != null && !string.IsNullOrWhiteSpace(name)) node.Name = name.Trim();
	}

	public string GetName(int handle)
	{
		Node node = Resolve(handle, "GetName");
		return node != null ? node.Name.ToString() : "";
	}

	/// <summary>Resolves an entity name to its handle, so a script can address another entity by
	/// name rather than a hardcoded number. Returns the FIRST entity with that name in lot order
	/// (the lowest handle) and -1 when nothing matches. Names are only unique among siblings, so
	/// when several entities share a name the earliest one wins silently (rule A). Entities only —
	/// script placeholders and internal children are not addressable. The inverse of
	/// <see cref="GetName(int)"/>.</summary>
	public int GetHandle(string name)
	{
		if (_scene == null || string.IsNullOrEmpty(name)) return -1;
		return _scene.FindHandleByName(name);
	}

	/// <summary>Destroys the object. Its handle becomes invalid afterwards.</summary>
	public void DestroyObject(int handle)
	{
		Node node = Resolve(handle, "DestroyObject");
		if (node == null) return;
		// The single destroy path: it forgets the handle, drops the entity's net bookkeeping and
		// releases the part's texture reference, all of which a bare QueueFree would leave behind.
		_scene.DestroyEntity(node);
	}

	// --- UI elements ---

	/// <summary>Spawns a UI frame at (x, y) with the given size. Returns its handle, or -1.</summary>
	public int SpawnUIFrame(float x, float y, float w, float h) { return SpawnUiInternal(LotUIKind.Frame, new Vector2(x, y), new Vector2(w, h), ""); }

	/// <summary>Spawns a UI button with the given label. Returns its handle, or -1.</summary>
	public int SpawnUIButton(float x, float y, float w, float h, string label) { return SpawnUiInternal(LotUIKind.Button, new Vector2(x, y), new Vector2(w, h), label); }

	/// <summary>Spawns a UI text label at (x, y). Returns its handle, or -1.</summary>
	public int SpawnUIText(float x, float y, string label) { return SpawnUiInternal(LotUIKind.Text, new Vector2(x, y), LotUiRenderer.DefaultSize(LotUIKind.Text), label); }

	/// <summary>Spawns a UI scrollbar at (x, y) with the given size. Returns its handle, or -1.</summary>
	public int SpawnUIScrollbar(float x, float y, float w, float h) { return SpawnUiInternal(LotUIKind.Scrollbar, new Vector2(x, y), new Vector2(w, h), ""); }

	/// <summary>Sets the label text of a Button or Text element.</summary>
	public void SetUIText(int handle, string text)
	{
		LotUIElement el = AsUiElement(handle, "SetUIText");
		if (el != null) el.Label = text;
	}

	public string GetUIText(int handle)
	{
		LotUIElement el = AsUiElement(handle, "GetUIText");
		return el != null ? el.Label : "";
	}

	/// <summary>Sets the element color (0..1 floats).</summary>
	public void SetUIColor(int handle, float r, float g, float b, float a)
	{
		LotUIElement el = AsUiElement(handle, "SetUIColor");
		if (el != null) el.Color = new Color(r, g, b, a);
	}

	/// <summary>Sets the element rect (x, y from the viewport top-left corner, Y down). Clamped so a
	/// script cannot place an element outside the viewport picture frame.</summary>
	public void SetUIRect(int handle, float x, float y, float w, float h)
	{
		LotUIElement el = AsUiElement(handle, "SetUIRect");
		if (el != null)
		{
			Vector2 pos = new Vector2(x, y);
			Vector2 size = new Vector2(w, h);
			UiGizmoMath.ClampRect(ref pos, ref size, _scene.UiCanvasBounds);
			el.Position = pos;
			el.Size = size;
		}
	}

	/// <summary>
	/// Prints a message to the Output window and the engine console — the destination of a script's
	/// <c>log(...)</c> and <c>print(...)</c>. Creator-side logging, so it is an Info entry, not a
	/// warning.
	/// </summary>
	public void Log(string message)
	{
		LotLog.Info("log", message);
		GD.Print("[Lot] " + message);
	}

	// --- net.* plumbing ---
	//
	// These two are NOT creator API: LuaManager registers them as globals (the bootstrap calls
	// them by name) but keeps them out of the documented Lot table. The bootstrap is the only
	// caller — see LuaBootstrap.Chunk for the sugar that reaches them.

	/// <summary>The net bridge, assigned by LuaManager once the runtime is up (null before then).</summary>
	public LuaNetBridge Net { get; set; }

	/// <summary>Records a net.* declaration so the router can warn when two scripts share a name.
	/// Called by the bootstrap's __newindex trap only.</summary>
	public void NetRegister(int handle, string direction, string name, string scriptName)
	{
		if (Net == null) return;
		Net.RegisterHandler(handle, direction, name, scriptName);
	}

	/// <summary>
	/// Cross-entity call (step 5): invokes the TARGET handle's net.server function through the same
	/// router/queue/envelope path as the caller's own net.* table. From a client the call goes to
	/// the host; from the host it is queued locally. Unknown handles/names warn and drop.
	/// Creator-facing form is the vararg sugar in the bootstrap (Lot.CallServer(handle, name, ...));
	/// this raw form takes a packed { n = count, ... } table.
	/// </summary>
	public void CallServer(int handle, string name, NLua.LuaTable args)
	{
		RouteCrossEntity(handle, NetDirection.Server, name, args);
	}

	/// <summary>Cross-entity call to the TARGET handle's net.client function: the host broadcasts
	/// it (and runs it locally, since the host is a player too); a client runs it locally for its
	/// own view only — clients never route to other clients.</summary>
	public void CallClient(int handle, string name, NLua.LuaTable args)
	{
		RouteCrossEntity(handle, NetDirection.Client, name, args);
	}

	private void RouteCrossEntity(int handle, NetDirection direction, string name, NLua.LuaTable args)
	{
		if (Net == null)
		{
			Warn("[LotLuaApi] net bridge is not ready; dropped cross-entity call " + name);
			if (args != null) args.Dispose();
			return;
		}
		Net.InvokeCrossEntity(handle, direction, name, args);
	}

	/// <summary>Funnels one net.* call from the bootstrap (args packed as { n = count, ... }).
	/// Serialization errors throw NetSerializationException, which surfaces in the calling script.</summary>
	public void NetInvoke(int handle, string direction, string name, NLua.LuaTable args)
	{
		if (Net == null)
		{
			Warn("[LotLuaApi] net bridge is not ready; dropped net." + direction + "." + name);
			if (args != null) args.Dispose();
			return;
		}
		Net.InvokeFromLua(handle, direction, name, args);
	}

	// --- event-subscription plumbing (milestone 3.7) ---
	//
	// These two are NOT creator API: the bootstrap's per-env Subscribe sugar calls them by name
	// (like NetInvoke/NetRegister) and LuaManager keeps them out of the documented Lot table. The
	// callback itself never crosses this boundary — it stays in the bootstrap's Lua-side registry
	// (design doc D4) — so this pair only mirrors subscription presence into the C#
	// LotEventRegistry, which owns presence, sensors and delivery.

	/// <summary>Records that <paramref name="handle"/> watches <paramref name="eventName"/> on
	/// <paramref name="targetHandle"/>. Called by the bootstrap's Subscribe sugar only (idempotent:
	/// the sugar calls it on both its append and replace paths); a refusal (bad handle, unknown
	/// event) is warned about by the registry and otherwise ignored.</summary>
	public void SubscribeEvent(int handle, int targetHandle, string eventName)
	{
		if (_scene == null || _scene.Events == null) return;
		_scene.Events.Subscribe(handle, targetHandle, eventName);
	}

	/// <summary>Removes one (subscriber, subject, event) subscription. Called by the bootstrap's
	/// cancel token only, and only once no script on the entity still watches the pair.</summary>
	public void UnsubscribeEvent(int handle, int targetHandle, string eventName)
	{
		if (_scene == null || _scene.Events == null) return;
		_scene.Events.Unsubscribe(handle, targetHandle, eventName);
	}

	// --- internals ---

	private int Spawn3D(LotObjectKind kind, Vector3 at)
	{
		if (_scene == null) return -1;
		LotObject obj = _scene.SpawnPrimitive(kind, at);
		int handle = _scene.RegisterHandle(obj);
		TagLoadTimeSpawn(obj);
		return handle;
	}

	/// <summary>
	/// Marks a node spawned while a script's initial load was running as owned by that script (F2):
	/// the reload cleanup deletes owned nodes before re-running the script, which is what stops a
	/// "spawn the capsule in the root script" pattern from duplicating them. Handler-time spawns are
	/// deliberately left untagged (they are runtime state).
	/// </summary>
	private void TagLoadTimeSpawn(Node node)
	{
		LuaNetBridge net = Net;
		if (net == null || node == null) return;
		if (net.LoadOwnerHandle < 0) return;
		node.SetMeta(LoadSpawnOwnerMeta, net.LoadOwnerHandle);
		node.SetMeta(LoadSpawnOwnerScriptMeta, net.LoadOwnerScript ?? "");
	}

	/// <summary>Meta marking a load-time spawn's owner (entity handle).</summary>
	public const string LoadSpawnOwnerMeta = "openlot_spawn_owner";

	/// <summary>Meta recording which script's load spawned the node.</summary>
	public const string LoadSpawnOwnerScriptMeta = "openlot_spawn_owner_script";

	private int SpawnUiInternal(LotUIKind kind, Vector2 pos, Vector2 size, string label)
	{
		if (_scene == null) return -1;
		// Same frame constraint as the editor: spawned UI always lands inside the picture frame.
		UiGizmoMath.ClampRect(ref pos, ref size, _scene.UiCanvasBounds);
		LotUIElement el = _scene.SpawnUIElement(kind, pos, size, label);
		int handle = _scene.RegisterHandle(el);
		TagLoadTimeSpawn(el);
		return handle;
	}

	private Node Resolve(int handle, string api)
	{
		Node node = _scene != null ? _scene.GetByHandle(handle) : null;
		if (node == null) Warn("[LotLuaApi] " + api + ": no lot object for handle " + handle);
		return node;
	}

	private Node3D AsNode3D(int handle, string api)
	{
		return Resolve(handle, api) as Node3D;
	}

	/// <summary>Resolves a handle to a 3D part, or null with a warning when it is not one.</summary>
	private LotObject AsPart(int handle, string api)
	{
		Node node = Resolve(handle, api);
		if (node == null) return null;
		LotObject part = node as LotObject;
		if (part == null) Warn("[LotLuaApi] " + api + ": handle " + handle + " is not a 3D part");
		return part;
	}

	/// <summary>Resolves a handle to a decal, or null with a warning when it is not one — so a script
	/// pointing a decal verb at a part is told why nothing happened.</summary>
	private LotObject AsDecal(int handle, string api)
	{
		Node node = Resolve(handle, api);
		if (node == null) return null;
		LotObject lot = node as LotObject;
		if (lot == null || lot.Kind != LotObjectKind.Decal)
		{
			Warn("[LotLuaApi] " + api + ": handle " + handle + " is not a decal");
			return null;
		}
		return lot;
	}

	private LotUIElement AsUiElement(int handle, string api)
	{
		return Resolve(handle, api) as LotUIElement;
	}

	/// <summary>
	/// Reports a creator-facing misuse to the Output window and the engine console in one step.
	/// Every warning in this file goes through here, so the window sees them without a second call
	/// site per message; the engine line stays for headless runs.
	/// </summary>
	private static void Warn(string message)
	{
		LotLog.Warn("lua", message);
		GD.PushWarning(message);
	}

	private void WarnMissing(int handle, string api)
	{
		Warn("[LotLuaApi] " + api + ": no lot object for handle " + handle);
	}
}