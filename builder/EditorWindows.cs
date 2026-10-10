/// <summary>
/// One toggleable editor window (the post-3.11 friction review): implemented by both core
/// windows today (<see cref="CodeEditorWindow"/>, <see cref="OutputWindow"/>) and registered
/// through <see cref="Builder.AddEditorWindow"/>, which is what puts a window into the Add
/// Window menu and the layout pass. Creation-environment plugins register panels through the
/// same seam later (§13.2) — this interface is deliberately the whole contract: a title, an
/// open flag, and a draw call.
/// </summary>
public interface IEditorWindow
{
	/// <summary>Menu label in Add Window.</summary>
	string Title { get; }

	bool IsOpen { get; }

	void Show();

	void Close();

	void Draw(Builder builder);
}
