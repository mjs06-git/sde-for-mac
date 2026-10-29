# What's new in SDE for Mac 3.0

SDE for Mac 3.0 is a redesign. It now looks and works like a Mac app rather than a web page. FEMA's calculation, file
formats and the database are unchanged: 3.0 opens databases from every earlier version, and 2.5 can still open a
database that 3.0 has used.

![Structures in 3.0](screenshots/01-structures.png)

## A Mac app
- **The Mac's own type** (San Francisco, at the Mac's sizes) in place of Inter.
- **Your accent colour.** Buttons, selection and links follow System Settings › Appearance › Accent colour, adjusted
  so text stays readable in light and dark.
- **Mac controls**: push buttons, pop-up and pull-down menus, segmented controls, a search field, tables with
  alternating rows, and the Mac focus ring.
- **Quieter pages.** The rows of big-number cards, coloured status pills, all-caps labels and the sentence under every
  heading are gone. A status is a coloured dot and a word. Explanations moved to tooltips, each field's **?** and the
  user guide.
- **A new icon**: a house cut away to show floodwater against a staff gauge, with the high-water mark in red.

## Finding your way around
- **The sidebar is a source list.**
  - **Library**: All Structures, and smart groups for Substantially Damaged, Near Threshold, Not Substantial and
    Incomplete, each with a count.
  - **Events**: every event in the database. Click one to see only its structures.
  - **Workflow**: Review, Compliance and Assignments.
  - **Recently Deleted**, at the bottom.
  - Hide or show it with the toolbar's sidebar button or Control-Command-S.
- **The toolbar** shows the page title with the database name under it, the page's own actions (Change, Reports and
  Export on Structures; Check Again and Mark Reviewed on Review; and so on), a List, Map and Summary switch, a **New**
  pull-down and a search field. In the editor it holds the save state, the status pop-up, Previous and Next Step,
  comments, and the Assessments and Reports menus.
- **An inspector** at the right of Structures and Review shows the selected row. Hide or show it with the toolbar
  button or Option-Command-I.
- **Sheets** slide down from under the toolbar, as on the Mac.

## Where things moved

| In 2.5 | In 3.0 |
|---|---|
| Map page | **Map** in the toolbar's view switch (⌘2) |
| Dashboard page | **Summary** in the view switch (⌘3), or Event › Event Summary |
| Settings (a numbered page) | SDE › Settings… (⌘,) |
| Import & export page | View › Import & Export (⌘8). File › Import SDE Data… (⌘I), Export All to SDE… (⌘E) and Import Inspections or Parcels… (⇧⌘I) work from anywhere |
| Event switcher at the top of the sidebar | **Events** in the sidebar; Event › New Event… and Manage Events… |
| Database switcher at the bottom of the sidebar | File › Open Database… (⌘O), Open Recent and New Database…; the database name shows under the page title |
| ⌘K search pill | The toolbar's search field. ⌘K still opens Search and Commands |
| Blue **+ New** button | The toolbar's **New** pull-down, or File › New Residential Structure (⌘N) |
| Cards with counts above Structures, Review, Compliance and the Dashboard | Smart groups with counts in the sidebar, and counts in a status bar under each table |
| Buttons above each table (Check again, Mark reviewed, Open case…, New team…) | The window's toolbar |
| The editor's step rail and its Back and Next buttons | The sidebar lists the assessment's sections while it is open; Previous and Next Step are in the toolbar (⌘[ and ⌘]) |
| **Needs attention** in the editor | **To Check** in the editor's right-hand panel |

The page shortcuts are now ⌘1 Structures, ⌘2 Map, ⌘3 Summary, ⌘4 Review, ⌘5 Compliance, ⌘6 Assignments, ⌘7 Recently
Deleted and ⌘8 Import & Export.

## Structures
- A plain table with 24-point rows. The result column shows a coloured dot and the percent damaged; substantially
  damaged results are in red.
- **The inspector** shows the selected structure: what kind of building it is, the percent damaged against the 50
  percent line, the owner, community, inspection and status, and the costs, with **Report** and **Open**.
- **Filters**, **Views** and **Columns** are small pop-ups above the table. **Change**, **Reports** and **Export** are
  in the toolbar; each acts on the checked structures, or the whole list when none are checked.
- A status bar under the table gives the count and the sort, or how many are checked.

## The editor
- **The sidebar is the assessment's outline** while it is open. It lists the eight numbered sections and the count of
  fields left to check, in red when a required field remains.
- **Fields sit on plain sections** divided by hairlines; what each field means is in its tooltip and its **?**.
- **Damage by element** is a compact table: each element has a percent field and a small slider (0 to 4 still jump to
  0, 25, 50, 75 and 100), and the 0 to 100 ruler appears once, in the header.
- **The right-hand panel** shows the percent damaged, the bar with the 50 percent line, the costs and the fields still
  to check, as links.

## Review, Compliance and Assignments
- **Review**: Queue or Checks, the status filter, Check Again and Mark Reviewed are in the toolbar. The selected
  assessment's checks (one line each, with **Show**) and comments are in the inspector.
- **Compliance**: the case filters and Open Case… are in the toolbar; overdue deadlines are plain red text.
- **Assignments**: teams are a plain list beside the route; New Team…, Order Route, Route Sheet… and Remove from Team…
  are in the toolbar.
- Each page's counts are in a status bar under its table, and results and statuses are dots.

## Summary, Import & Export and Settings
- **Summary** shows the event's figures as a short list alongside the charts. The toolbar's **Export** menu offers
  Event Summary PDF, GeoPackage and GeoJSON exports.
- **Import & Export** and **Settings** keep their pages but lose the boxes inside boxes and the line of explanation
  under every row; the explanations are tooltips.

## Also
- **Empty lists** show one grey line, such as "No Structures".
- **Settings › About** shows the version and the new icon.
- **Menus in the window** show shortcuts the Mac way (⇧⌘N), and a button that deletes something has red text, as in
  a Mac alert.
