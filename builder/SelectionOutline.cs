using Godot;

/// <summary>
/// Keeps the 3D selection highlight in sync with SelectionManager plus the object currently being
/// dragged. Driven by events (selection change, drag start/end) instead of per-frame, so the
/// outline pass costs nothing while nothing is selected and nothing is being dragged.
/// </summary>
public sealed class SelectionOutline
{
	private readonly Builder _builder;

	public SelectionOutline(Builder builder)
	{
		_builder = builder;
		_builder.Selection.OnSelectionChanged += Refresh;
	}

	/// <summary>Drops the selection-changed subscription; mirrors GizmoController.Shutdown.</summary>
	public void Shutdown()
	{
		_builder.Selection.OnSelectionChanged -= Refresh;
	}

	/// <summary>
	/// Re-evaluates every lot object's outline state. Called when the selection changes and when a
	/// drag starts or ends.
	/// </summary>
	public void Refresh()
	{
		Node3D dragged = _builder.Parts != null ? _builder.Parts.DraggedNode : null;
		RefreshChildren(_builder.Scene.LotRoot, dragged);
	}

	/// <summary>Walks the lot tree so objects nested inside model groups are highlighted too.</summary>
	private void RefreshChildren(Node parent, Node3D dragged)
	{
		int count = parent.GetChildCount();
		for (int i = 0; i < count; i++)
		{
			Node child = parent.GetChild(i);
			LotObject lotObject = child as LotObject;
			if (lotObject != null)
			{
				if (child == dragged)
				{
					lotObject.SetOutline(LotOutlineState.Dragged);
				}
				else if (_builder.Selection.IsSelected(child))
				{
					lotObject.SetOutline(LotOutlineState.Selected);
				}
				else
				{
					lotObject.SetOutline(LotOutlineState.None);
				}
			}
			RefreshChildren(child, dragged);
		}
	}
}