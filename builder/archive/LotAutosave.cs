using Godot;

/// <summary>
/// Autosave and crash recovery (milestone 4.1). Two files under <c>user://</c>:
///   * <see cref="AutosavePath"/> — a periodic snapshot written with the SAME pipeline a manual save
///     uses (<see cref="LotSave"/>), so it can never be a file the loader rejects.
///   * a crash marker — written when the builder starts and removed on a clean exit, so finding it at
///     the next start means the previous session died before it could clean up.
///
/// Autosave deliberately does NOT clear the lot's unsaved-changes state or move the undo save point:
/// that flag means "differs from the last manual save", and clearing it here would silently eat the
/// quit-confirmation prompt that exists to protect the creator's work.
/// </summary>
public static class LotAutosave
{
	/// <summary>Where the periodic snapshot is written. §4.2's recovery loads this path.</summary>
	public const string AutosavePath = "user://autosave.lot";

	/// <summary>Sentinel proving the last session exited cleanly (see the type summary).</summary>
	public const string CrashMarkerPath = "user://.openlot_crash";

	/// <summary>
	/// Seconds between autosaves while the lot is dirty. A const rather than a setting on purpose:
	/// §5.4's settings screen is where a creator would configure it, and nothing before that needs it
	/// configurable.
	/// </summary>
	public const double IntervalSeconds = 300.0;

	/// <summary>Time accumulated since the last autosave. Reset whenever nothing is dirty.</summary>
	private static double _secondsSinceSave;

	/// <summary>Records that a builder session is running, so an unclean exit can be detected next boot.
	/// Best-effort: a data directory we cannot write must not stop the builder from opening.</summary>
	public static void MarkSessionStart()
	{
		Godot.FileAccess file = Godot.FileAccess.Open(CrashMarkerPath, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			LotLog.Warn("autosave", "could not write the crash marker at " + CrashMarkerPath);
			GD.PushWarning("[LotAutosave] could not write the crash marker at " + CrashMarkerPath);
			return;
		}
		file.StoreString("open");
		file.Dispose();
	}

	/// <summary>Clears the crash marker on a clean exit and restarts the autosave countdown.</summary>
	public static void MarkSessionEnd()
	{
		if (Godot.FileAccess.FileExists(CrashMarkerPath)) DirAccess.RemoveAbsolute(CrashMarkerPath);
		_secondsSinceSave = 0;
	}

	/// <summary>True when the previous session died AND there is a snapshot worth offering. A marker
	/// with no snapshot is just a stale sentinel, so it does not raise a prompt.</summary>
	public static bool HasRecoverableAutosave()
	{
		return Godot.FileAccess.FileExists(CrashMarkerPath) && Godot.FileAccess.FileExists(AutosavePath);
	}

	/// <summary>
	/// Forgets the snapshot — the creator declined it. The crash marker is deliberately left alone: by
	/// the time this runs the current session has already re-written it (see <see cref="MarkSessionStart"/>),
	/// so the marker's meaning stays "a session is running", and removing the snapshot alone is what
	/// makes a later boot skip the prompt.
	/// </summary>
	public static void DiscardSnapshot()
	{
		if (Godot.FileAccess.FileExists(AutosavePath)) DirAccess.RemoveAbsolute(AutosavePath);
	}

	/// <summary>
	/// Advances the autosave clock. Called once per frame from the builder.
	///
	/// Nothing is written while a player session is running: the world is being driven by scripts and
	/// may be mid-reload, so it is not a moment to walk the scene. The countdown also restarts whenever
	/// the lot is clean, so the first autosave lands one interval after the first edit rather than
	/// immediately after boot.
	/// </summary>
	public static void Tick(double delta, BuilderScene scene, string lotName, bool dirty, bool inSession)
	{
		if (inSession || !dirty)
		{
			_secondsSinceSave = 0;
			return;
		}

		_secondsSinceSave += delta;
		if (_secondsSinceSave < IntervalSeconds) return;

		_secondsSinceSave = 0;
		if (!SaveNow(scene, lotName, out string error))
		{
			LotLog.Warn("autosave", error);
			GD.PushWarning("[LotAutosave] " + error);
		}
	}

	/// <summary>Writes a snapshot immediately, through the same pipeline a manual save uses.</summary>
	public static bool SaveNow(BuilderScene scene, string lotName, out string error)
	{
		error = "";
		if (scene == null)
		{
			error = "no lot scene to autosave";
			return false;
		}

		// Content whose source file has gone is reported, not silently dropped: an autosave that quietly
		// lost a texture would be a worse failure than one that never happened.
		System.Collections.Generic.List<string> missing = new System.Collections.Generic.List<string>();
		LotArchive.LotManifest manifest = LotSave.BuildManifest(scene.LotRoot, lotName, 0L);
		bool ok = LotSave.WriteScene(scene, AutosavePath, manifest, missing, out error);
		if (ok && missing.Count > 0)
		{
			LotLog.Warn("autosave", missing.Count + " item(s) had no source file to embed and were left out");
			GD.PushWarning("[LotAutosave] " + missing.Count + " item(s) had no source file to embed and were left out");
		}
		return ok;
	}
}
