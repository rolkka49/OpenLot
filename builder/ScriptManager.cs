using Godot;

/// <summary>
/// File-backed storage for creator scripts (user://Scripts/*.lua), ported from Unity's
/// ScriptManager. Save policy differs on purpose: the editor saves on deactivate, close and a
/// short debounce (handled in CodeEditorWindow), instead of Unity's write-per-keystroke.
/// </summary>
public class ScriptManager
{
	private const string ScriptsDir = "user://Scripts";

	private readonly Builder _builder;

	public ScriptManager(Builder builder)
	{
		_builder = builder;
	}

	public void OpenScriptByName(string scriptName)
	{
		if (string.IsNullOrEmpty(scriptName)) return;
		if (!scriptName.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase))
			scriptName += ".lua";

		string path = ScriptsDir + "/" + scriptName;
		if (Godot.FileAccess.FileExists(path))
		{
			OpenScript(path);
			return;
		}
		// Unknown script name: create it, like Unity's OpenScriptByName did.
		string created = CreateNewScript(scriptName.Substring(0, scriptName.Length - 4));
		OpenScript(created);
	}

	/// <summary>Creates a new uniquely-named .lua file and returns its path.</summary>
	public string CreateNewScript(string baseName)
	{
		if (string.IsNullOrWhiteSpace(baseName)) baseName = "NewScript";
		baseName = baseName.Trim();
		if (baseName.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase))
			baseName = baseName.Substring(0, baseName.Length - 4);

		EnsureDirectory();
		string fileName = SanitizeFileName(baseName) + ".lua";
		string path = ScriptsDir + "/" + fileName;
		int index = 1;
		while (Godot.FileAccess.FileExists(path) && index < 1000)
		{
			fileName = SanitizeFileName(baseName) + "_" + index + ".lua";
			path = ScriptsDir + "/" + fileName;
			index++;
		}

		string initialCode = "-- OpenLot Script: " + fileName + "\n\nfunction start()\n    \nend\n";
		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			GD.PushError("[ScriptManager] Failed to create " + path + ": " + Godot.FileAccess.GetOpenError());
			return path;
		}
		file.StoreString(initialCode);
		file.Dispose();
		return path;
	}

	public void OpenScript(string filePath)
	{
		if (string.IsNullOrEmpty(filePath)) return;
		Godot.FileAccess file = Godot.FileAccess.Open(filePath, Godot.FileAccess.ModeFlags.Read);
		if (file == null)
		{
			GD.PushError("[ScriptManager] Cannot open " + filePath + ": " + Godot.FileAccess.GetOpenError());
			return;
		}
		string code = file.GetAsText();
		file.Dispose();
		_builder.OpenCodeEditor(filePath, code);
	}

	public bool SaveScript(string path, string code)
	{
		Godot.FileAccess file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			GD.PushError("[ScriptManager] Failed to save " + path + ": " + Godot.FileAccess.GetOpenError());
			return false;
		}
		file.StoreString(code);
		file.Dispose();
		return true;
	}

	/// <summary>Renames the script's file and updates its hierarchy placeholder + open editor.</summary>
	public void RenameScriptFile(LotScriptNode script, string newBaseName)
	{
		if (script == null || string.IsNullOrWhiteSpace(newBaseName)) return;
		newBaseName = SanitizeFileName(newBaseName.Trim());

		string oldPath = script.ScriptPath;
		string newPath = ScriptsDir + "/" + newBaseName + ".lua";
		if (oldPath != newPath && Godot.FileAccess.FileExists(oldPath))
		{
			Error error = Godot.DirAccess.RenameAbsolute(oldPath, newPath);
			if (error != Error.Ok)
			{
				GD.PushError("[ScriptManager] Failed to rename " + oldPath + ": " + error);
				return;
			}
		}

		script.ScriptPath = newPath;
		script.DisplayName = newBaseName + ".lua";
		_builder.OnScriptRenamed(oldPath, newPath);
		_builder.MarkDirty();
	}

	private static void EnsureDirectory()
	{
		if (!Godot.DirAccess.DirExistsAbsolute(ScriptsDir))
			Godot.DirAccess.MakeDirRecursiveAbsolute(ScriptsDir);
	}

	private static string SanitizeFileName(string name)
	{
		char[] chars = name.ToCharArray();
		for (int i = 0; i < chars.Length; i++)
		{
			char c = chars[i];
			if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
				chars[i] = '_';
		}
		return new string(chars);
	}
}

/// <summary>
/// Hierarchy placeholder for a .lua script file (Unity created plain GameObjects for this).
/// The display name keeps the ".lua" suffix — Godot node names cannot contain dots, so the node
/// name itself is only a sanitized unique id and DisplayName is what the hierarchy shows.
/// </summary>
public partial class LotScriptNode : Node
{
	public string ScriptPath = "";
	public string DisplayName = "";
}