using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Test-mode isolation (§3.4 + §3.6): a snapshot of the authored lot taken when a player session
/// starts and restored when it ends, so playing can never change the build-mode world.
///
/// The session runs on the live tree (that is the §3.4 design — the lot reloads and its scripts run
/// on entry), so "load the lot as an instance" is implemented as: remember exactly what the authored
/// world looked like, hold the parts the session's reload would otherwise destroy, and put it all back
/// on exit — without running the scripts again. What is remembered:
///   * every entity that existed at entry — 3D parts: local transform, color, visibility and every
///     declared §2.3 property (anchored, collision group, texture, ...); UI elements: rect, label,
///     color, visibility;
///   * the entity SET: nodes spawned during the session are removed on exit;
///   * the script spawns that already existed at entry (<see cref="ParkScriptSpawns"/>): taken out
///     of the tree and held alive, so leaving a session restores them instead of re-running every
///     script to re-create them — which is what makes "stop" a revert rather than another run;
///   * the lot-level tables: constraint records (§3.6) and the collision-group matrix (§3.5).
///
/// Not restored, deliberately: an authored entity a session script *destroyed* (its shape and
/// asset references went with it — Test mode restores state, it does not resurrect), the camera,
/// the selection, and the environment (a view setting, not lot state).
///
/// Texture ids are held in <see cref="LotTextureCache"/> for the snapshot's lifetime, so a session
/// that swapped a texture cannot evict the entry the restore re-applies; the holds are released
/// once the restore has run.
/// </summary>
public sealed class LotSessionSnapshot
{
	private sealed class Entry
	{
		public Node Node;
		public Transform3D Transform;      // Node3D entities (parts); UI elements do not use it
		/// <summary>Authored extent of a sized part (cube/sphere/cylinder/wedge).</summary>
		public Vector3 Size;
		public bool HasSize;
		public Vector2 UiPosition;
		public Vector2 UiSize;
		public string UiLabel;
		public Color Color;
		public bool Visible;
		public List<LotPropertyDescriptor> Descriptors;
		public List<Variant> PropertyValues;
	}

	/// <summary>An authored load-time script spawn, detached for the session's duration (see
	/// <see cref="ParkScriptSpawns"/>).</summary>
	private sealed class ParkedSpawn
	{
		public Node Node;
		public Node Parent;
		public int Index;
	}

	// Reused while capturing, so one snapshot pass allocates only what it stores.
	private static readonly List<LotPropertyDescriptor> _descriptorScratch = new List<LotPropertyDescriptor>();

	private readonly List<Entry> _entries = new List<Entry>();
	private readonly HashSet<ulong> _keepIds = new HashSet<ulong>();
	private readonly List<ParkedSpawn> _parked = new List<ParkedSpawn>();
	private readonly List<string> _heldTextures = new List<string>();
	private readonly List<LotConstraintRecord> _constraints = new List<LotConstraintRecord>();
	private Godot.Collections.Array _collision;

	/// <summary>Records the authored world. Call before the session's script reload, so the
	/// snapshot is exactly the world the creator is looking at.</summary>
	public static LotSessionSnapshot Capture(BuilderScene scene)
	{
		LotSessionSnapshot snapshot = new LotSessionSnapshot();
		snapshot._collision = LotCollisionGroups.ToJson();

		IReadOnlyList<LotConstraintRecord> records = LotConstraints.All;
		for (int i = 0; i < records.Count; i++) snapshot._constraints.Add(CopyRecord(records[i]));

		snapshot.Collect(scene.LotRoot);
		snapshot.Collect(scene.LotUIRoot);
		return snapshot;
	}

	/// <summary>
	/// Puts the authored world back: session spawns are removed, every surviving entity's state is
	/// re-applied, and the lot-level tables are replaced with their snapshot. Runs AFTER
	/// <see cref="ReattachScriptSpawns"/> and does NOT run the lot's scripts — leaving a session is
	/// a revert, not another run.
	/// </summary>
	public void Restore(BuilderScene scene)
	{
		RemoveSessionSpawns(scene, scene.LotRoot);
		RemoveSessionSpawns(scene, scene.LotUIRoot);

		for (int i = 0; i < _entries.Count; i++)
		{
			Entry entry = _entries[i];
			Node node = entry.Node;
			// A node a session script destroyed cannot come back (see the class doc); skip it.
			if (node == null || !GodotObject.IsInstanceValid(node)) continue;

			Node3D node3d = node as Node3D;
			if (node3d != null) node3d.Transform = entry.Transform;

			LotUIElement ui = node as LotUIElement;
			if (ui != null)
			{
				ui.Position = entry.UiPosition;
				ui.Size = entry.UiSize;
				ui.Label = entry.UiLabel;
				ui.Color = entry.Color;
				ui.Visible = entry.Visible;
				// Milestone 5.3: the declared rows (alignment, checkbox state, dropdown
				// options/selection) go back too, in declaration order, so a session that toggled a
				// checkbox cannot leave the authored state changed.
				ApplyProperties(ui, entry);
				continue;
			}

			LotObject part = node as LotObject;
			if (part != null)
			{
				// Size first: the property pass below includes the texture mapping (tiling,
				// stretch), and both write through the same mesh-regenerating path.
				if (entry.HasSize) part.SetSize(entry.Size);
				part.Color = entry.Color;
				part.Visible = entry.Visible;
				ApplyProperties(part, entry);
			}
		}

		LotConstraints.RestoreRecords(_constraints, handle => scene.GetByHandle(handle) != null);
		LotCollisionGroups.FromJson(_collision);
		scene.RefreshCollision();

		// The cache holds were only to keep ids resolvable through the restore.
		for (int i = 0; i < _heldTextures.Count; i++) LotTextureCache.Release(_heldTextures[i]);
		_heldTextures.Clear();
		_entries.Clear();
		_keepIds.Clear();
		_constraints.Clear();
	}

	/// <summary>
	/// Takes the lot's load-time script spawns — nodes a script created at load, tagged
	/// <see cref="LotLuaApi.LoadSpawnOwnerMeta"/> — out of the tree WITHOUT freeing them, before the
	/// session's own script reload runs. The reload's cleanup pass cannot see them (it walks the
	/// tree), so instead of freeing and re-creating them it leaves them to
	/// <see cref="ReattachScriptSpawns"/>, and the session spawns its own copies to play with.
	///
	/// This is what lets leaving a session be a REVERT: without it the authored spawns (the capsule
	/// character among them) would be gone by the time the session ended, and the only way back
	/// would be to run every script again — which is exactly the "the Lua re-ran instead of my
	/// session being undone" behaviour. The tag stays on the node, so ownership is unchanged: the
	/// NEXT session's reload still cleans up and replaces them.
	/// </summary>
	public void ParkScriptSpawns(Node lotRoot, Node uiRoot)
	{
		ParkIn(lotRoot);
		ParkIn(uiRoot);
	}

	private void ParkIn(Node parent)
	{
		for (int i = parent.GetChildCount() - 1; i >= 0; i--)
		{
			Node child = parent.GetChild(i);
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			if (child.HasMeta(LotLuaApi.LoadSpawnOwnerMeta))
			{
				// Collected back-to-front, so the reattach loop (which runs the list backwards) puts
				// siblings back in ascending index order — the order that lands every one of them in
				// its authored slot.
				_parked.Add(new ParkedSpawn { Node = child, Parent = parent, Index = i });
				parent.RemoveChild(child);
				continue; // the whole spawned subtree travels with it
			}
			ParkIn(child);
		}
	}

	/// <summary>
	/// Puts the parked spawns back where they were, before <see cref="Restore"/> re-applies their
	/// authored state. A spawn whose parent the session destroyed cannot come back — the same rule
	/// the restore follows for authored entities — so it goes through the caller's destroy path
	/// rather than leaking as a detached node.
	/// </summary>
	public void ReattachScriptSpawns(Action<Node> destroyEntity)
	{
		// Backwards over a list collected backwards = ascending index order within each parent.
		for (int i = _parked.Count - 1; i >= 0; i--)
		{
			ParkedSpawn parked = _parked[i];
			Node node = parked.Node;
			if (node == null || !GodotObject.IsInstanceValid(node)) continue;
			if (node.IsQueuedForDeletion())
			{
				// Something in the session destroyed the spawn itself; the destroy path already owns
				// it, so it must not be re-attached into a tree it is leaving.
				continue;
			}

			Node parent = parked.Parent;
			// A parent that is gone OR already queued for deletion cannot host it: the spawn would be
			// freed as part of a dying subtree the destroy walk has already finished cleaning up,
			// which would leave its handle behind. It goes through the destroy path instead.
			if (parent == null || !GodotObject.IsInstanceValid(parent) || parent.IsQueuedForDeletion())
			{
				if (destroyEntity != null) destroyEntity(node);
				else node.QueueFree();
				continue;
			}

			parent.AddChild(node);
			parent.MoveChild(node, Mathf.Clamp(parked.Index, 0, parent.GetChildCount() - 1));
		}
		_parked.Clear();
	}

	// --- capture -----------------------------------------------------------------------------------

	private void Collect(Node node)
	{
		int count = node.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = node.GetChild(i);
			// Internal children (outline, collision body, joints, the hinge-point marker) are
			// implementation details; script placeholders are recreated by the reload.
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			if (child is LotScriptNode) continue;
			_keepIds.Add(child.GetInstanceId());
			_entries.Add(CaptureEntry(child));
			Collect(child);
		}
	}

	private Entry CaptureEntry(Node node)
	{
		Entry entry = new Entry { Node = node };

		Node3D node3d = node as Node3D;
		if (node3d != null) entry.Transform = node3d.Transform;

		// The authored size is separate state from the node transform (the migration moved resizing
		// off node scale), so it is captured beside it — the declared properties below only cover
		// the texture mapping, not the extent itself.
		LotObject sizedPart = node as LotObject;
		if (sizedPart != null && sizedPart.HasSize)
		{
			entry.HasSize = true;
			entry.Size = sizedPart.Size;
		}

		LotUIElement ui = node as LotUIElement;
		if (ui != null)
		{
			entry.UiPosition = ui.Position;
			entry.UiSize = ui.Size;
			entry.UiLabel = ui.Label;
			entry.Color = ui.Color;
			entry.Visible = ui.Visible;
			// Milestone 5.3: the declared customization rows (label, alignment, checkbox state,
			// dropdown options/selection) ride the same per-entity capture a part's do, so a
			// checkbox a session toggled comes back authored.
			CaptureDeclaredProperties(node, entry);
			return entry;
		}

		LotObject part = node as LotObject;
		if (part != null)
		{
			entry.Color = part.Color;
			entry.Visible = part.Visible;
			CaptureDeclaredProperties(node, entry);

			// Hold the part's texture so a mid-session swap cannot evict the id the restore needs.
			if (part.TextureId.Length > 0 && LotTextureCache.Contains(part.TextureId))
			{
				LotTextureCache.Acquire(part.TextureId);
				_heldTextures.Add(part.TextureId);
			}
		}
		return entry;
	}

	/// <summary>Captures every declared property that applies to <paramref name="node"/>, in
	/// declaration order — which the restore relies on (a dropdown's options precede its selection).
	/// </summary>
	private static void CaptureDeclaredProperties(Node node, Entry entry)
	{
		entry.Descriptors = new List<LotPropertyDescriptor>();
		entry.PropertyValues = new List<Variant>();
		LotPropertyRegistry.CollectFor(node, _descriptorScratch);
		for (int i = 0; i < _descriptorScratch.Count; i++)
		{
			LotPropertyDescriptor descriptor = _descriptorScratch[i];
			if (descriptor.Kind == LotPropertyKind.Bool && descriptor.GetBool != null)
			{
				entry.Descriptors.Add(descriptor);
				entry.PropertyValues.Add(descriptor.GetBool(node));
			}
			else if (descriptor.Kind == LotPropertyKind.Choice && descriptor.GetChoice != null)
			{
				entry.Descriptors.Add(descriptor);
				entry.PropertyValues.Add(descriptor.GetChoice(node) ?? "");
			}
			else if (descriptor.Kind == LotPropertyKind.Float && descriptor.GetFloat != null)
			{
				entry.Descriptors.Add(descriptor);
				entry.PropertyValues.Add(descriptor.GetFloat(node));
			}
			else if (descriptor.Kind == LotPropertyKind.Text && descriptor.GetText != null)
			{
				entry.Descriptors.Add(descriptor);
				entry.PropertyValues.Add(descriptor.GetText(node) ?? "");
			}
		}
	}

	// --- restore -----------------------------------------------------------------------------------

	/// <summary>
	/// Removes nodes the session created: anything not in the keep set goes, through the caller's
	/// single destroy path — which is why the session's own load-time spawns do not need an
	/// exemption here: the authored ones were parked out of the tree at entry and are back in the
	/// keep set by the time this runs. A destroyed parent takes its subtree with it, so the walk does
	/// not recurse into one. Script placeholders survive (the reload recreates them, and the walk's
	/// own set is what the snapshot compares against).
	/// </summary>
	private void RemoveSessionSpawns(BuilderScene scene, Node container)
	{
		for (int i = container.GetChildCount() - 1; i >= 0; i--)
		{
			Node child = container.GetChild(i);
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			if (child is LotScriptNode) continue;

			bool kept = _keepIds.Contains(child.GetInstanceId());
			if (kept)
			{
				RemoveSessionSpawns(scene, child);
				continue;
			}
			scene.DestroyEntity(child);
		}
	}

	/// <summary>
	/// Re-applies a node's declared properties through the descriptor setters directly — the same
	/// raw path <see cref="LotSceneLoad"/> uses when it rebuilds a part from an archive. This is a
	/// bulk state application, not an edit, so it deliberately does not go through the history.
	/// Serves parts and lot UI elements alike (milestone 5.3).
	/// </summary>
	private static void ApplyProperties(Node node, Entry entry)
	{
		if (entry.Descriptors == null) return;
		for (int i = 0; i < entry.Descriptors.Count; i++)
		{
			LotPropertyDescriptor descriptor = entry.Descriptors[i];
			Variant value = entry.PropertyValues[i];
			if (descriptor.Kind == LotPropertyKind.Bool && descriptor.SetBool != null
				&& value.VariantType == Variant.Type.Bool)
			{
				descriptor.SetBool(node, value.AsBool());
			}
			else if (descriptor.Kind == LotPropertyKind.Choice && descriptor.SetChoice != null
				&& value.VariantType == Variant.Type.String)
			{
				descriptor.SetChoice(node, value.AsString());
			}
			else if (descriptor.Kind == LotPropertyKind.Float && descriptor.SetFloat != null
				&& value.VariantType == Variant.Type.Float)
			{
				descriptor.SetFloat(node, (float)value.AsDouble());
			}
			else if (descriptor.Kind == LotPropertyKind.Text && descriptor.SetText != null
				&& value.VariantType == Variant.Type.String)
			{
				descriptor.SetText(node, value.AsString() ?? "");
			}
		}
	}

	/// <summary>A field copy of a constraint record, so the snapshot owns data the session cannot mutate.</summary>
	private static LotConstraintRecord CopyRecord(LotConstraintRecord source)
	{
		return new LotConstraintRecord
		{
			Id = source.Id,
			Kind = source.Kind,
			A = source.A,
			B = source.B,
			Enabled = source.Enabled,
			Pivot = source.Pivot,
			Axis = source.Axis,
			LimitsEnabled = source.LimitsEnabled,
			LowerDeg = source.LowerDeg,
			UpperDeg = source.UpperDeg,
			Motor = source.Motor,
			MotorVelocity = source.MotorVelocity,
			MotorMaxPush = source.MotorMaxPush
		};
	}
}
