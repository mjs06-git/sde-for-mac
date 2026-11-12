# Design language (3.5, "High-Water")

SDE's look comes from its icon: a house, floodwater, and a staff gauge with a red high-water mark. This page is the
rulebook for 3.5 and later. `docs/design/UNSLOP-CHECKLIST.md` records how 3.0 got here; its copy rules still apply.
The tests named below enforce the mechanical rules.

## Structure
- **The frame.** The sidebar and the title band are one deep navy frame (`BgFrame`) in both themes. They carry the
  `onFrame` class, and every control placed in them uses the on-frame variants in `Styles/Shell.axaml`: ghost buttons
  with white text, the frame's search field, and one solid accent button (New). Text on the frame uses
  `TextOnFrame`, `TextOnFrameSecondary` and `TextOnFrameTertiary`, never the content text tokens.
- **Content.** The working area sits inside the frame with a rounded top-left corner, on `BgApp`. Working surfaces
  are white cards (`BgSurface`, `RadiusCard`, `ShadowCard`, `BorderCard`) with padding on a 4 px grid.
- **The hero.** One dark card per screen at most (`BgHero`, the `ResultHero` control) for the single most important
  figure: the percent damaged in the Structures inspector and the editor, the headline on Summary, the result on the
  Case File, the counts on Agenda.
- **Inspectors.** Every right-hand inspector uses the shared `InspectorWidth`, the `ResizeHandle` and the one
  inspector toggle (⌥⌘I), so the width is the same and remembered on every page. List pages use `ListDetailPage`.
- **Sheets.** Sheets attach under the title band, dim the window (`BgOverlay`) and end in one `SheetFooter`: an
  optional leading action at the left, then Cancel and one default action at the right. A destructive default action
  is red and Return does not press it. `SheetTests` checks every sheet has exactly one footer.

## Colour
- **Accent.** A strong water blue (`AccentFill`) marks the one primary action in a view, selection, and links.
  Secondary buttons are white with a border.
- **Red** is kept for substantial damage, errors and destructive actions. Near threshold is amber; not substantial is
  green.
- **Tokens only.** Colours, sizes, radii and type sizes live in `Styles/Tokens.axaml`. Views never use literal values
  (`StyleGuardTests`). Every text and background pair keeps 4.5:1 in both themes (`ContrastTests`). The phone form
  (`src/Sde.Field/wwwroot/app.css`) and the reports (`src/Sde.Reports/Internal/Theme.cs`) share the brand core
  colours, and `BrandAgreementTests` checks that they agree.
- **The Mac's accent** replaces SDE's blue only when chosen in Settings › Appearance. `Services/SystemAccent.cs` keeps
  text on and in the accent at 4.5:1.

## Where styles live
- **App-wide or scoped.** `App.axaml` includes the styles that can apply anywhere. A style whose classes are used in
  only one area goes in that area's `Styles/Scope.<Area>.axaml`, which the area's views include in their
  `<UserControl.Styles>` (the sheet card and the sidebar in `Views/MainWindow.axaml` include theirs). Avalonia tries
  every app-wide style on every new control, so page-only styles kept app-wide slow down the Structures table, the
  editor and the map. `ScaleTests` and the map's frame-time tests catch a slowdown.
- **Cheap selectors.** Put a class on the element itself rather than reaching it with a descendant selector
  (`Border.x TextBlock`). A descendant selector checks every ancestor of every matching control, so it never starts
  from `:is(Control)`: name the ancestor's real type, as in `Border.onFrame` or `:is(UserControl).dataPage`.

## Type and shape
- **Public Sans** on all three surfaces, bundled once in `src/Sde.Reports/Fonts`. Tabular figures for numbers and money
  (the `num` and `money` classes), not for dates.
- **Ramp:** caption 11, footnote 12, body 13, subheadline 15, title 18, large title 24, display 30, hero 44. A page's
  main heading inside content is display size; card headings are 15 to 18, semibold.
- **Radii:** small parts 5, controls 7, cards 12, sheets 14.
- **Density.** Comfortable is the default (28 px controls, 36 px rows). Compact (`Styles/Density.Compact.axaml`) keeps
  the 3.0 sizes. Size tokens are read with `DynamicResource` so Compact reaches every style. `FitTests` checks that no
  text is clipped at either density.

## The staff gauge
`Controls/StaffGauge.cs` shows a percent damaged anywhere one appears: a staff with graduations every 10 percent, a
water fill, and the red high-water mark at the threshold (50 by default). The water is blue below the line, amber
within 10 points of it and red at or above it. `IsMini` draws it in a table cell; `OnDark` draws it on the hero. It
names itself for screen readers ("75 percent damaged, at or above the 50 percent line").

## Motion and accessibility
- Sheets slide and fade, the backdrop dims, and hovers ease, only under the `.motion` class on the window (written
  `:is(Window).motion`, so Avalonia looks for it on the window alone). Motion is off under the Mac's Reduce Motion
  setting and in the off-screen tests. There are no page transitions.
- Increase Contrast strengthens borders and secondary text.
- Every control can be reached with the keyboard and has an accessible name. Icon-only buttons have a tooltip too.

## Icons
Phosphor (regular) for the interface, plus SDE's own geometry for the brand mark and a few domain shapes, all as
`StreamGeometry` entries in `Styles/Icons.axaml`. No SVG or image files (`NoSvgTests`).

## Copy (kept from 3.0)
- No sentence under a heading. A heading is a noun.
- No uppercase labels, no rows of big-number cards, no tinted status pills: a status is a dot and a word.
- An empty state is one grey line, at most two buttons.
- Explanations go in tooltips, the field's ?, or the user guide. Warnings the user needs before an irreversible step
  stay on screen.
- Plain English, short sentences, no em dashes. Sentence case, except menu items and buttons.

## Deliberate changes from 3.0
- SDE's own blue is the default accent; the Mac's accent is an option.
- Public Sans replaces the system font, so the app, the phone form and the reports match.
- Sheets dim the window.
- Cards are back as the main surface, inside a navy frame, instead of hairline-separated groups on a plain window.
