# Milestone 5.3 — Lot UI customization

Decision record for the lot UI milestone: new element kinds, descriptor-driven customization,
scroll containers, and the in-session path from a lot-UI press to a script handler.

Status: landed. Code entry points are named inline; the self-test is `tests/LotUiSelfTest.cs`.

## 1. Scope

Deliver, on top of the existing flat `LotUIRoot` element overlay (`builder/lot/LotUIElement.cs`):

1. **More customization options**, declared in the §2.3 `LotPropertyRegistry`, so the Inspector draws
   typed widgets and Lua can set them (one vocabulary, no second path).
2. **New element kinds** — Checkbox, Dropdown — usable as lot UI, not only editor controls.
3. **Scroll containers** that clip and scroll their children through ImGui's own child-region support.
4. **Click routing to script handlers** during a player session, with the §3.7 attribution rule.
5. **Lua access** to the new state (verb-first, §2.2).

## 2. Decisions

| # | Decision | Where |
|---|---|---|
| D1 | **Two render modes, one renderer.** Build mode renders the overlay exactly as before (non-interactive draw-list calls), so `UiGizmoController`'s selection/drag feel is unchanged. A player session renders an **interactive** pass: real ImGui items (an `InvisibleButton` per interactive element) resolve presses through **item state**, which is what the milestone asks for and what lets the Dropdown use ImGui popups. The editor's raw-mouse path stays untouched. | `LotUiRenderer.DrawAll` / `DrawSession`, `ViewportWindow` |
| D2 | **Layering is preserved.** Lot UI is still the OpenLot (ImGui-derived) UI layer of §1.2; Lua never touches any of it — every new capability is a bound `LotLuaApi` verb. | `builder/script/LotLuaApi.cs` |
| D3 | **UI elements may nest.** A `ScrollContainer`'s contents are its `LotUIElement` children; a child's `Position` is **relative to its container's top-left** (uGUI heritage, the port's origin) and rides the container's scroll. Root-level elements stay picture-frame-relative, exactly as today. | `LotUIElement.cs` |
| D4 | **Nesting rides the existing archive shape.** `LotNodeRecord.ParentIndex` already exists and the capture/restore walk is already generic (`LotSceneWalk.WalkChildren` → `LotSceneLoad.RestoreRecords`), so nesting is **additive to v1**: a flat lot is byte-identical, and a build that does not know a new kind warns and skips it (the existing unknown-kind rule). No `formatVersion` bump, no migration. | `LotSceneWalk`, `LotSceneLoad` |
| D5 | **Customization rides the descriptor table.** New UI properties are declared as ordinary `LotPropertyDescriptor`s, so the Inspector widget, the undo entry, `lot.json` and the §3.6 session snapshot all come for free — the same rule §2.3 established for parts. Only the new options are declared; the colors and the rect keep their existing editing paths (the rect is transform state with clamping, and the color already has a typed color widget). | `LotProperty.cs`, `InspectorPanel.cs` |
| D6 | **`clicked` is the event.** A session press on an interactive element queues the already-settled `clicked` event (§3.7) on the element's handle, attributed to the player through the normal delivery path — `net.fromPlayer` is true inside the handler and its `net.server` calls inherit it. No new event vocabulary. | `LotEventRegistry`, `ViewportWindow` |
| D7 | **`Scrollbar` is kept, not replaced.** The milestone's scroll containers supersede it for real scrolling, but the existing kind keeps loading, rendering and scripting unchanged (nothing is cut); it is simply no longer the only scrolling affordance. | `LotUIElement.cs` |
| D8 | **UI elements get handles at session entry.** A script can only `Subscribe` to an element it can address, and an editor-placed element carries a handle only once a registration path touches it (§3.2 option A). The session build ensures a handle for every placed element, exactly as §3.13's query volumes do. | `BuilderScene.EnterTestMode` |


## 3. Execution flow (a press)

```
layout pass (session)          ViewportWindow.Draw → LotUiRenderer.DrawSession
  interactive element          InvisibleButton over the element rect
  press                        ImGui item state (InvisibleButton return)
  checkbox / dropdown          state mutates now (Checked toggles / SelectedIndex set)
  activation recorded          activations.Add(element)

same or next frame             SessionInputRouter.Flush → BuilderScene.EnqueueUiClick(handle)
                               Events.Enqueue(handle, "clicked", character, fromPlayer, peerId)

next ScriptRuntime.Tick        Events.Drain → LuaNetBridge.TryDispatchEvent
                               handler runs as one watchdog unit; net.fromPlayer true
```

The enqueue is session-gated in the same place the sensor and query-volume paths gate (`InTestMode`),
and the delivery obeys the existing coalesce / per-frame cap / two-ended cleanup rules.

## 4. Limitations (stated, not implied)

- **ImGui child windows can overrun a viewport edge.** A `ScrollContainer` is clipped to its own rect,
  but a Dropdown's popup is a separate ImGui window and can render outside the picture frame (the same
  way an ImGui tooltip does). Cosmetic only; the popup closes on selection or outside click.
- **Headless verification covers the data and the routing, not the pixels.** The self-test pins the
  element state rules, the descriptor declarations and accessors, the nesting/clip/hit-test math, the
  archive round-trip (including the real JSON encoder) and the new Lua verbs through the live VM. The
  `clicked` delivery itself rides the event path `EventSelfTest` already drives end to end. How the
  widgets look and how the scroll feels is a rendering outcome checked by eye in the running client —
  the project's usual split.
- **A `ScrollContainer` is a child window.** In build mode that means a press over a container's rect
  resolves to the container, not to the 3D part behind it (the overlay is on top — the intended order),
  and in a session the viewport's hover test had to grow `HoveredChildWindows` (look) and
  `FocusedRootAndChildWindows` (camera authority) so it keeps working over the overlay.
- **`changed` is not a new event.** A handler reads the new value through the getters; a dedicated
  value-change event would be new vocabulary and was deliberately not added here.
- **Build-mode interaction is unchanged.** Element dragging/resizing still belongs to
  `UiGizmoController`; the interactive pass exists only inside a session.

## 5. Verification

`LotUiSelfTest` (53 checks, 0 failures) runs headless under `#if DEBUG` from `BuilderScene._Ready`:
the new kinds' defaults (including the button's centred caption, the pre-5.3 look), the option-list
parse/format with its caps, checkbox toggling, dropdown selection rules (surviving/vanished
selection, unknown option, index clamping), the alignment name round-trip, the five descriptors and
their per-kind scope, the accessor round-trips, the container content height and the topmost-element
hit rule, the hierarchy-drop rule for a container vs a leaf, the archive capture of a nested tree and
its real-JSON round-trip, and every new Lua verb driven through the live VM.

Full headless run: all suites green (0 failures), `LotPropertySelfTest` 42 (its UI pin updated to the
new contract). The widgets' look, the scroll feel and the click → Output line are eye-checked in the
running client.

