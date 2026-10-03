using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Build-mode selection (port of Unity's SelectionManager): ordered multi-select over lot nodes.
/// Immediate-mode panels read the state every frame; OnSelectionChanged is the ported public
/// contract for future subscribers (the gizmo tool) — nothing subscribes yet.
/// </summary>
public class SelectionManager
{
	private readonly List<Node> _selected = new List<Node>();

	public event Action OnSelectionChanged;

	public int Count { get { return _selected.Count; } }

	public void Select(Node node, bool multiSelect)
	{
		if (node == null) return;
		if (!multiSelect)
		{
			_selected.Clear();
			_selected.Add(node);
		}
		else
		{
			if (!_selected.Remove(node)) _selected.Add(node);
		}
		RaiseChanged();
	}

	public void Deselect(Node node)
	{
		if (_selected.Remove(node)) RaiseChanged();
	}

	public void ClearSelection()
	{
		if (_selected.Count == 0) return;
		_selected.Clear();
		RaiseChanged();
	}

	/// <summary>
	/// Replaces the whole selection with the given nodes, raising OnSelectionChanged once. Used by
	/// the hierarchy marquee, where selecting row-by-row would fire the event (and reset the gizmo
	/// and outline) once per row.
	/// </summary>
	public void SetSelection(IReadOnlyList<Node> nodes)
	{
		_selected.Clear();
		for (int i = 0; i < nodes.Count; i++)
		{
			Node node = nodes[i];
			if (node == null || _selected.Contains(node)) continue;
			_selected.Add(node);
		}
		RaiseChanged();
	}

	/// <summary>Adds the given nodes to the selection, raising OnSelectionChanged once.</summary>
	public void AddToSelection(IReadOnlyList<Node> nodes)
	{
		bool changed = false;
		for (int i = 0; i < nodes.Count; i++)
		{
			Node node = nodes[i];
			if (node == null || _selected.Contains(node)) continue;
			_selected.Add(node);
			changed = true;
		}
		if (changed) RaiseChanged();
	}

	public bool IsSelected(Node node)
	{
		return _selected.Contains(node);
	}

	/// <summary>First selected node, pruning freed ones; null when nothing is selected.</summary>
	public Node GetFirstValid()
	{
		for (int i = _selected.Count - 1; i >= 0; i--)
		{
			Node node = _selected[i];
			if (!GodotObject.IsInstanceValid(node))
			{
				_selected.RemoveAt(i);
				continue;
			}
			return node;
		}
		return null;
	}

	private void RaiseChanged()
	{
		Action handler = OnSelectionChanged;
		if (handler != null) handler();
	}
}
