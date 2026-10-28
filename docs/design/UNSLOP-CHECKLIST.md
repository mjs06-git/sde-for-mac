# Unslop checklist (v3.0)

v3.0's goal is that SDE looks and behaves like a native Mac tool and not like a generic web or SaaS template. Each row
below is a pattern found in the 2.5 UI, paired with the Mac-native replacement. Every v3.0 milestone is checked against
this list, and the anti-slop guard in `tests/Sde.App.Tests/StyleGuardTests.cs` enforces what can be checked mechanically.

## Shell

| 2.5 pattern | Where | v3.0 replacement |
|---|---|---|
| Inter everywhere | `Program.cs` `.WithInterFont()` | System font (SF Pro), macOS type ramp, tabular figures for numbers |
| Hard-coded brand-blue accent | `App.axaml` `BrandAccentColor` | The user's system accent color (brand blue only as fallback) |
| Icon + label nav under WORK / DATA / MANAGE eyebrows | `MainWindow.axaml` sidebar | Source list: Library (smart groups), Events, Workflow, Recently Deleted |
| ⌘K search pill in the title band | `MainWindow.axaml:559` | Toolbar search field at the right edge; palette stays on ⌘K only |
| Blue "+ New" pill button | title band | Toolbar New pull-down, File ▸ New |
| Event and database switcher flyouts | sidebar top and bottom | Events section in the source list; File ▸ Open / Open Recent; window title |
| Import & export page | `ImportExportView.axaml` | File ▸ Import ▸ / Export ▸ menus opening sheets |
| Settings page inside the window | `SettingsView.axaml` | Separate Settings window (⌘,) with toolbar tabs |
| Map and Dashboard as pages | sidebar | Map: List / Map view mode of Structures. Dashboard: Event Summary sheet |
| Centred sheet card over a scrim | `Shell.axaml` sheet host | Sheets attached under the toolbar, Mac sheet metrics |
| Toasts | `MainWindow.axaml:659` | Status-bar message |
| Stacked banners in 7 tones | 30 `c:Banner` | One thin info bar, neutral or warning only |

## Content

| 2.5 pattern | Where | v3.0 replacement |
|---|---|---|
| Row of four KPI cards | Structures, Dashboard, Review | Smart-group counts in the sidebar and a status bar with counts |
| Cards around every section (96 uses) | all pages | Plain grouped content separated by hairlines; grouped boxes only in Settings forms |
| Icon in a tinted rounded square before card titles | Settings, Import & export | No decorative icons; icons only where Mac uses them (toolbar, source list) |
| Tinted pill badges for status | Structures and Review tables | Coloured dot plus plain text |
| Uppercase eyebrow labels (75) | "% DAMAGED", "OPEN CHECKS" | Mac section headers: small, semibold, sentence case |
| Big coloured numbers | KPI cards, result panel | Numbers at body or title size; colour only on the SI result |
| Centred empty state with icon, title and paragraph (18) | `c:EmptyState` | One grey title line in the middle ("No Structures") |
| Repeated 0/25/50/75/100 ruler on every element row | Damage by element | Ruler once in the header; one inline bar per row |
| Step rail with icon tiles and "Complete" ticks | editor | Plain outline list with a count of fields to check |
| Live result as a pink card | editor right column | Inspector pane |

## Copy

| 2.5 pattern | Example | v3.0 replacement |
|---|---|---|
| A subtitle under every page heading | "Exchange data with FEMA's SDE (Windows)…" | No subtitle; the heading is a noun |
| Question-style step subtitles | "What kind of building is it?" | None |
| Helper sentence under fields and sections | "Shown in each assessment's history…" | Tooltip or the field's "?" help; detail in the user guide |
| Multi-sentence explanations in sheets and panels | AI assistants, Check out, Depth tables | One line at most; the rest in the user guide |
| Chatty empty states | "Assessments appear here for review as soon as…" | "No Assessments to Review" |

## Mechanical guard (StyleGuardTests)

- No hex or named colours, FontSize or FontWeight literals (already in 2.5).
- No CornerRadius literals in Views.
- No `card`, `pill`, `eyebrow` or KPI-tile classes in Views.
- No page subtitle class.
- No `c:EmptyState` body text longer than about 40 characters.

## Outcome in 3.0

Walked against the 3.0 screenshots and the approved Sequoia mock-ups on 2026-10-28.

**Done as listed**: the system font and type ramp; the system accent with brand blue as the fallback; the source list
(Library smart groups with counts, Events, Workflow, Recently Deleted); the toolbar search field, with ⌘K kept for the
palette; the New pull-down; events in the sidebar and databases in File › Open / Open Recent with the name under the
title; Map as a view mode of Structures; sheets attached under the toolbar; KPI cards replaced by smart-group counts,
a status bar and one line of counts on the workflow pages; dots in place of status pills; sentence-case section
labels in place of uppercase eyebrows; numbers at body or title size; one-line empty states; the damage ruler once in
the header; a plain step outline with counts; the result as an inspector-style panel; page subtitles, question-style
step subtitles and helper sentences moved to tooltips and the user guide.

**Done differently**:
- Settings and Import & Export stay pages in the main window, reached from SDE › Settings… (⌘,) and View (⌘8). A
  separate Settings window and import/export sheets were left for later.
- The Dashboard became **Summary**, the third view mode of Structures (⌘3, or Event › Event Summary), not a sheet.
- Toasts stay as toasts. Banners are restyled (neutral by default, a fill only for warning and danger) rather than
  merged into one info bar.
- Sheets keep a close button as well as Esc and Cancel.
- Cards remain as grouped boxes (no border, a subtle fill) on Settings, Summary and some panels.

**Guard**: StyleGuardTests checks hex and named colours, font sizes and weights, typed ▾ and ⋯, pill and KPI
classes, all-caps labels, empty states over 60 characters and any text over 200. Not enforced: CornerRadius literals
(the 15 left draw colour swatches, map chips and square edges, where the radius is the shape) and the `card` and
`eyebrow` classes (now restyled in the theme rather than removed).
