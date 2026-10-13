# Milestone 5.4 — Client settings system

**Status:** implemented — verified headless (see §4).
**Related:** §2.3 (the settings file this grows, and the wallpaper block it shares) · §5.1 (the
`theme`/`theme font` command contracts this must not break) · §3.14 (`OrbitalCamera.ZoomStep` /
`BlendStart`, the player-zoom rows) · §3.9 (`LotInputActions`, the single input route §5.5 rebinds) ·
§2.8 (the Output window, whose severity colours now come from here) · §5.6 (tiling, which will reuse
the window-style block) · §5.5 (the Keys tab, its own record).

## 1. What the creator gets

The main menu's theme-only window becomes a tabbed settings surface — **Theme, Window Style, Camera,
Graphics, Fonts** — drawn by **one** shared window (`client/SettingsWindow.cs`) that both hosts
mount: the terminal's main menu and the builder (View → Settings…, and the pause menu inside a
session). One surface means the terminal and build mode can never disagree about what a setting is
called or what it does, the same reasoning the `theme` command and the font pickers already follow.

Every edit previews live and commits on release: a colour drag or a slider re-applies to the running
UI every frame and writes the settings file once, when the widget is deactivated — the
commit-on-release rule §2.4's undo coalescing uses, so dragging a slider is not a file write per
frame.

**Colours live in the Theme tab, shape in the Window Style tab.** The Theme tab offers the nine
preset buttons (three per row), the creator's own **Custom preset** controls, and two colour sections:
*Role colours* (the eight log/severity roles) and **Window colours** — the chrome a user actually sees
as "the window": window/popup/menu surfaces, title bar, buttons, input and slider frames, tabs,
selectable rows, scrollbars, the check mark, **text and disabled text**, and the border, each with an
alpha bar (so window and frame **transparency** is settable). Choosing a preset restyles the roles
*and* the chrome in one step; Window Style keeps the numeric shape (rounding, border size, padding,
title alignment, spacing).

**The creator's own preset** is saved from whatever the theme looks like at that moment: type a name,
press *Save current theme as this preset*, and it can be re-applied later from the *Apply "name"*
button — or from the command line (`theme save <name>`, then `theme custom`; `theme custom clear`
deletes it). It is a snapshot in its own config blocks, so applying a built-in preset afterwards never
loses it.

**Transparency has its own controls** (Theme tab → *Transparency*), because a colour row's alpha field
is easy to miss and, on its own, easy to think broken: **Windows & workspace** (a percentage) sets the
window background *and* the workspace behind the panels — either alone shows no visible change, since
a transparent window on an opaque workspace (or the reverse) still looks solid — and **Input fields
(frames)** sets the input/slider frame alpha. *Reset transparency* restores just those three alphas.
Each row's swatch previews its alpha, and the individual colour rows still allow setting them apart.

## 2. Decisions

| # | Decision | Where it lives |
|---|---|---|
| D1 | **One global theme: palette + chrome colours + window shape.** The eight role colours are the terminal's existing severity vocabulary (kept because `theme foreground|background` is a pinned command contract); the chrome is the 24-slot <see cref="ThemeChrome"/> table (window/popup/menu surfaces, the workspace behind the panels, title bar, buttons, frames, tabs, rows, scrollbars, check mark, text + disabled text, border); the shape is rounding, border size, padding, title alignment, frame and item spacing. **Nine built-in presets** ship — Default, Green Matrix, Amber Retro, Cyberpunk (neon on near-black), Synthwave, Kawaii (pink and white), Navy (blue and white), Forest and Mono — each defined as one entry in a data table (palette + shape + four chrome anchors), so a new theme is one entry rather than a new branch in three accessors. The text anchor is what makes the light presets readable: controls are derived by nudging the surface toward the text colour, so buttons and fields read as raised on white and on black alike. | `ThemePalette`, `ThemeChrome`, `ThemePresets`, `ThemeSettings`, `ThemeService` |
| D2 | **Only what differs from a base is pushed.** `ThemeService.PushChrome()` runs once per layout pass (inside the font push) and skips any value equal to its base, where the base is **the live ImGui style read at startup** — every chrome colour via `ImGui.GetStyleColor` (the wrapper exposes style colours, though not style vars) and ImGui's documented stock values for the shape. The default theme therefore pushes nothing and an install that never opens the settings renders exactly as before; a slot nobody edited keeps following whatever the running style uses. Verified headlessly: the default theme pushes 0 colours, Green Matrix pushes all 20. | `ThemeService.PushChrome`, `ThemeService.LoadPersistedSettings` |
| D3 | **The settings file is the one file.** `[theme]`, `[window_chrome]`, `[window_style]`, `[camera]`, `[graphics]` and (5.5) `[keybindings]` join `[wallpaper]`/`[alias]`/`[font]` in `user://openlot_settings.cfg`; every writer re-reads first, so a save cannot drop another block, and only values that differ from a base are stored. A saved **custom preset** adds `[custom_theme]` (its name + roles), `[custom_chrome]` and `[custom_style]` — its own blocks, so the live theme and the saved one cannot overwrite each other. Each model class takes an **explicit path**, so a suite drives a scratch file (the `FontRegistry`/`AliasTable` pattern). | `ThemeSettings`, `CameraSettings`, `GraphicsSettings` |
| D4 | **Camera settings are global (one set, both cameras).** FOV, look sensitivity and invert-Y were the same hardcoded values on the freecam and the player camera, so one value tuning both is what the current behaviour already means; per-camera values would be a later extension if §11.4's feel check asks. Invert-Y flips the shared look delta at hand-off (`ViewportWindow.TakeLookDelta`), so one flip covers both cameras. §3.14's zoom exposes **speed** and **blend range** only — `ZoomDampRate`/`ZoomMin`/`ZoomMax` stay code constants (no speculative configurability). | `CameraSettings`, `CameraSettingsService`, `BuilderScene.ApplyCameraSettings` |
| D5 | **Graphics: three live, one restart-required.** Resolution scale, the shadow toggle and MSAA apply live to the builder's lot viewport. Resolution scale uses Godot's 3D scaling (`Viewport.Scaling3DScale`) because the viewport's **size** is owned by `ViewportWindow` (the render target follows the displayed rect) — scaling is the only honest place to change render resolution. MSAA and shadows reconfigure the render target, so they apply on release rather than per drag frame. The render METHOD cannot change at runtime in Godot, so it is stored and applied by relaunching with `--rendering-method` (a "Restart now to apply" button, with the exact flag in the failure message); the tab shows the running method beside the stored one rather than offering a switch that quietly does nothing. | `GraphicsSettings`, `GraphicsSettingsService`, `BuilderScene.ApplyGraphicsSettings` |
| D6 | **Dirty/undo are not involved.** These are client preferences, not lot edits: nothing here marks the lot dirty, enters the undo stack, or enters `lot.json`. They also stay out of the §3.6 session snapshot — a testing session must not revert a preference. | `Builder` (no `MarkDirty` on any settings path) |
| D7 | **The Output window's severity colours come from here at draw time** (Info → `Foreground`, Warn → `Warning`, Error → `Error`), read live rather than cached, so a theme change retints the panel without a restart. This closes the item §2.8 queued for this milestone. | `OutputWindow.ColorFor` |
| D8 | **The window is not a modal**, so the scene stays visible behind it — a theme preview only means something if the lot is on screen. The id is pinned (`Settings###openlot_settings`) so a restart restores the same geometry. Both scenes read every block at startup (`Terminal._Ready`, `Builder._Ready`), and `BuilderScene` subscribes to the camera/graphics change events (unsubscribing in `_ExitTree`, so a subscription cannot outlive the scene). | `SettingsWindow`, `Builder`, `BuilderScene` |
| D9 | **Commit-on-release is a pending-edit flag, not just `IsItemDeactivatedAfterEdit`.** A drag moves a value every frame and reports "deactivated after edit" only on release — and that release frame usually reports no movement of its own — so the flag (`_pendingEdit`) is set when a value changes and consumed when the widget is released. Without it a stray deactivation wrote the file with nothing changed (observed while instrumenting the graphics tab), and a release after a drag could skip the write. | `SettingsWindow` |
| D10 | **No per-frame allocations in the drawn tabs**: the chrome/role labels, the MSAA and render-method option arrays and the font row labels are built once (the last one lazily, when the font set changes) rather than on every frame — the project's hot-path rule, and it keeps the settings window out of the GC's way while a session runs. | `SettingsWindow` |
| D11 | **A creator's preset is a snapshot, not a mode.** `theme save <name>` (or the Theme tab's Save button) copies the live palette, chrome and shape into `[custom_theme]`/`[custom_chrome]`/`[custom_style]`; `theme custom` (or *Apply "name"*) re-applies that copy; `theme custom clear` (or *Delete*) forgets it. Because it is a copy, choosing a built-in preset never loses it, and the settings header still reports "Custom" for a hand-edited theme that matches no preset. | `ThemeSettings`, `ThemeService`, `Terminal.HandleThemeCommand` |
| D12 | **Transparency is a control, not just an alpha field — and it needs two slots to work.** The window's own alpha is not enough: the workspace behind the panels (`ColDockingEmptyBg`, a slot the table carries as *Workspace (behind panels)*) is a separate opaque fill, so a "transparent" window still sat on a solid background and looked unchanged. *Windows & workspace* therefore writes `window_bg` **and** `docking_empty_bg` together (and each row still allows setting them apart), the frame slider writes `frame`'s alpha, and *Reset transparency* restores exactly those three alphas from the base. The sliders write only the alpha — never the colour — and the colour swatches preview alpha so a transparent row is obvious. | `ThemeChrome`, `ThemeSettings.SetWindowTransparency`, `SettingsWindow` |

## 3. Scope honestly stated

* **The Fonts tab cannot use a child window.** This addon's child API (`BeginChild`/`EndChild`, both
  the legacy 3-arg and the modern 5-arg form) **hangs** when used inside a tab bar's window — an empty
  child is enough to reproduce it, and the terminal's *previous* settings window (which drew the two
  font lists in children) would have hung the same way, which is how the crash was reported. The Fonts
  tab therefore shows **one list at a time** (radio buttons choose the UI or Console slot) and the
  window's own scrollbar is the scrolling region. Everything else about the font picker is unchanged.
* **Panels keep ImGui's own window background** unless the theme's *Window background* slot is set —
  the default theme matches the live style exactly, so nothing changes until the creator edits it.
* **The render method needs a restart** (D5). The relaunch spawns a new instance via
  `OS.CreateProcess`; if it fails, the message names the exact flag. A manual restart *without* the
  flag keeps the current renderer.
* **Per-window overrides and per-lot creator styles are out of scope** — that is §5.3's territory
  (lot UI styling), not the client theme's.
* **The theme preset colours are a starting point**, not a fixed look: each named preset derives its
  chrome from three anchors that the Window-colour rows can refine. A hand-edited theme re-derives its
  preset label from the palette, the chrome and the shape together, so the header cannot claim
  "Default" after an edit and cannot claim "Custom" when the values reproduce a preset exactly.
* **A known, pre-existing engine artefact, stated so it is not mistaken for this milestone's:** a run
  that quits *early* (before the staged physics probes have cleaned up) while the settings window is
  open can hit Godot's C# "leaked unsafe reference" FATAL in a finalizer (`KinematicCollision3D`,
  `SphereMesh`, … — probe objects, not settings objects). The standard full run
  (`--quit-after 8000`, with or without the window open) exits cleanly, and the milestone's own
  allocation discipline (D10) keeps the settings window out of the GC's way.

## 4. Verification

`ThemeSelfTest` — **71 checks, 0 failures** — the seed palette the default preset restores exactly
(the pre-5.4 literals); the preset table (every listed preset carries a palette, a chrome set and a
shape — the default one deliberately carries none; each preset's text contrasts with its own window
background, which is what makes the light themes readable; no two presets are the same theme under two
names); preset coherence; **the custom preset** (nothing saved → apply refused; save under a name; a
blank name refused without clobbering; the saved snapshot survives applying a built-in; re-applying
restores palette, chrome *and* shape; it round-trips through the real config encoder; deleting it is
not resurrected by the next load); **transparency** (the window control moves the window *and* the
workspace alphas, the frame control only the frames, RGB is never touched, values clamp both ways, the
reset restores exactly the three alphas, and the value survives a save/reload); the base-diff contract
(the default theme's shape and every chrome slot match the base, so nothing is pushed; `ResetChrome`
returns to the base); the parse → apply → persist → reload round-trip on a scratch file; the default
theme storing **no** `window_style` or `window_chrome` block; and a theme save leaving the
wallpaper/alias blocks intact.

`CommandSelfTest` grew to **51 checks** for the wider vocabulary: every listed preset must parse back
to itself (so a name cannot be added to the list and forgotten in the parser), the new presets resolve
with their shorthands (`bladerunner` → Cyberpunk, `sakura` → Kawaii, …), and `custom` resolves to the
user slot.

`ClientSettingsSelfTest` — **27 checks, 0 failures** — the camera and graphics defaults equal the
pre-milestone values (FOV 75, sensitivity 0.15, zoom 1.0/3.5, scale 1.0, MSAA off, shadows on), the
clamps hold in both directions, both blocks round-trip on a scratch file (leaving the wallpaper block
alone), a missing file restores the defaults, and an unknown render method falls back to the engine
default.

Beyond the suites, two things were checked by instrumented headless runs because they are behaviour,
not arithmetic:

* **The chrome really is pushed** — Green Matrix pushes **20** colours, the light Kawaii preset pushes
  **23** (every slot, text included) plus its rounding, and the default theme pushes **none** (the
  "untouched install is unchanged" contract, and what the reported "the blueness cannot be changed"
  bug was about). Reading the style back after the push showed a 50 % transparency arriving as
  `window=(…, 0.5) workspace=(…, 0.5)` — the width of the pushed set follows the theme, it is not a
  fixed list.
* **Every tab body draws** — the settings window was forced open and each of the six tabs selected in
  turn (the selected tab confirmed from the log, so a silently-ignored force cannot pass as
  coverage): all six runs finished cleanly. This is how the Fonts-tab hang was reproduced and fixed.

The chrome's on-screen result (rounding, borders, padding, the tab layout, the colour rows, how each
preset actually reads) is a rendering outcome checked by eye in the running client, the project's
usual split.

