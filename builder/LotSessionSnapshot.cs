using System.Collections.Generic;
using Godot;

/// <summary>
/// Test-mode isolation (§3.4 + §3.6): a snapshot of the authored lot taken when a player session
/// starts and restored when it ends, so playing can never change the build-mode world.
///
/// The session runs on the live tree (that is the §3.4 design — reload on entry, reload on exit),
/// so "load the lot as an instance" is implemented as: remember exactly what the authored world
/// looked like, and put it back on exit. What is remembered:
///   * every entity that existed at entry — 3D parts: local transform, color, visibility and every
///     declared §2.3 property (anchored, collision group, texture, ...); UI elements: rect, label,
///     color, visibility;
///   * the entity SET: nodes spawned during the session are removed on exit (script-owned spawns
///     are exempt — the exit reload re-creates them from their scripts, which is the §3.4 rule);
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
		public Vector2 UiPosition;
		public Vector2 UiSize;
		public string UiLabel;
		public Color Color;
		public bool Visible;
		public List<LotPropertyDescriptor> Descriptors;
		public List<Variant> PropertyValues;
	}

	// Reused while capturing, so one snapshot pass allocates only what it stores.
	private static readonly List<LotPropertyDescriptor> _descriptorScratch = new List<LotPropertyDescriptor>();

	private readonly List<Entry> _entries = new List<Entry>();
	private readonly HashSet<ulong> _keepIds = new HashSet<ulong>();
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
	/// re-applied, and the lot-level tables are replaced with their snapshot. Runs AFTER the exit
	/// reload, so the script-owned content the reload recreated is already in place.
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
				continue;
			}

			LotObject part = node as LotObject;
			if (part != null)
			{
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

		LotUIElement ui = node as LotUIElement;
		if (ui != null)
		{
			entry.UiPosition = ui.Position;
			entry.UiSize = ui.Size;
			entry.UiLabel = ui.Label;
			entry.Color = ui.Color;
			entry.Visible = ui.Visible;
			return entry;
		}

		LotObject part = node as LotObject;
		if (part != null)
		{
			entry.Color = part.Color;
			entry.Visible = part.Visible;
			entry.Descriptors = new List<LotPropertyDescriptor>();
			entry.PropertyValues = new List<Variant>();
			LotPropertyRegistry.CollectFor(part, _descriptorScratch);
			for (int i = 0; i < _descriptorScratch.Count; i++)
			{
				LotPropertyDescriptor descriptor = _descriptorScratch[i];
				if (descriptor.Kind == LotPropertyKind.Bool && descriptor.GetBool != null)
				{
					entry.Descriptors.Add(descriptor);
					entry.PropertyValues.Add(descriptor.GetBool(part));
				}
				else if (descriptor.Kind == LotPropertyKind.Choice && descriptor.GetChoice != null)
				{
					entry.Descriptors.Add(descriptor);
					entry.PropertyValues.Add(descriptor.GetChoice(part) ?? "");
				}
				else if (descriptor.Kind == LotPropertyKind.Float && descriptor.GetFloat != null)
				{
					entry.Descriptors.Add(descriptor);
					entry.PropertyValues.Add(descriptor.GetFloat(part));
				}
				else if (descriptor.Kind == LotPropertyKind.Text && descriptor.GetText != null)
				{
					entry.Descriptors.Add(descriptor);
					entry.PropertyValues.Add(descriptor.GetText(part) ?? "");
				}
			}

			// Hold the part's texture so a mid-session swap cannot evict the id the restore needs.
			if (part.TextureId.Length > 0 && LotTextureCache.Contains(part.TextureId))
			{
				LotTextureCache.Acquire(part.TextureId);
				_heldTextures.Add(part.TextureId);
			}
		}
		return entry;
	}

	// --- restore -----------------------------------------------------------------------------------

	/// <summary>
	/// Removes nodes the session created: anything not in the keep set and not a script-owned spawn
	/// (those are exempt because the exit reload recreated them from their scripts). A destroyed
	/// parent takes its subtree with it, so the walk does not recurse into one.
	/// </summary>
	private void RemoveSessionSpawns(BuilderScene scene, Node container)
	{
		for (int i = container.GetChildCount() - 1; i >= 0; i--)
		{
			Node child = container.GetChild(i);
			if (child.HasMeta(LotObject.InternalChildMeta)) continue;
			if (child is LotScriptNode) continue;

			bool kept = _keepIds.Contains(child.GetInstanceId())
				|| child.HasMeta(LotLuaApi.LoadSpawnOwnerMeta);
			if (kept)
			{
				RemoveSessionSpawns(scene, child);
				continue;
			}
			scene.DestroyEntity(child);
		}
	}

	/// <summary>
	/// Re-applies a part's declared properties through the descriptor setters directly — the same
	/// raw path <see cref="LotSceneLoad"/> uses when it rebuilds a part from an archive. This is a
	/// bulk state application, not an edit, so it deliberately does not go through the history.
	/// </summary>
	private static void ApplyProperties(LotObject part, Entry entry)
	{
		if (entry.Descriptors == null) return;
		for (int i = 0; i < entry.Descriptors.Count; i++)
		{
			LotPropertyDescriptor descriptor = entry.Descriptors[i];
			Variant value = entry.PropertyValues[i];
			if (descriptor.Kind == LotPropertyKind.Bool && descriptor.SetBool != null
				&& value.VariantType == Variant.Type.Bool)
			{
				descriptor.SetBool(part, value.AsBool());
			}
			else if (descriptor.Kind == LotPropertyKind.Choice && descriptor.SetChoice != null
				&& value.VariantType == Variant.Type.String)
			{
				descriptor.SetChoice(part, value.AsString());
			}
			else if (descriptor.Kind == LotPropertyKind.Float && descriptor.SetFloat != null
				&& value.VariantType == Variant.Type.Float)
			{
				descriptor.SetFloat(part, (float)value.AsDouble());
			}
			else if (descriptor.Kind == LotPropertyKind.Text && descriptor.SetText != null
				&& value.VariantType == Variant.Type.String)
			{
				descriptor.SetText(part, value.AsString() ?? "");
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
