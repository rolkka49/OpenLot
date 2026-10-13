using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// One reversible editor operation (milestone 2.4). Commands are the ONLY way the lot changes
/// through the editor: a command's <see cref="Redo"/> is the mutation path, so push/undo/redo all
/// run the same code and cannot drift apart. Runtime/script-driven changes (the Lua API) do not
/// go through here — the editor history is the creator's editing history, not the world's.
///
/// <see cref="Discard"/> is called when the command leaves the history for good (trimmed off the
/// bottom, cut with the redo branch, or the history is cleared). Commands that hold a node alive
/// across undo use it to free that node if it is currently detached.
/// </summary>
public interface IEditorCommand
{
	string Label { get; }
	void Redo(Builder builder);
	void Undo(Builder builder);
	void Discard(Builder builder);
}

/// <summary>
/// Shared tree operations for the node-lifetime commands. All of them are no-ops on a node that
/// has been freed, so a command can safely outlive a target that something else destroyed.
/// </summary>
public static class EditTreeOps
{
	public static bool IsLive(GodotObject obj)
	{
		return obj != null && GodotObject.IsInstanceValid(obj);
	}

	/// <summary>Adds <paramref name="node"/> under <paramref name="parent"/> at <paramref name="index"/>.</summary>
	public static void Attach(Node node, Node parent, int index)
	{
		if (!IsLive(node) || !IsLive(parent)) return;
		if (node.GetParent() != parent) parent.AddChild(node);
		MoveToIndex(node, index);
	}

	/// <summary>Removes <paramref name="node"/> from whatever parent it has, keeping it alive.</summary>
	public static void Detach(Node node)
	{
		if (!IsLive(node)) return;
		Node parent = node.GetParent();
		if (parent != null) parent.RemoveChild(node);
	}

	/// <summary>
	/// Moves <paramref name="node"/> under <paramref name="newParent"/> at <paramref name="index"/>.
	/// A same-parent move is a plain reorder. A cross-parent move of a Node3D preserves the world
	/// transform (the existing TurnToModel behaviour), so reparenting in the hierarchy does not
	/// teleport the object.
	/// </summary>
	public static void Move(Node node, Node newParent, int index)
	{
		if (!IsLive(node) || !IsLive(newParent)) return;
		Node current = node.GetParent();
		if (current == newParent)
		{
			MoveToIndex(node, index);
			return;
		}

		if (current != null && node is Node3D node3d)
		{
			node3d.Reparent(newParent, true);
		}
		else
		{
			if (current != null) current.RemoveChild(node);
			newParent.AddChild(node);
		}
		MoveToIndex(node, index);
	}

	/// <summary>Clamps an out-of-range index instead of throwing (a parent can be empty).</summary>
	public static void MoveToIndex(Node node, int index)
	{
		if (!IsLive(node)) return;
		Node parent = node.GetParent();
		if (parent == null) return;
		int count = parent.GetChildCount();
		if (count <= 0) return;
		int clamped = System.Math.Clamp(index, 0, count - 1);
		if (node.GetIndex() != clamped) parent.MoveChild(node, clamped);
	}
}

/// <summary>
/// The editor's undo/redo stack (milestone 2.4). A bounded linear history with a cursor: entries
/// below the cursor are applied, entries at/above it are the redo branch. A new push cuts the
/// redo branch. The save point is a cursor value, so undoing back to the last save reports the lot
/// as clean again instead of leaving a sticky dirty flag.
///
/// Capacity is bounded so a long session cannot grow without limit; the oldest entry is discarded
/// (and frees anything it was holding alive) when the cap is exceeded.
/// </summary>
public sealed class EditorHistory
{
	public const int DefaultCapacity = 100;

	private readonly Builder _builder;
	private readonly List<IEditorCommand> _commands = new List<IEditorCommand>();
	private int _cursor;
	private int _savePoint;

	/// <summary>Raised after any change to the stack, the cursor or the save point.</summary>
	public event Action Changed;

	public int Capacity { get; set; } = DefaultCapacity;

	public EditorHistory(Builder builder)
	{
		_builder = builder;
	}

	public bool CanUndo { get { return _cursor > 0; } }
	public bool CanRedo { get { return _cursor < _commands.Count; } }
	public int Count { get { return _commands.Count; } }
	public string UndoLabel { get { return _cursor > 0 ? _commands[_cursor - 1].Label : ""; } }
	public string RedoLabel { get { return _cursor < _commands.Count ? _commands[_cursor].Label : ""; } }

	/// <summary>True when the cursor has moved away from the last save point.</summary>
	public bool IsDirty { get { return _cursor != _savePoint; } }

	/// <summary>
	/// Applies <paramref name="command"/> and records it. The command performs the mutation, so the
	/// call site must not have done it already.
	/// </summary>
	public void Push(IEditorCommand command)
	{
		if (command == null) return;
		CutRedoBranch();
		command.Redo(_builder);
		_commands.Add(command);
		_cursor = _commands.Count;
		TrimToCapacity();
		RaiseChanged();
	}

	public bool Undo()
	{
		if (!CanUndo) return false;
		_cursor--;
		_commands[_cursor].Undo(_builder);
		RaiseChanged();
		return true;
	}

	public bool Redo()
	{
		if (!CanRedo) return false;
		_commands[_cursor].Redo(_builder);
		_cursor++;
		RaiseChanged();
		return true;
	}

	/// <summary>Records the current cursor position as "saved" — called from the save/load pipeline (§4.2).</summary>
	public void MarkSavePoint()
	{
		_savePoint = _cursor;
		RaiseChanged();
	}

	/// <summary>Drops every command, freeing whatever the node-lifetime commands were holding.</summary>
	public void Clear()
	{
		for (int i = 0; i < _commands.Count; i++) _commands[i].Discard(_builder);
		_commands.Clear();
		_cursor = 0;
		_savePoint = 0;
		RaiseChanged();
	}

	private void CutRedoBranch()
	{
		for (int i = _commands.Count - 1; i >= _cursor; i--)
		{
			_commands[i].Discard(_builder);
			_commands.RemoveAt(i);
		}
		if (_savePoint > _cursor) _savePoint = _cursor;
	}

	private void TrimToCapacity()
	{
		int cap = Capacity < 1 ? 1 : Capacity;
		while (_commands.Count > cap)
		{
			_commands[0].Discard(_builder);
			_commands.RemoveAt(0);
			_cursor--;
			if (_cursor < 0) _cursor = 0;
			if (_savePoint > 0) _savePoint--;
		}
	}

	private void RaiseChanged()
	{
		Action handler = Changed;
		if (handler != null) handler();
	}
}

/// <summary>
/// A command built from three delegates, for the many edits whose before/after state is a couple
/// of captured values (Inspector fields, property writes, script renames, UI rects). Closures make
/// each call site self-contained and readable, which is what the milestone asks for over a class
/// per operation.
/// </summary>
public sealed class DelegateCommand : IEditorCommand
{
	private readonly Action<Builder> _redo;
	private readonly Action<Builder> _undo;
	private readonly Action<Builder> _discard;

	public string Label { get; private set; }

	public DelegateCommand(string label, Action<Builder> redo, Action<Builder> undo, Action<Builder> discard = null)
	{
		Label = label ?? "";
		_redo = redo;
		_undo = undo;
		_discard = discard;
	}

	public void Redo(Builder builder) { if (_redo != null) _redo(builder); }
	public void Undo(Builder builder) { if (_undo != null) _undo(builder); }
	public void Discard(Builder builder) { if (_discard != null) _discard(builder); }
}

/// <summary>
/// A transform edit (gizmo drag, direct part drag, Inspector field). Stores the whole local
/// transform, so one command covers position, rotation and scale: the gizmo writes the global
/// transform and the Inspector the local one, and both land here as "the node's transform before
/// and after", which is what a fixed parent makes equivalent.
/// </summary>
public sealed class TransformCommand : IEditorCommand
{
	private readonly Node3D _node;
	private readonly Transform3D _before;
	private readonly Transform3D _after;
	// Size migration: a resize is a transform AND an authored-size change, so one entry carries
	// both. _sized is null for a plain transform entry (and for every node without a size).
	private readonly LotObject _sized;
	private readonly Vector3 _sizeBefore;
	private readonly Vector3 _sizeAfter;
	public string Label { get; private set; }

	public TransformCommand(string label, Node3D node, Transform3D before, Transform3D after)
		: this(label, node, before, after, null, Vector3.One, Vector3.One)
	{
	}

	public TransformCommand(string label, Node3D node, Transform3D before, Transform3D after,
		LotObject sized, Vector3 sizeBefore, Vector3 sizeAfter)
	{
		Label = string.IsNullOrEmpty(label) ? "Transform" : label;
		_node = node;
		_before = before;
		_after = after;
		_sized = sized;
		_sizeBefore = sizeBefore;
		_sizeAfter = sizeAfter;
	}

	public void Redo(Builder builder) { Apply(_after, _sizeAfter); }
	public void Undo(Builder builder) { Apply(_before, _sizeBefore); }
	public void Discard(Builder builder) { }

	private void Apply(Transform3D transform, Vector3 size)
	{
		if (!EditTreeOps.IsLive(_node)) return;
		_node.Transform = transform;
		if (_sized != null && EditTreeOps.IsLive(_sized)) _sized.SetSize(size);
	}

	/// <summary>True when two transforms differ enough to be worth a history entry.</summary>
	public static bool Changed(Transform3D a, Transform3D b)
	{
		return !a.IsEqualApprox(b);
	}

	/// <summary>True when two authored sizes differ enough to be worth a history entry.</summary>
	public static bool SizeChanged(Vector3 a, Vector3 b)
	{
		return !a.IsEqualApprox(b);
	}
}

/// <summary>
/// A transform edit over a whole selection (the multi-object gizmo): one history entry for every
/// member, so a group Move/Rotate/Scale is a single Ctrl+Z that restores all of it — positions,
/// rotations and (for the sized parts) the authored sizes together. The caller only adds members
/// that actually changed, so the label's count matches what moved.
///
/// Per member the storage shape is exactly <see cref="TransformCommand"/>'s: the whole local
/// transform, plus the authored size where the member has one.
/// </summary>
public sealed class TransformNodesCommand : IEditorCommand
{
	private readonly List<Node3D> _nodes = new List<Node3D>();
	private readonly List<Transform3D> _before = new List<Transform3D>();
	private readonly List<Transform3D> _after = new List<Transform3D>();
	private readonly List<LotObject> _sized = new List<LotObject>();
	private readonly List<Vector3> _sizeBefore = new List<Vector3>();
	private readonly List<Vector3> _sizeAfter = new List<Vector3>();
	public string Label { get; private set; }

	public TransformNodesCommand()
	{
		Label = "Transform";
	}

	/// <summary>How many members the entry covers.</summary>
	public int Count { get { return _nodes.Count; } }

	public void Add(Node3D node, Transform3D before, Transform3D after,
		LotObject sized, Vector3 sizeBefore, Vector3 sizeAfter)
	{
		if (node == null) return;
		_nodes.Add(node);
		_before.Add(before);
		_after.Add(after);
		_sized.Add(sized);
		_sizeBefore.Add(sizeBefore);
		_sizeAfter.Add(sizeAfter);
	}

	/// <summary>
	/// Names the entry after what it did and how many members it touched, in the same style as
	/// <see cref="DeleteNodesCommand"/> ("Move 3 Objects" / "Move"). Called before the push.
	/// </summary>
	public void FinishLabel(string verb)
	{
		string name = string.IsNullOrEmpty(verb) ? "Transform" : verb;
		Label = _nodes.Count > 1 ? name + " " + _nodes.Count + " Objects" : name;
	}

	public void Redo(Builder builder) { Apply(_after, _sizeAfter); }
	public void Undo(Builder builder) { Apply(_before, _sizeBefore); }
	public void Discard(Builder builder) { }

	private void Apply(List<Transform3D> transforms, List<Vector3> sizes)
	{
		for (int i = 0; i < _nodes.Count; i++)
		{
			Node3D node = _nodes[i];
			if (node == null || !GodotObject.IsInstanceValid(node)) continue;
			node.Transform = transforms[i];
			LotObject sized = _sized[i];
			if (sized != null && GodotObject.IsInstanceValid(sized)) sized.SetSize(sizes[i]);
		}
	}
}

/// <summary>
/// Attaches a node that was created detached. Undo detaches it again but keeps it alive, so redo
/// re-attaches the SAME node (names, children and handles stay stable across undo/redo). The node
/// is freed only when the command is permanently dropped while detached.
/// </summary>
public sealed class CreateNodeCommand : IEditorCommand
{
	private readonly Node _node;
	private readonly Node _parent;
	private readonly int _index;
	private readonly Action<Builder> _afterAttach;
	public string Label { get; private set; }

	public CreateNodeCommand(string label, Node node, Node parent, int index, Action<Builder> afterAttach = null)
	{
		Label = string.IsNullOrEmpty(label) ? "Create" : label;
		_node = node;
		_parent = parent;
		_index = index;
		_afterAttach = afterAttach;
	}

	public void Redo(Builder builder)
	{
		EditTreeOps.Attach(_node, _parent, _index);
		if (_afterAttach != null) _afterAttach(builder);
	}

	public void Undo(Builder builder)
	{
		EditTreeOps.Detach(_node);
	}

	public void Discard(Builder builder)
	{
		if (!EditTreeOps.IsLive(_node)) return;
		// Only a node that is currently detached is owned by this command; if the user undid and
		// then re-attached it by hand, it is live content and must not be freed.
		if (_node.GetParent() == null && builder.Scene != null) builder.Scene.DestroyEntity(_node);
	}
}

/// <summary>
/// Deletes one or more nodes by detaching them (not freeing them), so one undo puts the whole set
/// back exactly where it was — same nodes, same indices, same handles. This is the editor's only
/// delete path: a single delete is a one-node set, so the Delete key and the hierarchy's context
/// menu cannot drift apart.
///
/// The set is stored in ascending (parent, index) order, which is what makes the undo exact: a node
/// is re-attached with its later siblings already in place, so each insertion pushes them right to
/// where they belong. A node whose ancestor is also in the set is dropped by the caller (detaching
/// the ancestor takes the subtree whole). Each node is freed when the command leaves the history
/// while the deletion is still in effect.
/// </summary>
public sealed class DeleteNodesCommand : IEditorCommand
{
	private readonly List<Node> _nodes = new List<Node>();
	private readonly List<Node> _parents = new List<Node>();
	private readonly List<int> _indices = new List<int>();
	public string Label { get; private set; }

	public DeleteNodesCommand(IReadOnlyList<Node> nodes)
	{
		for (int i = 0; i < nodes.Count; i++)
		{
			Node node = nodes[i];
			if (node == null || !GodotObject.IsInstanceValid(node)) continue;
			Node parent = node.GetParent();
			if (parent == null) continue;
			int index = node.GetIndex();
			// Insertion sort into (parent, index) order — a handful of nodes, and the ordering is
			// load-bearing for Undo, not just cosmetic.
			int at = _nodes.Count;
			while (at > 0 && Compare(_parents[at - 1], _indices[at - 1], parent, index) > 0) at--;
			_nodes.Insert(at, node);
			_parents.Insert(at, parent);
			_indices.Insert(at, index);
		}
		Label = _nodes.Count > 1 ? "Delete " + _nodes.Count + " Objects" : "Delete";
	}

	/// <summary>Parent identity first, then index — the order Undo needs to land every node back.</summary>
	private static int Compare(Node parentA, int indexA, Node parentB, int indexB)
	{
		if (parentA == parentB) return indexA.CompareTo(indexB);
		return parentA.GetInstanceId().CompareTo(parentB.GetInstanceId());
	}

	public void Redo(Builder builder)
	{
		// Later siblings come out first, so detaching one never invalidates another's recorded index.
		for (int i = _nodes.Count - 1; i >= 0; i--)
		{
			Node node = _nodes[i];
			EditTreeOps.Detach(node);
			if (builder.Selection != null && EditTreeOps.IsLive(node)) builder.Selection.Deselect(node);
		}
	}

	public void Undo(Builder builder)
	{
		for (int i = 0; i < _nodes.Count; i++) EditTreeOps.Attach(_nodes[i], _parents[i], _indices[i]);
	}

	public void Discard(Builder builder)
	{
		for (int i = 0; i < _nodes.Count; i++)
		{
			Node node = _nodes[i];
			if (!EditTreeOps.IsLive(node)) continue;
			if (node.GetParent() == null && builder.Scene != null) builder.Scene.DestroyEntity(node);
		}
	}
}

/// <summary>
/// Reorders within a parent or reparents into another one (hierarchy drag and drop, UI draw order).
/// Both ends of the move are stored, so undo is exact even when the destination index shifted.
/// </summary>
public sealed class MoveNodeCommand : IEditorCommand
{
	private readonly Node _node;
	private readonly Node _fromParent;
	private readonly int _fromIndex;
	private readonly Node _toParent;
	private readonly int _toIndex;
	public string Label { get; private set; }

	public MoveNodeCommand(string label, Node node, Node fromParent, int fromIndex, Node toParent, int toIndex)
	{
		Label = string.IsNullOrEmpty(label) ? "Move" : label;
		_node = node;
		_fromParent = fromParent;
		_fromIndex = fromIndex;
		_toParent = toParent;
		_toIndex = toIndex;
	}

	public void Redo(Builder builder) { EditTreeOps.Move(_node, _toParent, _toIndex); }
	public void Undo(Builder builder) { EditTreeOps.Move(_node, _fromParent, _fromIndex); }
	public void Discard(Builder builder) { }
}

/// <summary>
/// A code-editor edit session (milestone 2.4): one entry per session, holding the buffer before and
/// after. Written through <see cref="Builder.ApplyScriptCode"/>, which saves via ScriptManager (the
/// single script writer). Undo/redo does not re-run the lot — scripts still load at lot load (§3.2).
/// If the file is gone (the script was deleted), ApplyScriptCode refuses to recreate it and the
/// entry becomes a no-op rather than resurrecting a deleted asset.
/// </summary>
public sealed class ScriptEditCommand : IEditorCommand
{
	private readonly string _path;
	private readonly string _before;
	private readonly string _after;

	public string Label { get { return "Edit Script"; } }

	public ScriptEditCommand(string path, string before, string after)
	{
		_path = path ?? "";
		_before = before ?? "";
		_after = after ?? "";
	}

	public void Redo(Builder builder) { builder.ApplyScriptCode(_path, _after); }
	public void Undo(Builder builder) { builder.ApplyScriptCode(_path, _before); }
	public void Discard(Builder builder) { }
}


