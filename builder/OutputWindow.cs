using Godot;

/// <summary>
/// The creation environment's Output panel (milestone 2.8): every build-mode diagnostic the lot
/// produces accumulates here and stays readable — script output and errors, save/load outcomes,
/// archive warnings, net and constraint notices. It is the one place Unity's Console / Studio's
/// Output is reproduced, and it replaces the "flash for four seconds and vanish" behaviour of the
/// command strip.
///
/// Editor state, never lot state: the window's filters and the log itself survive a Test-mode
/// round trip, and Game mode never draws it (the layout pass that draws panels is not reached
/// there at all). Colours come from <see cref="LotLogSeverity"/> through one mapping function, so
/// the §5.4 theme service can take it over in one place.
/// </summary>
public sealed class OutputWindow
{
	private const float ScrollSlack = 8f;   // pixels from the bottom that still count as "at bottom"

	/// <summary>Severity colours. The terminal's roles are the starting palette, per the milestone.</summary>
	private static readonly Color InfoColor = new Color(0.86f, 0.86f, 0.86f);
	private static readonly Color WarnColor = new Color(0.95f, 0.75f, 0.3f);
	private static readonly Color ErrorColor = new Color(0.95f, 0.35f, 0.35f);

	private bool _showInfo = true;
	private bool _showWarn = true;
	private bool _showError = true;
	private bool _jumpRequested;

	public bool IsOpen { get; private set; }

	public void Show() { IsOpen = true; }

	public void Close() { IsOpen = false; }

	public void Draw(Builder builder)
	{
		ImGui.SetNextWindowSize(560f, 300f, ImGui.CondFirstUseEver);
		ImGui.SetNextWindowPos(360f, 430f, ImGui.CondFirstUseEver);

		// The id is pinned with "##Output" so the count in the visible title can change every frame
		// (a new entry) without ImGui treating it as a different window.
		string title = "Output (" + LotLog.Count + ")##Output";
		bool open = ImGui.Begin(title, EditorChrome.PanelWindowFlags);
		if (!open)
		{
			ImGui.End();
			return;
		}

		DrawToolbar();

		if (ImGui.BeginChild("ScrollingRegion", 0, 0))
		{
			DrawRows();
			ImGui.EndChild();
		}

		ImGui.End();
	}

	/// <summary>Filters, Clear, and the jump-to-latest action. Drawn above the scrolling child so
	/// they never scroll away with the rows.</summary>
	private void DrawToolbar()
	{
		bool info = ImGui.Checkbox("Info", _showInfo);
		ImGui.SameLine();
		bool warn = ImGui.Checkbox("Warn", _showWarn);
		ImGui.SameLine();
		bool error = ImGui.Checkbox("Error", _showError);
		ImGui.SameLine();

		_showInfo = info;
		_showWarn = warn;
		_showError = error;

		if (ImGui.Button("Clear", 70f, 0f)) LotLog.Clear();
		ImGui.SameLine();
		if (ImGui.Button("Jump to latest", 130f, 0f)) _jumpRequested = true;
	}

	private void DrawRows()
	{
		// "At bottom" is sampled BEFORE this frame's rows are drawn: forcing the scroll every frame
		// would make the check pointless, and a creator who scrolled up must be able to read while
		// new lines keep arriving.
		bool wasAtBottom = ImGui.GetScrollMaxY() <= 0f || ImGui.GetScrollMaxY() - ImGui.GetScrollY() <= ScrollSlack;

		if (LotLog.Count == 0)
		{
			ImGui.TextDisabled("No output yet.");
			return;
		}

		for (int i = 0; i < LotLog.Count; i++)
		{
			LotLog.Entry entry = LotLog.At(i);
			if (!IsVisible(entry.Severity)) continue;

			ImGui.TextDisabled(entry.TimeLabel);
			ImGui.SameLine();
			ImGui.TextDisabled(entry.Source);
			ImGui.SameLine();

			// One style push/pop per row, the terminal console's pattern. The pop must stay inside
			// the child: leaving it open across EndChild() makes the addon's recovery path pop it,
			// and this pop then underflows the native colour stack (see Terminal.DrawConsoleWindow).
			ImGui.PushStyleColor(ImGui.ColText, ColorFor(entry.Severity));
			ImGui.Text(entry.Text);
			ImGui.PopStyleColor();
		}

		if (wasAtBottom || _jumpRequested)
		{
			ImGui.SetScrollHereY(1.0f);
			_jumpRequested = false;
		}
	}

	private bool IsVisible(LotLogSeverity severity)
	{
		switch (severity)
		{
			case LotLogSeverity.Warn: return _showWarn;
			case LotLogSeverity.Error: return _showError;
			default: return _showInfo;
		}
	}

	/// <summary>The one severity-to-colour mapping (the §5.4 theme service's future home).</summary>
	public static Color ColorFor(LotLogSeverity severity)
	{
		switch (severity)
		{
			case LotLogSeverity.Warn: return WarnColor;
			case LotLogSeverity.Error: return ErrorColor;
			default: return InfoColor;
		}
	}
}
