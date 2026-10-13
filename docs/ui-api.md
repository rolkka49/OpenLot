# Lot UI API (milestone 5.3)

Scripts drive lot UI through `Lot.*` (the same bound surface as everything else — Lua never touches
Godot, §1.1). All coordinates are pixels from the viewport picture frame's top-left corner, Y down,
and every rect is clamped into the frame, so a script cannot place an element off-screen.

A lot UI element is an entity: it has a handle, it appears in the hierarchy, and a script can
`Subscribe` to it. The kinds are `Frame`, `Button`, `Text`, `Scrollbar`, `Checkbox`, `Dropdown` and
`ScrollContainer`.

## 1. Spawning

| Verb | Notes |
|---|---|
| `SpawnUIFrame(x, y, w, h)` | a plain panel |
| `SpawnUIButton(x, y, w, h, label)` | a pressable button |
| `SpawnUIText(x, y, label)` | a text label (sized automatically) |
| `SpawnUIScrollbar(x, y, w, h)` | a static bar (kept for older lots; see Scroll Area below) |
| `SpawnUICheckbox(x, y, w, h, label, checked)` | a tick box |
| `SpawnUIDropdown(x, y, w, h, options)` | options are `'A|B|C'`; the first starts selected |
| `SpawnUIScrollContainer(x, y, w, h)` | a clipping, scrolling region |

Each returns the element's handle, or `-1`.

## 2. Text and styling

```lua
Lot.SetUIText(h, "Play")        -- Button / Text / Checkbox label
Lot.GetUIText(h)                -- the current text
Lot.SetUITextAlign(h, "Center") -- "Left" / "Center" / "Right"
Lot.GetUITextAlign(h)           -- the current alignment
Lot.SetUIColor(h, r, g, b, a)   -- 0..1 each
Lot.SetUIRect(h, x, y, w, h)    -- clamped into the picture frame
Lot.SetUIVisible(h, false)      -- hides the element (it stays addressable)
Lot.GetUIVisible(h)
```

## 3. Checkbox

```lua
local box = Lot.SpawnUICheckbox(20, 20, 160, 24, "Sound", true)
print(Lot.GetUIChecked(box))    -- true
Lot.SetUIChecked(box, false)
```

When a player toggles it, the element raises `clicked` (see §5) **after** the value changes, so the
handler reads the new state with `GetUIChecked`.

## 4. Dropdown

```lua
local dd = Lot.SpawnUIDropdown(20, 60, 180, 28, "Low|Medium|High")  -- "Low" selected
Lot.SetUIDropdownOptions(dd, "Low|High")   -- replaces the list
Lot.GetUIDropdownOptions(dd)               -- "Low|High"
Lot.GetUIDropdownSelection(dd)             -- "Low"
Lot.SetUIDropdownSelection(dd, "High")     -- select by option text
```

Up to 32 options, each up to 64 characters. Replacing the options keeps a selection whose text
survives and clears one whose text is gone; selecting a text that is not an option changes nothing.

## 5. Reacting to a press

A player's press on a **Button**, **Checkbox** or **Dropdown** runs the element's `clicked` handler —
the same event system every other subscription uses (§3.7):

```lua
local button = Lot.SpawnUIButton(20, 100, 160, 40, "Start")
Subscribe(button, "clicked", function(player)
    print("started by " .. tostring(player))
end)
```

Inside the handler `net.fromPlayer` is `true` (it was player input) and its `net.server` calls inherit
that. Events fire only inside a player session (Test or Game mode), like `touched` and `onFrame`.

## 6. Scroll areas and nesting

A `ScrollContainer` may hold other lot UI elements as children; attach them with the usual
`Reparent` gesture in the Hierarchy, or from Lua when you build the tree. A child's position is
relative to its container's top-left, and the container clips and scrolls its contents. Editing a
container in the Inspector moves and resizes the container itself.

## 7. Authoring vs. runtime

The Inspector shows every element's customization rows (Label, Text Align, Checked, Options,
Selected) with the typed widgets every other property uses; a change is one undo entry. The state a
script sets is the same state the Inspector edits and the same state `lot.json` and a Test-mode
session save and restore.
