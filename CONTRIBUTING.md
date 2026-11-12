# Contributing

Thanks for helping. SDE for Mac is used to make substantial damage determinations, so correctness comes first.

## Building and testing
- Install the .NET 10 SDK (on a Mac: `brew install dotnet`).
- `dotnet build Sde.sln && dotnet test Sde.sln`.
- The report tests read PDFs back with poppler (`brew install poppler`); the `.mdb` tests need mdbtools
  (`brew install mdbtools`).
- The calculation-parity and seed-data tests compare against FEMA's own SDE 3.0. Its installer is not in this
  repository, and without it those tests are skipped. To run them, get the installer (`SDE3_04062018.zip`) from FEMA
  and run `tools/answer-key/setup.sh path/to/SDE3_04062018.zip` (it needs msitools and mdbtools). The script checks the
  installer against a pinned SHA-256, unpacks it into `extracted/`, decompiles FEMA's assemblies into `decomp/` with a
  pinned ILSpy for reference, and creates `reference/OriginalCalc`, which compiles FEMA's engine unedited as the tests'
  answer key. Those folders are ignored by git and must never be committed. Run the setup and those tests before
  changing `src/Sde.Calculation`, `src/Sde.Data/Seed` or the export formats.
- To build the app itself (`out/<arch>/SDE.app` and a zip in `out/`), use the three packaging scripts described in
  [RELEASING.md](docs/RELEASING.md).
- The UI tests render the real windows off-screen. On macOS they refresh `docs/screenshots/`; restore those files
  unless your change is meant to alter the UI.
- Tests must not touch your own SDE folder. Each test process sets `SDE_HOME` and `HOME` to a scratch folder
  (`tests/Sde.TestSupport/TestHome.cs`), and `IsolationTests` checks this. Preserve that isolation in new test projects;
  they get it from `tests/Directory.Build.props`.

## Where things are

| Path | Contents |
|---|---|
| `src/Sde.Calculation` | FEMA's damage engine, ported with as few changes as possible |
| `src/Sde.Core`, `src/Sde.Data` | FEMA's records, validation and file formats; SQLite storage, history, backups |
| `src/Sde.App` | The Avalonia app |
| `src/Sde.Reports` | PDF reports, the submission folder, letters, event summary, SI determination |
| `src/Sde.Interop` | Excel, KMZ, GIS, imports, online look-ups, photo metadata |
| `src/Sde.Field` | The phone form's local web server |
| `src/Sde.Mcp` | The server for AI assistants (`SDE mcp`, see [MCP.md](docs/MCP.md)) |
| `src/Sde.Cli` | The `sde` command-line tool |
| `tests/` | Tests and synthetic fixtures |
| `tools/` | The answer-key setup, packaging, the app icon's sources, fixture generators |
| `docs/` | User guides, how it works, what changed from FEMA's version, test report, screenshots |

## Rules that keep the results trustworthy
- `src/Sde.Calculation` is a port of FEMA's engine and must keep giving identical results. Don't "improve" it.
- Export.json, the `.mdb` import and the SDE_n folders must stay exactly as FEMA's SDE writes and reads them. New
  information goes in the Mac-only tables (`src/Sde.Data/Schema.sql`), which are never exported.
- A database written by this version must still open in the previous release.
- Do not add behaviour specific to one city, county, state agency or software vendor. Put such details in a profile or
  a user-created setting instead.
- Test data is synthetic. Never commit real addresses, owner names or photos.
- Dependencies must be under a license that fits the MIT license (MIT, Apache-2.0, BSD). Every package version is
  set in `Directory.Packages.props`; project files list packages without versions. PDFsharp and MigraDoc stay
  on 6.2.x, the last MIT release, because 6.5 and later are AGPL-3.0 or commercial. SkiaSharp moves to a new major
  version only together with Avalonia, which ships its native library.

## Style
- C#: tabs, braces on their own lines, explicit types (no `var`), nullable reference types off.
- XML doc comments in plain English, saying what something is for.
- User-facing text: plain English, short sentences, no em dashes.

## Reporting problems and ideas
- Use the issue forms (**Issues > New issue**): *Something isn't working*, *Result differs from FEMA's SDE*, or *Idea
  or request*.
- Issues are public. Never post real owner names, addresses, parcel numbers, photos or databases; use made-up values.
- Security problems go through private reporting, never a public issue: see [SECURITY.md](SECURITY.md).

## Pull requests
1. Fork the repository and make a branch for your change. For anything larger than a small fix, open an issue first
   so the approach can be agreed before you spend time on it.
2. Keep each pull request to one change. Fill in the template: what changed and why, how you tested it, and anything a
   reviewer should check by hand (screenshots help for UI changes).
3. CI builds and tests on Ubuntu and macOS. Both must pass before a pull request can be merged, and `main` only
   changes through reviewed pull requests.
4. Pull requests are squash-merged, so the pull request's title becomes the commit message: write it as a plain
   sentence saying what the change does.

By contributing, you agree that your contribution is licensed under the project's [MIT license](LICENSE).
