using System;
using System.Collections.Generic;
using Godot;

/// <summary>Value kind of a declared property. Drives which Inspector widget is drawn.</summary>
public enum LotPropertyKind
{
	Bool,
	Choice,
	/// <summary>A plain number (milestone 3.6's extension for the hinge editor; mass/friction in
	/// §3.11 reuse it).</summary>
	Float,
	/// <summary>A free-text string (milestone 2.6's on-face UI: a panel's Text/Button label; §5.3's
	/// lot-UI labels reuse it).</summary>
	Text
}

/// <summary>
/// One declared property: what it is called, which nodes it applies to, and how to read/write it.
/// A property is added by declaring a descriptor — no Inspector code changes (milestone 2.3).
///
/// Value plumbing is a pair of delegates per kind, created once as static lambdas, so declaring a
/// property costs nothing per frame and the Inspector never allocates to read or write one.
/// Only the delegate group matching <see cref="Kind"/> is populated.
/// </summary>
public sealed class LotPropertyDescriptor
{
	/// <summary>Stable key. Persisted in the §8.1 lot format and used by the plugin-facing API.</summary>
	public string Id;
	/// <summary>Label shown in the Inspector.</summary>
	public string DisplayName;
	/// <summary>Which value kind this is.</summary>
	public LotPropertyKind Kind;
	/// <summary>Section the Inspector groups the property under.</summary>
	public string Category;
	/// <summary>Contract text: what the property means and any side effect. Bound to the widget's tooltip.</summary>
	public string Doc;
	/// <summary>True for the nodes this property is offered on.</summary>
	public Func<Node, bool> AppliesTo;

	// Bool properties.
	public Func<Node, bool> GetBool;
	public Action<Node, bool> SetBool;

	// Choice properties. ListOptions supplies the current option list, so a live list (registered
	// image assets, for instance) does not need to be cached and invalidated by hand.
	public Func<string[]> ListOptions;
	public Func<Node, string> GetChoice;
	public Action<Node, string> SetChoice;

	// Float properties.
	public Func<Node, float> GetFloat;
	public Action<Node, float> SetFloat;
	/// <summary>Drag step per pixel for the Float widget (the widget's own default when 0).</summary>
	public float Speed;

	// Text properties.
	public Func<Node, string> GetText;
	public Action<Node, string> SetText;

	/// <summary>
	/// True for a Choice whose combo carries the texture affordances: a leading "(none)" row that
	/// clears the value and a trailing "Import..." row that opens the image browser. False (the
	/// default) draws a plain list of the property's own options — what every other Choice needs, so
	/// the Inspector no longer assumes those two rows belong to every dropdown.
	/// </summary>
	public bool HasImportAction;
}

/// <summary>
/// The declaration table for lot properties. Every property the Inspector shows and every property
/// the bound Lua API addresses is declared here, so the two cannot disagree about a name or a type.
///
/// <see cref="Register"/> is the extension point for creation-environment plugins (Tier 2 per
/// clinerules 1.4): a plugin adds a property by declaring a descriptor, and it appears in the
/// Inspector with no core change. Tier 1 (terminal/sandboxed) never reaches this table.
/// </summary>
public static class LotPropertyRegistry
{
	private static readonly List<LotPropertyDescriptor> _all = new List<LotPropertyDescriptor>();

	static LotPropertyRegistry()
	{
		RegisterCore();
	}

	/// <summary>Every declared property, in declaration order.</summary>
	public static IReadOnlyList<LotPropertyDescriptor> All { get { return _all; } }

	/// <summary>Declares a property. Registering the same id twice is ignored (first wins).</summary>
	public static void Register(LotPropertyDescriptor descriptor)
	{
		if (descriptor == null || string.IsNullOrEmpty(descriptor.Id)) return;
		if (Find(descriptor.Id) != null) return;
		_all.Add(descriptor);
	}

	/// <summary>
	/// Removes a declared property by id, <see cref="Register"/>'s counterpart. Used to undo a
	/// scratch declaration (a self-test registering a property to exercise the Float plumbing) so a
	/// run cannot leave a test property visible in the editor and present in saved lots.
	/// </summary>
	public static bool Unregister(string id)
	{
		if (string.IsNullOrEmpty(id)) return false;
		for (int i = 0; i < _all.Count; i++)
		{
			if (_all[i].Id != id) continue;
			_all.RemoveAt(i);
			return true;
		}
		return false;
	}

	public static LotPropertyDescriptor Find(string id)
	{
		if (string.IsNullOrEmpty(id)) return null;
		for (int i = 0; i < _all.Count; i++)
		{
			if (_all[i].Id == id) return _all[i];
		}
		return null;
	}

	/// <summary>
	/// Appends the properties that apply to <paramref name="node"/> into a caller-owned list. The
	/// list is passed in rather than returned so the Inspector can reuse one buffer and allocate
	/// nothing per frame.
	/// </summary>
	public static void CollectFor(Node node, List<LotPropertyDescriptor> into)
	{
		if (into == null) return;
		into.Clear();
		if (node == null) return;

		for (int i = 0; i < _all.Count; i++)
		{
			LotPropertyDescriptor descriptor = _all[i];
			if (descriptor.AppliesTo == null || descriptor.AppliesTo(node)) into.Add(descriptor);
		}
	}

	private static bool IsPart(Node node)
	{
		// A decal is a LotObject too, but the part properties (anchored, collision group, the
		// whole-part texture) are meaningless for a decoration patch: it has its own set below.
		return node is LotObject part && part.Kind != LotObjectKind.Decal;
	}

	private static bool IsDecal(Node node)
	{
		return node is LotObject part && part.Kind == LotObjectKind.Decal;
	}

	/// <summary>
	/// The core property set. Semantics live on the owning type (LotObject.Anchored /
	/// LotObject.CanCollide / LotObject.SetTexture), not duplicated here; Doc is the creator-facing
	/// one-liner.
	///
	/// Reserved ids for later milestones, deliberately NOT declared yet so nothing speculative
	/// appears in the Inspector: spawn / team / checkpoint (§14.8).
	/// </summary>
	private static void RegisterCore()
	{
		Register(new LotPropertyDescriptor
		{
			Id = "anchored",
			DisplayName = "Anchored",
			Kind = LotPropertyKind.Bool,
			Category = "Part",
			Doc = "Anchored parts stay in place; unanchored parts are affected by gravity in a " +
				"player session. Position writes still work either way.",
			AppliesTo = IsPart,
			GetBool = node => ((LotObject)node).Anchored,
			SetBool = (node, value) => ((LotObject)node).SetAnchored(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "canCollide",
			DisplayName = "CanCollide",
			Kind = LotPropertyKind.Bool,
			Category = "Part",
			Doc = "When off, solid things pass through the part. The part stays selectable and " +
				"draggable in the editor.",
			AppliesTo = IsPart,
			GetBool = node => ((LotObject)node).CanCollide,
			SetBool = (node, value) => ((LotObject)node).SetCanCollide(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "texture",
			DisplayName = "Texture",
			Kind = LotPropertyKind.Choice,
			Category = "Part",
			Doc = "Image applied to the part, chosen from the registered assets. 'Import...' loads " +
				"a new file; '(none)' clears it.",
			AppliesTo = IsPart,
			HasImportAction = true,
			ListOptions = () => LotTextureCache.IdsAsArray,
			GetChoice = node => ((LotObject)node).TextureId,
			SetChoice = (node, value) => ((LotObject)node).SetTexture(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "collisionGroup",
			DisplayName = "Collision Group",
			Kind = LotPropertyKind.Choice,
			Category = "Part",
			Doc = "Which named collision group the part belongs to (milestone 3.5). The View -> " +
				"Collision Groups matrix decides which groups interact.",
			AppliesTo = IsPart,
			ListOptions = () => LotCollisionGroups.Names,
			GetChoice = node => ((LotObject)node).CollisionGroup,
			SetChoice = (node, value) => ((LotObject)node).SetCollisionGroup(value)
		});

		// --- Physics material (milestone 3.11) ---
		// Three numbers the bodies §3.6 swapped in already respect; declared here so the Inspector
		// and the Lua verbs share one vocabulary, and so lot.json / the session snapshot carry
		// them for free. The defaults reproduce the pre-3.11 behaviour exactly: Godot's own
		// friction 1 and bounce 0, and mass 0 = "derive it from the mesh's volume" (the §3.6 rule).

		Register(new LotPropertyDescriptor
		{
			Id = "friction",
			DisplayName = "Friction",
			Kind = LotPropertyKind.Float,
			Category = "Physics",
			Doc = "How grippy the part's surface is (0 = slippery, 1 = Godot's default). Applies " +
				"to the part's collision body, in the editor and in play alike.",
			AppliesTo = IsPart,
			Speed = 0.01f,
			GetFloat = node => ((LotObject)node).Friction,
			SetFloat = (node, value) => ((LotObject)node).SetFriction(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "bounce",
			DisplayName = "Bounce",
			Kind = LotPropertyKind.Float,
			Category = "Physics",
			Doc = "How bouncy the part is (0 = dead, 1 = fully elastic). Applies to the part's " +
				"collision body, in the editor and in play alike.",
			AppliesTo = IsPart,
			Speed = 0.01f,
			GetFloat = node => ((LotObject)node).Bounce,
			SetFloat = (node, value) => ((LotObject)node).SetBounce(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "mass",
			DisplayName = "Mass",
			Kind = LotPropertyKind.Float,
			Category = "Physics",
			Doc = "The part's weight in play. 0 = auto: derived from the part's size, which is " +
				"what every part did before this property existed. Impulses divide by it.",
			AppliesTo = IsPart,
			Speed = 0.05f,
			GetFloat = node => ((LotObject)node).Mass,
			SetFloat = (node, value) => ((LotObject)node).SetMass(value)
		});

		// --- Decal properties (milestone 2.6) ---
		// The image patch's declaration table. Declared here like any other property, so the
		// Inspector widgets, undo, the session snapshot and lot serialization all come for free.

		Register(new LotPropertyDescriptor
		{
			Id = "decal_face",
			DisplayName = "Face",
			Kind = LotPropertyKind.Choice,
			Category = "Decal",
			Doc = "Which face of the host part the image sits on. +Z is the front face of an " +
				"unrotated part.",
			AppliesTo = IsDecal,
			ListOptions = () => DecalMath.FaceNames,
			GetChoice = node => DecalMath.FaceNames[((LotObject)node).DecalFace],
			SetChoice = (node, value) => ((LotObject)node).SetDecalFace(DecalMath.FaceIndex(value))
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_texture",
			DisplayName = "Image",
			Kind = LotPropertyKind.Choice,
			Category = "Decal",
			Doc = "Image drawn on the chosen face; the image's transparent areas stay " +
				"transparent. 'Import...' loads a new file; '(none)' leaves a plain white square.",
			AppliesTo = IsDecal,
			HasImportAction = true,
			ListOptions = () => LotTextureCache.IdsAsArray,
			GetChoice = node => ((LotObject)node).TextureId,
			SetChoice = (node, value) => ((LotObject)node).SetTexture(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_offset_u",
			DisplayName = "Offset U",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "Slides the image across the face horizontally, as a fraction of the face " +
				"width (-0.5 .. 0.5). Clamped so the image stays on the face.",
			AppliesTo = IsDecal,
			Speed = 0.005f,
			GetFloat = node => ((LotObject)node).DecalOffsetU,
			SetFloat = (node, value) => ((LotObject)node).SetDecalOffsetU(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_offset_v",
			DisplayName = "Offset V",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "Slides the image across the face vertically, as a fraction of the face " +
				"height (-0.5 .. 0.5). Clamped so the image stays on the face.",
			AppliesTo = IsDecal,
			Speed = 0.005f,
			GetFloat = node => ((LotObject)node).DecalOffsetV,
			SetFloat = (node, value) => ((LotObject)node).SetDecalOffsetV(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_scale_u",
			DisplayName = "Scale U",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "Image width as a fraction of the face width (0.05 .. 1).",
			AppliesTo = IsDecal,
			Speed = 0.005f,
			GetFloat = node => ((LotObject)node).DecalScaleU,
			SetFloat = (node, value) => ((LotObject)node).SetDecalScaleU(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_scale_v",
			DisplayName = "Scale V",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "Image height as a fraction of the face height (0.05 .. 1).",
			AppliesTo = IsDecal,
			Speed = 0.005f,
			GetFloat = node => ((LotObject)node).DecalScaleV,
			SetFloat = (node, value) => ((LotObject)node).SetDecalScaleV(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_opacity",
			DisplayName = "Opacity",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "How solid the image is (0 invisible .. 1 opaque). Transparent pixels of the " +
				"image stay transparent at any opacity.",
			AppliesTo = IsDecal,
			Speed = 0.01f,
			GetFloat = node => ((LotObject)node).DecalOpacity,
			SetFloat = (node, value) => ((LotObject)node).SetDecalOpacity(value)
		});

		// --- Decal on-face UI content (milestone 2.6, on-face UI step) ---

		Register(new LotPropertyDescriptor
		{
			Id = "decal_content",
			DisplayName = "Content",
			Kind = LotPropertyKind.Choice,
			Category = "Decal",
			Doc = "What the panel draws: an image patch, text, a button surface, or a scrollbar. " +
				"Button and Scrollbar are drawn and configurable; their click handling waits on " +
				"the event system (§3.7).",
			AppliesTo = IsDecal,
			ListOptions = () => DecalMath.ContentNames,
			GetChoice = node => DecalMath.ContentNames[(int)((LotObject)node).Content],
			SetChoice = (node, value) => ((LotObject)node).SetDecalContent(DecalMath.ContentIndex(value))
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_text",
			DisplayName = "Text",
			Kind = LotPropertyKind.Text,
			Category = "Decal",
			Doc = "The text a Text or Button panel shows. Wraps at the panel's edge; the font " +
				"size sets how much of the panel one line takes.",
			AppliesTo = IsDecal,
			GetText = node => ((LotObject)node).DecalText,
			SetText = (node, value) => ((LotObject)node).SetDecalText(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_scroll",
			DisplayName = "Scroll",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "Scrollbar thumb position, 0 (top) .. 1 (bottom). Scripts move the thumb with " +
				"this; the bar itself takes no input until the event system lands (§3.7).",
			AppliesTo = IsDecal,
			Speed = 0.005f,
			GetFloat = node => ((LotObject)node).DecalScroll,
			SetFloat = (node, value) => ((LotObject)node).SetDecalScroll(value)
		});

		Register(new LotPropertyDescriptor
		{
			Id = "decal_font_size",
			DisplayName = "Font Size",
			Kind = LotPropertyKind.Float,
			Category = "Decal",
			Doc = "Text size on the panel (8 .. 160). One line takes FontSize/160 of the panel's " +
				"height, so the text scales with the panel.",
			AppliesTo = IsDecal,
			Speed = 1f,
			GetFloat = node => ((LotObject)node).DecalFontSize,
			SetFloat = (node, value) => ((LotObject)node).SetDecalFontSize(value)
		});
	}
}

/// <summary>
/// The single write path for lot properties. Every writer goes through here — the Inspector today,
/// and later the bound Lua API and the undo/redo stack (§2.4) and lot serialization (§8.1) — so
/// there is exactly one place where "a property changed" is turned into scene state plus a dirty
/// mark. Nothing else may call a property's setter directly.
///
/// Reads are pure pass-throughs to the descriptor's accessor, so a reader can never accidentally
/// mutate the scene.
/// </summary>
public sealed class LotPropertyService
{
	/// <summary>Id of the built-in Texture descriptor; its choice picks need cache references held.</summary>
	private const string TexturePropertyId = "texture";

	private readonly Builder _builder;

	public LotPropertyService(Builder builder)
	{
		_builder = builder;
	}

	/// <summary>Reads a Bool property. False for a null node or a mismatched descriptor.</summary>
	public bool ReadBool(Node node, LotPropertyDescriptor descriptor)
	{
		if (node == null || descriptor == null || descriptor.GetBool == null) return false;
		return descriptor.GetBool(node);
	}

	/// <summary>
	/// Records a Bool property change as one history entry (milestone 2.4). A no-op when the value
	/// did not change, so a checkbox that fires without a change cannot pollute the stack.
	/// </summary>
	public void WriteBool(Node node, LotPropertyDescriptor descriptor, bool value)
	{
		if (node == null || descriptor == null || descriptor.SetBool == null) return;
		bool before = descriptor.GetBool(node);
		if (before == value) return;
		_builder.History.Push(new DelegateCommand(descriptor.DisplayName,
			b => b.Properties.ApplyBool(node, descriptor, value),
			b => b.Properties.ApplyBool(node, descriptor, before)));
	}

	/// <summary>
	/// Records a Choice property change. For the Texture property both the old and the new image are
	/// held in <see cref="LotTextureCache"/> for as long as the entry lives, so undo and redo can
	/// both restore their side; the extra references are handed back when the entry is discarded.
	/// </summary>
	public void WriteChoice(Node node, LotPropertyDescriptor descriptor, string value)
	{
		if (node == null || descriptor == null || descriptor.SetChoice == null) return;
		string before = descriptor.GetChoice(node);
		if (before == value) return;

		bool isTexture = descriptor.Id == TexturePropertyId;
		string keepNew = isTexture ? (value ?? "") : "";
		string keepOld = isTexture ? (before ?? "") : "";
		if (keepNew.Length > 0) LotTextureCache.Acquire(keepNew);
		if (keepOld.Length > 0) LotTextureCache.Acquire(keepOld);

		_builder.History.Push(new DelegateCommand(descriptor.DisplayName,
			b => b.Properties.ApplyChoice(node, descriptor, value),
			b => b.Properties.ApplyChoice(node, descriptor, before),
			b =>
			{
				if (keepNew.Length > 0) LotTextureCache.Release(keepNew);
				if (keepOld.Length > 0) LotTextureCache.Release(keepOld);
			}));
	}

	/// <summary>Reads a Float property. 0 for a null node or a mismatched descriptor.</summary>
	public float ReadFloat(Node node, LotPropertyDescriptor descriptor)
	{
		if (node == null || descriptor == null || descriptor.GetFloat == null) return 0f;
		return descriptor.GetFloat(node);
	}

	/// <summary>
	/// Raw Float apply — the in-drag write. A Float widget is a drag/typing session, so the value is
	/// applied live here and the whole session becomes ONE history entry via
	/// <see cref="PushFloatEdit"/> when the widget deactivates (the same begin/commit split the
	/// transform fields use).
	/// </summary>
	public void ApplyFloat(Node node, LotPropertyDescriptor descriptor, float value)
	{
		if (node == null || descriptor == null || descriptor.SetFloat == null) return;
		descriptor.SetFloat(node, value);
	}

	/// <summary>Records one Float edit session (its start and end values) as a single history entry.</summary>
	public void PushFloatEdit(Node node, LotPropertyDescriptor descriptor, float before, float after)
	{
		if (node == null || descriptor == null || descriptor.SetFloat == null) return;
		if (before == after) return;
		_builder.History.Push(new DelegateCommand(descriptor.DisplayName,
			b => b.Properties.ApplyFloat(node, descriptor, after),
			b => b.Properties.ApplyFloat(node, descriptor, before)));
	}

	/// <summary>Reads a Text property. "" for a null node or a mismatched descriptor.</summary>
	public string ReadText(Node node, LotPropertyDescriptor descriptor)
	{
		if (node == null || descriptor == null || descriptor.GetText == null) return "";
		return descriptor.GetText(node) ?? "";
	}

	/// <summary>Raw Text apply — the in-edit write. Like a Float field, the text applies live while
	/// the field is being typed in and the whole session becomes ONE history entry via
	/// <see cref="PushTextEdit"/> when the field deactivates.</summary>
	public void ApplyText(Node node, LotPropertyDescriptor descriptor, string value)
	{
		if (node == null || descriptor == null || descriptor.SetText == null) return;
		descriptor.SetText(node, value);
	}

	/// <summary>Records one Text edit session as a single history entry.</summary>
	public void PushTextEdit(Node node, LotPropertyDescriptor descriptor, string before, string after)
	{
		if (node == null || descriptor == null || descriptor.SetText == null) return;
		if (before == after) return;
		_builder.History.Push(new DelegateCommand(descriptor.DisplayName,
			b => b.Properties.ApplyText(node, descriptor, after),
			b => b.Properties.ApplyText(node, descriptor, before)));
	}

	/// <summary>Raw apply used by the history (never called directly by UI code).</summary>
	public void ApplyBool(Node node, LotPropertyDescriptor descriptor, bool value)
	{
		if (node == null || descriptor == null || descriptor.SetBool == null) return;
		descriptor.SetBool(node, value);
	}

	/// <summary>Raw apply used by the history (never called directly by UI code).</summary>
	public void ApplyChoice(Node node, LotPropertyDescriptor descriptor, string value)
	{
		if (node == null || descriptor == null || descriptor.SetChoice == null) return;
		descriptor.SetChoice(node, value);
	}

	/// <summary>Raw texture apply used by the history (never called directly by UI code).</summary>
	public void ApplyTexture(LotObject part, string assetId)
	{
		if (part == null || !GodotObject.IsInstanceValid(part)) return;
		part.SetTexture(assetId);
	}

	/// <summary>Reads a Choice property. "" for a null node or a mismatched descriptor.</summary>
	public string ReadChoice(Node node, LotPropertyDescriptor descriptor)
	{
		if (node == null || descriptor == null || descriptor.GetChoice == null) return "";
		return descriptor.GetChoice(node) ?? "";
	}

	/// <summary>
	/// Imports an image file and assigns it to a part's Texture property in one step, releasing the
	/// cache's import reference once the part holds its own. Keeping the acquire/release pairing in
	/// this one place is what stops callers from leaking an image or freeing one still in use.
	/// Returns false (with a message) when the file cannot be read.
	/// </summary>
	public bool SetTextureFromFile(LotObject part, string path, out string error)
	{
		if (part == null || !GodotObject.IsInstanceValid(part))
		{
			error = "no part is selected";
			return false;
		}

		string assetId = LotTextureCache.ImportFromFile(path, out error);
		if (assetId.Length == 0) return false;

		string before = part.TextureId;
		if (before == assetId)
		{
			LotTextureCache.Release(assetId);
			error = "";
			return true;
		}

		// The import reference is held for the entry's lifetime so redo can re-apply the image even
		// after the part dropped its own reference; it is handed back when the entry is discarded.
		string keepOld = before ?? "";
		if (keepOld.Length > 0) LotTextureCache.Acquire(keepOld);

		_builder.History.Push(new DelegateCommand("Set Texture",
			b => b.Properties.ApplyTexture(part, assetId),
			b => b.Properties.ApplyTexture(part, before),
			b =>
			{
				LotTextureCache.Release(assetId);
				if (keepOld.Length > 0) LotTextureCache.Release(keepOld);
			}));

		error = "";
		return true;
	}
}
