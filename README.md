# DFSR Analyser

DFSR Analyser reads the DFS Replication debug logs (`Dfsr*.log`, `Dfsr*.log.gz`) from many servers. You can import them or collect them from the servers directly. It then charts event rates over time, so you can spot trends, changes in rate, noisy files and applications, and where changes come from. It only goes down to individual events when you ask, and it can trace one file through every server it replicated to.

It is a Windows desktop app, `DfsrAnalyser.exe`. A command-line companion, `dfsra.exe`, can do the same imports and analysis from a script.

## Install

In PowerShell (Windows PowerShell or PowerShell 7), as yourself, not as administrator:

```powershell
irm https://raw.githubusercontent.com/MrR1974/DFSR-Analyser/main/install.ps1 | iex
```

This downloads the latest release, checks it against its published SHA-256 and installs it. To read the script before running it, open [`install.ps1`](install.ps1).

Or install by hand: download `DfsrAnalyser-<version>.zip` from [Releases](https://github.com/MrR1974/DFSR-Analyser/releases), extract it, and double-click **Install DFSR Analyser.cmd**.

- **Rights and prerequisites:** no administrator rights, and you don't need to install .NET.
- **Where it goes:** the program installs for the current user in `%LOCALAPPDATA%\Programs\DFSR Analyser`.
- **What it adds:**
  - a *DFSR Analyser* Start menu shortcut;
  - the program folder on your PATH, so `dfsra` works in any console you open afterwards (`Install-DfsrAnalyser.ps1 -NoPath` skips this);
  - an entry in *Settings › Apps › Installed apps*.
- **Upgrades:** run the installer again. It replaces the old version and keeps your settings.
- **Uninstall:** use *Settings › Apps › Installed apps*. Your cases are kept, and so is `%LOCALAPPDATA%\DfsrAnalyser\settings.json`, which holds your recent cases and the UTC/local choice.
- **Without installing:** run `app\DfsrAnalyser.exe` or `app\dfsra.exe` straight from the extracted zip. This is handy on a server or in an isolated lab.

The release is a zip with an install script rather than an MSI because the program isn't code-signed, and Windows SmartScreen warns about every unsigned installer it sees downloaded.

Supported: Windows 10/11 and Windows Server 2016 or later, x64. The logs must come from Windows Server 2016 or later.

**How to use it:** the [user guide](docs/User-Guide.md) covers everything from getting good logs to typical investigations. In the app, use **Help › User guide** (F1). The quick start below is the short version.

## Quick start

1. **File › New case…** and pick an empty folder. A *case* is one investigation: a folder holding a DuckDB database (`case.duckdb`), the case's rules (`rules.json`) and its settings (`case.json`). You can reopen a case and add more logs later.
2. Bring in some logs:
   - **File › Import files…** or **Import folder…** for logs you already have. The server name comes from each log's header, or from the sub-folder when you keep one folder per server.
   - **Collect › Collect from servers…** to copy logs straight from the servers. See [Remote collection](#remote-collection).
3. Explore:
   - **Facets** (left) filter by category, server, partner, replication group/folder, originating server, extension, severity, error code and file name. File names accept wildcards: `*`, `?`, `;` between alternatives, and `!` to exclude, as in `*.docx;!~$*`.
   - **Chart** shows event counts per time window. Drag across it to narrow the time range, or type the range into the toolbar. **View › Show local time** switches the display between UTC and local time.
   - **Pivot** is a grid of counts by category and then any dimension you choose, with a rate per minute and a peak for each row. Expand a row to go down to individual events. Select an event to see its raw line, the nested detail, decoded fields with resolved names, and the note from the rule that categorised it.
   - **Top-N** ranks the busiest files, folders, extensions, originating servers, partners, servers, categories, error codes or Class::Method values for the current filter. Double-click a row to filter to it.
   - **File trace** takes a file name (wildcards allowed) or a file ID. It shows each version of that file: where and when it changed, and when each partner received and installed it, with the delay since the change.
4. **Export** writes any of these views to CSV. Times are UTC, plus a local column when the app shows local time.

## Remote collection

*Collect › Collect from servers…* has five steps:

1. **Find the servers.** It lists replication groups and their members from Active Directory. SYSVOL is included, with domain controllers marked.
2. **Choose a time range.** Only log files that overlap the range are copied.
3. **Copy the logs** over the admin share (`\\server\admin$\debug` by default; each server can use a different log folder).
4. **Import them** into the case.
5. **Read the event log too (optional).** It can also read each server's *DFS Replication* event log and show it on the same timeline.

What it needs:

- An account with administrator rights on each server, for the admin share. Use your current sign-in or alternate credentials. Alternate credentials are checked against the domain before any server is contacted, and they are never saved.
- SMB (TCP 445) access to every server.
- For the event log, *Remote Event Log Management* allowed through each server's firewall.

Save a collection as a *profile*. **Collect › Top up** then fetches only what is new since the last run, starting a little before it and skipping files the case already holds complete. Every run writes a diagnostic log to `<case>\logs\`.

## Categories and rules

Each event is put into a category (*Local create*, *Installed: file content*, *Connection failed* and so on, in groups such as *Local change*, *Install* and *Problems*) by the rules in the case's `rules.json`. **Rules › Edit rules file…** opens it. **Rules › Re-apply rules** re-categorises the case without importing anything again. Housekeeping categories, such as companion database lines, are hidden by default (**View › Show hidden categories**). Events that no rule matches go to *Other (\<Module\>)*.

## Command line

`dfsra` with no arguments prints full help. The main commands:

| Command | What it does |
|---|---|
| `dfsra import --case <folder> <files or folders>` | Import logs into a case, creating the case if needed |
| `dfsra stats --case <folder> [--from/--to] [--file <pattern>]` | Totals, categories and signature counts |
| `dfsra top --case <folder> --by files\|folders\|ext\|origin\|partner\|server\|category\|error\|method` | Top-N table |
| `dfsra trace --case <folder> --name <file name>` | Trace one file across servers |
| `dfsra export --case <folder> --what events\|grid\|topn\|trace --out <file.csv>` | CSV export |
| `dfsra discover` / `test` / `collect` / `topup` | Remote collection (`--user DOMAIN\name` prompts for a password) |
| `dfsra verify --case <folder>` | Check a case for duplicate or missing data |
| `dfsra compare --case <a> --case <b>` | Compare two cases file by file |

Times on the command line are local unless you add `--utc`. Examples:

```
dfsra import --case D:\Cases\Branch42 --per-folder D:\Logs\Branch42
dfsra top --case D:\Cases\Branch42 --by origin --from "2026-11-10 08:00" --to "2026-11-10 12:00"
dfsra collect --case D:\Cases\Branch42 --discover --rg "Branch Data" --last 6h
```

## Build from source

You need the .NET 10 SDK.

```
dotnet build DfsrAnalyser.slnx
dotnet test
.\tools\New-Package.ps1            # artifacts\DfsrAnalyser-<version>.zip and .zip.sha256 (the release)
```

A release is a GitHub release tagged `v<version>` with `DfsrAnalyser-<version>.zip` and its `.sha256` attached. `install.ps1` finds both by those exact names.

| Path | Contents |
|---|---|
| `src/DfsrAnalyser.Core` | Parsing, categorisation, name resolution, the case database |
| `src/DfsrAnalyser.Collection` | Windows-only parts: AD discovery, impersonation, the remote event log |
| `src/DfsrAnalyser.App` | WPF app |
| `src/DfsrAnalyser.Cli` | `dfsra` |
| `tests/DfsrAnalyser.Tests` | xUnit tests on lab log fragments |
| `tools/` | Packaging (`New-Package.ps1`), the install and uninstall scripts, the icon generator |
| `install.ps1` | The one-line installer |
| `docs/User-Guide.md` | How to use it |
| `docs/Lab-Test-Checklist.md` | Manual tests against a DFSR lab (collection needs real servers) |
| `PLAN.md` | Design decisions, findings and build notes |
| `DFSR-Debug-Log-Reference.md` | How DFSR debug logs are put together |

## Licence

MIT. See [LICENSE](LICENSE). Third-party components and their licences are listed in [NOTICE.md](NOTICE.md).
