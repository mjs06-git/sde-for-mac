# SDE for AI assistants (MCP): developer notes

> For developers. People using SDE will find the steps in the user guide's [AI assistants](USER-GUIDE.md#16-ai-assistants) chapter.

SDE includes a [Model Context Protocol](https://modelcontextprotocol.io/) server that lets an AI assistant such as
Claude Desktop or Claude Code read an SDE database and, when allowed, edit it. Run the app with the `mcp` verb:

```
SDE.app/Contents/MacOS/SDE mcp --db "<path to the database>"
```

The server speaks JSON-RPC on stdin and stdout and shows no window or Dock icon (`Program.Main` branches before
Avalonia loads). Its code is in `src/Sde.Mcp` and uses the official C# SDK (`ModelContextProtocol`, Apache-2.0).

## Connecting an assistant

**Claude Code**, from a terminal:

```
claude mcp add sde -- "/Applications/SDE.app/Contents/MacOS/SDE" mcp --db "/Users/you/Library/Application Support/SDE/sde.sqlite"
```

Add `--scope user` to make it available in every project.

**Claude Desktop**: Settings, Developer, Edit Config, then add an entry to `claude_desktop_config.json`
(`~/Library/Application Support/Claude/`) and restart Claude Desktop:

```json
{
  "mcpServers": {
    "sde": {
      "command": "/Applications/SDE.app/Contents/MacOS/SDE",
      "args": ["mcp", "--db", "/Users/you/Library/Application Support/SDE/sde.sqlite"]
    }
  }
}
```

Run SDE from the Applications folder. A copy opened straight from Downloads runs from a temporary path (App
Translocation) that changes every time; the server warns about it. Open the database in SDE once before connecting.
The server does not upgrade database tables and refuses a database that needs an upgrade.

## What it can do

Read tools (always on; read-only connections):

| Tool | What it does |
| --- | --- |
| `find_structures` | Lists structures with their assessment ID, address, owner, status and result. Filters: text (like the Structures search box), status, structure type, result, % damaged range, has or missing coordinates, event. Paged. |
| `get_assessment` | Every field that applies, with labels and display values, by editor step; damage by element; result; status; photo count. |
| `explain_result` | How the percent damaged was worked out, in plain English, from a fresh run of FEMA's calculation. |
| `get_history` | The assessment's edit history, newest first. Paged. |
| `get_field_options` | What a field accepts on one assessment, with the choices offered for it now. |
| `field_help` | The editor's field help, with the FEMA P-784 page. |
| `list_events` | The events (floods, storms) with their kind, disaster number, dates and assessment counts. |
| `event_summary` | The Dashboard's numbers (`EventSummary`) for one event or the whole database: results, dollars, dates, review status, top streets, communities and inspectors, team progress. |
| `qa_check` | `QaRules` over chosen assessments, one event or the whole database, with dismissals applied (dismissed findings only on request), grouped by check with its title and severity. Paged. |
| `get_review_comments` | An assessment's review comment threads and replies, so `add_review_comment` can answer one. |
| `list_compliance_cases` | The compliance cases as `ComplianceReport.Rows` gives them. Filters: open or closed, overdue, path, ICC status, event. Paged. |
| `list_layers` | The Map page's layers (`MapLayer`) with their table in `<db>.layers.gpkg`, geometry type, feature count, fields and extent; warns when the layers file belongs to another database. |
| `query_layer` | One layer's features by attribute conditions (AND or OR) and/or a bounding box, with their values and boxes; GeoJSON shapes only when asked, and shapes over 2,000 points left out. Paged. |

Output tools are always allowed. They do not change the database; they write only to `<database folder>/AI outputs/`,
creating the folder when needed and using an available file name. Each result gives the output path:

| Tool | What it does |
| --- | --- |
| `make_report` | The summary or detailed PDF for chosen assessments or an event, through `ReportGenerator` as the app prints it, photos with their markup. |
| `export_geojson` | The structures as RFC 7946 points with SDE's GIS columns plus chosen fields; owner details only with `includeOwnerNames`. Optionally one event. |
| `make_web_map` | `WebMapExport`: internal (dots, popups with chosen fields) or public (grid squares, no owners, addresses hidden or cut to the block), with the event summary when an event is given. |

Edit tools (only while AI edits are on):

| Tool | What it does |
| --- | --- |
| `update_fields` | Changes fields through `SdeStore.Apply` and `FieldInput`, checked exactly as the phone form checks them. |
| `set_status` | Draft, Reviewed or Appeal. Final and Letter sent stay a person's decision. |
| `create_structure` | A new structure and its first assessment (a Draft): address and structure type at least, other fields through `FieldInput`; joins an event when one is given; warns about a duplicate address. |
| `add_review_comment` | `SdeStore.AddReviewComment`, on the whole assessment or one field, or a reply (`replyTo`). Allowed on Final assessments (a comment doesn't change them); refused on checked-out ones. |
| `assign_to_team` | `SdeStore.AssignToTeam` for structures (by assessment or structure ID), a team by ID or name, an event, a due date, and optionally FEMA's Team name. |
| `create_event` | `SdeStore.SaveEvent`; refused when an event of that name exists. |

Prompts: `event_briefing` (an event's numbers, quality problems and compliance work), `qa_review` (works through
`qa_check`, changes nothing unless asked) and `owner_letter_draft` (a plain-language draft for staff to check; it
never invents values and leaves placeholders for what is missing).

Resources: `sde://assessment/{id}` and `sde://field-help`.

## Safety

- Read tools are always on and use read-only SQLite connections. The layers GeoPackage is opened read-only too, and
  is never created by the server.
- Outputs go only into the "AI outputs" folder beside the database, not to an assistant-specified path. This also
  avoids macOS privacy prompts attributed to the assistant's app. `AiOutputs.SafeName` replaces separators and
  look-alike characters with "-", removes control and invisible characters (such as text-direction controls) and
  leading dots, and truncates at whole characters within 100 characters and 200 bytes. The server creates each file
  as new and writes through the handle that created it. It never replaces an existing file or writes through a link,
  including a dangling link; it adds " 2", " 3"… to the name instead. It refuses an "AI outputs" path that is a link
  or a file.
- Edits work only while the database's `Mcp.AllowEdits` setting is on (`AiAssistantSettings`), read again on every
  call. Each edit call is one `SaveContext.Batch` with `Via = "AI assistant"` and `User = "<client name> (AI)"`, so
  `AiAssistantSettings.RecentChanges` can list it and `AiAssistantSettings.Revert` can undo it.
- Changes outside FEMA's fields (a structure, an event, a comment or an assignment) are kept in the batch's record
  row. It is an `EditHistory` row with no structure or assessment, so no history list shows it. It has
  `Field = "AI change"` and details as JSON (`SdeStore.WriteBatchRecord`). `RecentChanges` names the change from that row
  (`AiChangeBatch.Kind`, `RevertNote`), and `Revert` undoes it as follows:
  - fields: put back through `SdeStore.RevertBatch`; a status: set back;
  - a new structure: moved to Recently deleted, unless a person has worked on it since;
  - a new event: removed while nothing has been put in it;
  - a comment: removed while nobody has answered or resolved it;
  - an assignment: each structure goes back to the team it had for that event (or off the team), with FEMA's Team
    name put back, unless it has been visited or reassigned since.

  Anything left alone is counted in `BulkResult.ChangedSince`.
- The first edit of a session takes a backup named "before AI edits".
- Final and Letter sent assessments can't be changed (a review comment, which doesn't change them, is allowed as in
  SDE), and an assistant can't reopen one. Assessments checked out in a work package are refused. A structure's own
  fields (address, owner, flood zone, elevations…) belong to all its assessments, so `update_fields` refuses to change
  them while another assessment of the structure is Final, Letter sent or checked out. `assign_to_team` leaves a
  structure its team has already visited, finished or skipped for that event where it is (moving it would mark it
  Assigned again, which revert couldn't undo). `create_event` keeps to the New event sheet's limits (a datum offset
  within 20 feet either way). Nothing can be deleted, restored, imported, exported to FEMA's format or marked as letter
  sent, and Settings can't be changed.
- The server holds a shared lease on the database, so SDE won't restore a backup or move it underneath.
- Tool descriptions and the server's instructions say that free text in the database and in map layers is data,
  never instructions. Results are paged and long text is shortened to stay under Claude Code's tool-result limit. A
  prompt's message reaches the assistant as the person's own words, so prompts name an event or assessment by ID
  only; its name or address goes only in the prompt's description, on one shortened line.

## Logs

stdout carries only JSON-RPC; every log line goes to stderr, which Claude Desktop keeps in
`~/Library/Logs/Claude/mcp-server-sde.log`. The server also writes `sde-mcp-yyyy-MM-dd.log` (information and up) to
SDE's log folder, `~/Library/Logs/SDE`.

By default, neither log holds the protocol messages themselves. The SDK logs full requests and results at Trace,
including owners' names, addresses and phone numbers, but those lines are excluded so SDE's Recently deleted, backups
and permanent deletes never reach the assistant's log file. To debug the protocol, set `SDE_MCP_LOG_TRAFFIC=1` in the
server's environment (in Claude Desktop's config, an `"env"` entry beside `"args"`). This allows those Trace lines on
stderr; turn it off again after debugging.

## Testing

`tests/Sde.Mcp.Tests` calls every tool and prompt through the SDK's client over in-memory pipes, and spawns the built
app's `SDE mcp` to check that stdout is pure JSON-RPC, also while it writes a PDF and a web map. By hand, the
[MCP Inspector](https://github.com/modelcontextprotocol/inspector) works too:
`npx @modelcontextprotocol/inspector "/Applications/SDE.app/Contents/MacOS/SDE" mcp --db <scratch database>`.
