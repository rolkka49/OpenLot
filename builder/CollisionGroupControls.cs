using Godot;

/// <summary>
/// The collision-group matrix editor (milestone 3.5), drawn inline inside the builder's View menu —
/// the same home as the environment controls, because it is a view-adjacent lot-wide setting rather
/// than a per-object property (those live in the Inspector, one group dropdown per part).
///
/// The state itself is the session-wide, mutable <see cref="LotCollisionGroups"/> table. This class
/// only draws the checkboxes and, on a change, asks <see cref="BuilderScene.RefreshCollision"/> to
/// push the new matrix into every part's collision body. Writes are marked as a lot change so the
/// creator is warned before quitting, since the matrix is saved in the `.lot` (§4.1).
/// </summary>
public class CollisionGroupControls
{
	public void Draw(Builder builder)
	{
		BuilderScene scene = builder.Scene;
		if (scene == null) return;

		ImGui.TextDisabled("Which groups interact");
		ImGui.Separator();

		bool changed = DrawMatrix();
		ImGui.Separator();
		if (ImGui.Button("Reset (all collide)"))
		{
			LotCollisionGroups.ResetAllCollisions();
			changed = true;
		}

		if (!changed) return;
		scene.RefreshCollision();
		builder.MarkDirty();
	}

	/// <summary>
	/// Draws the symmetric matrix as a grid: a header row of group names, then one row per group
	/// with a checkbox per column. The matrix is symmetric, so a change in any cell is mirrored —
	/// both corners redraw from the same value next frame.
	/// </summary>
	private static bool DrawMatrix()
	{
		int n = LotCollisionGroups.Count;
		bool changed = false;

		if (ImGui.BeginTable("##openlot_collision_matrix", n + 1,
			ImGui.TableSizingFixedFit | ImGui.TableBorders | ImGui.TableBordersInner))
		{
			ImGui.TableSetupColumn("");
			for (int j = 0; j < n; j++) ImGui.TableSetupColumn(LotCollisionGroups.Names[j]);
			ImGui.TableHeadersRow();

			for (int i = 0; i < n; i++)
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				ImGui.AlignTextToFramePadding();
				ImGui.Text(LotCollisionGroups.Names[i]);

				for (int j = 0; j < n; j++)
				{
					ImGui.TableSetColumnIndex(j + 1);
					bool value = LotCollisionGroups.GetCollides(i, j);
					// An empty label with an id so the checkbox has no text but stays uniquely named.
					bool edited = ImGui.Checkbox("##cg_" + i + "_" + j, value);
					if (edited == value) continue;
					LotCollisionGroups.SetCollides(i, j, edited);
					changed = true;
				}
			}
			ImGui.EndTable();
		}

		return changed;
	}
}
