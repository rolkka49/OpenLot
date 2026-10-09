using Godot;

/// <summary>
/// The creation environment's "secret content" gate (milestone 4.1). A lot's base content — the
/// scripts and formats it ships with, stored in the archive's <c>Secret_Code/</c> and
/// <c>Secret_Asset/</c> folders — is hidden in the editor by default, because a creator does not
/// normally edit it and changing or deleting it can break features that depend on it. Revealing it is
/// one deliberate toggle behind a warning.
///
/// Scope: this type owns the *editor visibility* decision and nothing else. What counts as base
/// content is the format's business (<see cref="LotArchiveCollect.IsBaseScript"/>,
/// <see cref="LotArchive.IsSecretPath"/>), so the editor and the archive cannot disagree about which
/// content is base content.
///
/// Visibility only. A hidden node is still in the scene, still runs, and still saves — hiding it is a
/// view decision, never a behaviour change. Session-only, like the environment settings: a restart
/// starts hidden again, which is the safe default.
/// </summary>
public static class SecretContent
{
	/// <summary>False until the creator reveals base content. Hidden is the default.</summary>
	public static bool Revealed { get; private set; }

	/// <summary>Prefixed to a base-content row, so it is identifiable even out of context (a drag
	/// preview, or a row nested below the section heading).</summary>
	public const string RowMarker = "[base] ";

	/// <summary>Heading of the separated section base content is drawn in.</summary>
	public const string SectionHeading = "Secret Content (proprietary)";

	/// <summary>One-line caution under the heading, for a creator who scrolled past the modal.</summary>
	public const string SectionNote = "Universal to OpenLot. Altering or deleting these can break features.";

	/// <summary>Warning colour for the section heading. Amber rather than red: this is a caution, not an
	/// error state.</summary>
	public static readonly Color CautionColor = new Color(0.95f, 0.68f, 0.25f, 1f);

	/// <summary>
	/// The caution a reveal must be confirmed with. Worded as an explicit warning rather than a neutral
	/// label on purpose: these files are shared by the lot and are not meant to be edited casually.
	/// </summary>
	public const string RevealWarning =
		"The following content is universal and proprietary to OpenLot.\n\n"
		+ "Altering or deleting these files may result in broken features — for this lot and for anything "
		+ "else that shares them.\n\n"
		+ "ONLY proceed if you know what you are doing.";

	/// <summary>Shows base content. Called only after the creator confirmed the warning.</summary>
	public static void Reveal()
	{
		Revealed = true;
	}

	/// <summary>Hides base content again. Safe, so it needs no confirmation.</summary>
	public static void Hide()
	{
		Revealed = false;
	}

	/// <summary>
	/// True for a script placeholder carrying base content — the script a new lot ships with. The name
	/// test defers to <see cref="LotArchiveCollect.IsBaseScript"/>, which is also what routes the script
	/// into <c>Secret_Code/</c>, so the editor and the archive agree by construction.
	/// </summary>
	public static bool IsBaseContent(LotScriptNode script)
	{
		if (script == null) return false;
		string name = string.IsNullOrEmpty(script.DisplayName) ? script.Name.ToString() : script.DisplayName;
		return LotArchiveCollect.IsBaseScript(name);
	}

	/// <summary>
	/// True when a node belongs to the separated base-content section — i.e. it is base content,
	/// <b>regardless of whether the section is currently shown</b>. This is the stable "which section
	/// does this belong to" test; <see cref="Revealed"/> decides whether the section is drawn at all.
	///
	/// Base content is never drawn inline among a creator's own nodes: keeping the two lists physically
	/// separate is what stops a shared, proprietary file from being edited by muscle memory right next
	/// to the creator's own work.
	/// </summary>
	public static bool IsSecretContent(Node node)
	{
		return IsBaseContent(node as LotScriptNode);
	}
}
