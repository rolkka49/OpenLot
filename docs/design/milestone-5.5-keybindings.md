# Milestone 5.5 — Keybinding customization

**Status:** implemented — verified headless (see §4).
**Related:** §5.4 (the settings window this adds a Keys tab to, and the file it shares) · §3.9
(`LotInputActions`, the single input route a gameplay rebind moves) · §2.4 (the code editor's
in-widget undo and clipboard conventions, which stay fixed) · §16.1 (gamepad rebinding, deliberately
not here).

## 1. What the creator gets

A **Keys** tab in the shared settings window lists every rebindable row with one button per bound key:
click a key to change it, `x` to remove it, `+` to add another — **an action may hold as many keys as
the creator wants** (Ctrl+S *and* Enter for save, two movement keys, …); adding a key never replaces
the one already there. Two families:

* **Gameplay (7)** — moveForward/Backward/Left/Right, jump, sprint, interact. These are the logical
  actions §3.9 registered, so a rebind moves the character, the freecam and every script that reads
  the action **together** (one route, three consumers), and every key of the row lands in the InputMap
  as its own binding.
* **Editor (5)** — Save lot (Ctrl+S), Undo (Ctrl+Z), Redo (Ctrl+Shift+Z **and** Ctrl+Y), Delete
  selection (Delete) and Toggle terminal (`~`). These are the chords that used to be hardcoded in
  `Builder._UnhandledInput` / `Terminal._UnhandledInput`; the two redo keys live on **one** row now
  that a row can hold several.

Bindings persist in `[keybindings]` of `user://openlot_settings.cfg` and are applied at startup, so a
rebind survives a restart — and because a gameplay rebind goes through `LotInputActions.RebindKeys`,
the active keys are what the InputMap holds from the first frame of the next session.

## 2. Decisions

| # | Decision | Where it lives |
|---|---|---|
| D1 | **A chord is a value type**: key + Ctrl/Shift/Alt, with `Format`/`TryParse` as the settings-file pair (Godot's `OS.GetKeycodeString` / `OS.FindKeycodeFromString` for the key name) and `Matches(InputEventKey)` as the one test the handlers use. **Ctrl also means Cmd (Meta)** — the convention the code editor's clipboard resolver already uses, so "Ctrl+Z" is the same binding on macOS. Modifier state is read from the **event** (again like the clipboard bridge), so the check does not depend on polling `Input` and cannot miss a modifier released in the same frame as the key. | `KeyChord` |
| D2 | **Defaults are exactly the pre-5.5 keys.** Nothing about an untouched install changes; the self-test pins every default. Redo's two chords (Ctrl+Shift+Z and Ctrl+Y) are **both defaults of the one Redo row** — the menu has always accepted both, and removing one would be a feature cut; before this pass they were two rows only because a row could hold a single key. | `KeyBindings.BuildDefaults` |
| D3 | **Conflicts are refused, never stolen — per key.** A key another row already uses is rejected with a message naming the owner; a key the **same row** already holds is rejected as a duplicate ("… is already on <row>."); there is no "steal anyway" flow in v1, and no key is ever silently moved. A **gameplay** row additionally refuses modifiers (movement is plain keys — `RebindKeys` has no modifier field), with a message saying so. Assigning a slot replaces that slot; assigning at the row's count (the `+`) appends. | `KeyBindingService.TryAssign`, `KeyBindings.TryAssign`, `KeyBindings.FindConflict` |
| D4 | **One resolver, so the menu, the key and the behaviour cannot drift.** `KeyBindingService.ResolveEditorCommand(InputEventKey)` is pure and returns the row id, checking **every key of every editor row** (row order, then the row's key order) so a second key on a command is enough for it to answer to both. `Builder._UnhandledInput` and `Terminal._UnhandledInput` route through it; the menu accelerators render `KeyBindingService.ChordLabel(id)` — the row's **primary** (first) key, so a rebound key shows in the menu and an unbound row shows none. The **gating is unchanged**: Save never fires inside a player session, and Undo/Redo/Delete stay out of the code editor's text field (there the key is text editing and ImGui consumes it). | `KeyBindingService`, `Builder`, `Terminal` |
| D5 | **Capture is fed from the host's `_Input`, not from ImGui.** While a text field has focus the addon marks every key event handled, so the one point where a press is still visible is `_Input` on the scene's own nodes — the same seam and the same reason as the code-editor clipboard bridge. A capture targets a **slot** (`BeginCapture(id, index)`): an existing index replaces that key, the row's current count appends one. A **bare modifier keeps waiting** (Escape cancels, since Escape is deliberately not a rebindable chord); a refused chord still ends the capture with its message reported where the host logs (the terminal's scrollback, the builder's Output window). | `KeyBindingService.TryCapture`, `Builder._Input`, `Terminal._Input` |
| D6 | **Gameplay rows push into the InputMap; editor rows are matched.** `ApplyToInputMap` calls `LotInputActions.RebindKeys` for the seven actions with **one key event per bound key** (keyboard events only — gamepad bindings stay as registered, since pad rebinding is §16.1); `Rebind(action, key)` stays as the single-key convenience over it. An unbound row clears the action's keyboard events (the action simply never fires). Editor chords are read through `Matches`. One apply path runs after every assignment/removal and at load, so the table and the live InputMap cannot diverge. | `KeyBindingService`, `LotInputActions` |
| D7 | **The live table is a plain instance behind a static holder** (`KeyBindings` / `KeyBindingService`), path-explicit like the other settings models, so a suite exercises defaults, conflicts, persistence and the capture machine without touching the real settings or the live InputMap. | `KeyBindings`, `KeyBindingService` |
| D8 | **Several keys per row, stored as a comma-separated list.** One `[keybindings]` value per row (`editor.save = "Ctrl+S, Enter"`; a chord never contains a comma, so the list is unambiguous), with three distinct states: a **missing** key means "the default", an **empty** value means **unbound** (a row may legitimately have no keys — removing the last one unbinds the action rather than quietly restoring a default, and the row shows `(unbound)` with its `+` still there to rebind it), and a value whose entries cannot be parsed is also left unbound rather than silently replaced by the defaults. A single-chord value written by an earlier build loads as a one-entry list, so **no migration was needed**. `Restore defaults` restores each row's own full default list. | `KeyBindings.Load/Save`, `KeyBinding` |

## 3. Scope honestly stated

* **Fixed by convention, not rebindable:** Escape (leave session / close the modal), the code editor's
  Ctrl+C/V/X and Ctrl+Z (text-editing conventions and ImGui's own in-widget undo, §2.4), and the
  builder's other hardcoded keys (arrow keys in the lot list, Enter to submit). The Keys tab says
  plainly that gameplay keys take plain keys while editor chords may use modifiers.
* **Unbinding is allowed, rebinding a bare modifier is not.** Removing every key from a row leaves the
  action unbound (`(unbound)` in the tab, no key event in the InputMap for a gameplay row, nothing for
  the menu to show) — recoverable with `+` or `Restore default keys`. A bare modifier is never a
  binding of its own; the defaults use Shift for sprint and Ctrl for the editor chords.
* **Gamepad rebinding is not here** — §16.1 owns it, and `RebindKeys` touches keyboard events only.
* **The capture's on-screen feel** (the per-key "Press a key…" slot, the `x`/`+` controls, the message
  in the Output window / scrollback) is an eye-check in the running client, the project's usual split.

## 4. Verification

`KeyBindingSelfTest` — **77 checks, 0 failures**:

* the 12 rows and their exact pre-5.5 defaults (WASD, Space, Shift, E, Ctrl+S, Ctrl+Z,
  Ctrl+Shift+Z + Ctrl+Y on one Redo row, Delete, `~`), that gameplay rows are modifierless, that every
  row starts at its default and bound, and that an out-of-range key read is harmless;
* the chord round-trip for every default (Format → TryParse → same key and modifiers), the modifier
  spelling, the bare-modifier test, Ctrl-vs-Meta equivalence, and the refusal of a release/echo/null;
* parse refusals (empty, null, an unknown modifier, an unknown key name);
* conflicts per key in both directions (the owner is named; a row ignores the slot it is replacing; a
  key already on the **same** row reports as a duplicate; a row's **second** key is matched too), plus
  replace-in-place, the same-key no-op, add-duplicate refusal and `RestoreDefaults` on a local table;
* the `[keybindings]` round-trip on a scratch file — **both keys of a row** survive in order, an edited
  editor key and an edited gameplay key survive, an untouched row keeps its default (extra defaults
  included), a row left without keys is stored as an empty value and read back unbound, the on-disk
  form is the documented list, a missing file restores the defaults, a **pre-multi-key single value**
  loads as a one-entry list, an unparseable value leaves the row unbound, `RestoreDefaults` restores a
  multi-key row's full list, and the wallpaper block survives the save;
* the pure editor resolver on synthesized events: Ctrl+S → save, plain S → nothing, Ctrl+Z → undo,
  Ctrl+Shift+Z → redo, Ctrl+Y → the same redo, Delete → delete, `~` → the terminal toggle, an
  unbound key → nothing, and a gameplay key is never an editor command; the accelerator label is the
  live chord, and a multi-key row's label is its primary key;
* the capture machine: a bare modifier keeps the capture open, a free key is assigned to the slot it
  was captured for and reported, a capture at the row's count **adds** a key (reported as an
  addition), both keys then resolve to the same command, the `x` drops one key and keeps the rest,
  removing the last key unbinds the action and says so, an out-of-range removal is refused, a
  conflicting key is refused (naming the owner) and still ends without applying, a gameplay action
  refuses modifiers, an unknown id is refused, and the live bindings/settings path are restored
  exactly as they were found;
* the **InputMap side** on a real action: two keys become two key events, the pad events survive a
  keyboard rebind, an empty row clears the keyboard shortcuts, and re-applying the table writes exactly
  the row's own keys — the gameplay half of every claim above, measured on Godot's own `InputMap`
  rather than only on the table (`RebindKeys` registers the action itself, so this needs no session).

The staged probes and every other suite stay green in the same run (`InputSelfTest`'s live rebind
probe in particular, which exercises the same InputMap route), and the full headless run
(`--quit-after 8000`) reports 0 failures.

