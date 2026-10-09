using Godot;

/// <summary>
/// Bottom command strip, ported from Unity's BuilderCommandLine: one-line Lua scratchpad. Enter
/// submits the buffer to LuaManager — the seam xLua plugs into when scripting lands. InputText
/// widgets deactivate on Enter, and the wrapper discards their return bool, so submission is
/// detected through IsItemDeactivatedAfterEdit (fires on Enter and on blur-after-edit alike).
/// </summary>
public class BuilderCommandLine
{
	private const float BarHeight = 34f;
	private const int BufferCapacity = 4096;
	private const double StatusDurationSeconds = 4.0;

	private readonly Builder _builder;
	private string _input = "";
	private string _statusText = "";
	private double _statusUntil = -1.0;

	public BuilderCommandLine(Builder builder)
	{
		_builder = builder;
	}

	public void Draw(Builder builder)
	{
		Vector2 displaySize = builder.GetViewport().GetVisibleRect().Size;

		ImGui.SetNextWindowPos(0f, displaySize.Y - BarHeight, ImGui.CondAlways);
		ImGui.SetNextWindowSize(displaySize.X, BarHeight, ImGui.CondAlways);
		int flags = ImGui.WindowNoTitleBar | ImGui.WindowNoResize | ImGui.WindowNoMove | ImGui.WindowNoScrollbar
			| ImGui.WindowNoSavedSettings | ImGui.WindowNoFocusOnAppearing | ImGui.WindowNoDocking | ImGui.WindowNoCollapse;

		bool open = ImGui.Begin("##CommandBar", flags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		ImGui.AlignTextToFramePadding();
		ImGui.Text(">");
		ImGui.SameLine();
		ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 240f);
		string edited = ImGui.InputTextWithHint("##cmdline", "Click here to type or paste XLua code...", _input, BufferCapacity);
		ImGui.PopItemWidth();
		bool deactivatedAfterEdit = ImGui.IsItemDeactivatedAfterEdit();

		if (edited != _input) _input = edited;
		if (deactivatedAfterEdit)
		{
			string code = _input.Trim();
			_input = "";
			if (code.Length > 0) Submit(code);
		}

		ImGui.SameLine();
		if (_statusText.Length > 0 && ImGui.GetTime() < _statusUntil)
			ImGui.TextDisabled(_statusText);

		ImGui.End();
	}

	private void Submit(string code)
	{
		bool executed = LuaManager.Instance.RunString(code, "LotBuilder_CommandLine");
		if (executed)
		{
			_statusText = "ran Lua";
		}
		else
		{
			// Keep the one-line status readable: Lua errors carry full stack traces. The Output
			// window keeps the whole trace; the strip only flashes the first line.
			LotLog.Error("lua", LuaManager.Instance.LastError);
			int lineEnd = LuaManager.Instance.LastError.IndexOf('\n');
			string firstLine = lineEnd > 0 ? LuaManager.Instance.LastError.Substring(0, lineEnd) : LuaManager.Instance.LastError;
			_statusText = "[Lua error] " + firstLine;
		}
		_statusUntil = ImGui.GetTime() + StatusDurationSeconds;
	}
}