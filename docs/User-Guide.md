# DFSR Analyser — user guide

This guide walks through DFSR Analyser from getting good logs to answering typical replication questions. For installing, see the [README](../README.md#install). In the app, **Help › User guide** (F1) opens this guide.

- [1. Before you start: get logs worth analysing](#1-before-you-start-get-logs-worth-analysing)
- [2. Cases](#2-cases)
- [3. Bringing logs in](#3-bringing-logs-in)
- [4. The main window](#4-the-main-window)
- [5. Categories and rules](#5-categories-and-rules)
- [6. Exporting](#6-exporting)
- [7. Typical investigations](#7-typical-investigations)
- [8. The command line](#8-the-command-line)
- [9. Troubleshooting](#9-troubleshooting)
- [10. Where things are kept](#10-where-things-are-kept)

---

## 1. Before you start: get logs worth analysing

DFSR writes its debug logs to `%windir%\debug` on every member (`Dfsr00001.log`, …). Older logs are compressed to `.log.gz`. DFSR Analyser reads both. The logs must come from Windows Server 2016 or later.

**The logs roll.** By default DFSR keeps 1,000 files of 200,000 lines each. Those defaults are generous on Windows Server 2016 and later, but a busy server can still roll through them in hours. If you need to look back further than the logs reach, raise the number of files kept *before* the problem happens again. On each member, in an elevated PowerShell:

```powershell
Get-DfsrServiceConfiguration | Format-List *DebugLog*        # what it is now
Set-DfsrServiceConfiguration -MaxDebugLogFiles 2000          # keep more history
```

**Severity.** The default severity, 4, records every change, transfer, install and error, and it is what DFSR Analyser's rules are built around. Severity 5 adds trace detail (RDC internals, conflict decisions, extended RPC errors, throttling waits). Use it only for a targeted test, because it fills the logs several times faster. `Set-DfsrServiceConfiguration -DebugLogSeverity 5`, and back to 4 afterwards.

**Collect from every server involved.** Each server's log describes only what that server did. To see where a change came from and how long it took to arrive, you need the logs of the server it was made on and of its partners. A partner whose logs you don't have shows up as `unknown {GUID}`.

**Collect soon.** The active log keeps growing and the oldest files are deleted as new ones are written. Collect (or copy) as soon after the event as you can. A collection profile with **Top up** makes repeated collection cheap (§3.2).

## 2. Cases

A **case** is one investigation: a folder that holds

| File | What it is |
|---|---|
| `case.duckdb` | The database of parsed events |
| `rules.json` | The case's categorisation rules (§5) |
| `case.json` | Case settings and saved collection profiles. Never credentials |
| `logs\` | A diagnostic log for every import and collection |

- **File › New case…** picks or creates an empty folder.
- **File › Open case…** (Ctrl+O) opens an existing case folder. **File › Recent cases** lists the last few.

A case can be added to at any time. Files it already holds are skipped. An active log that has grown since the last import is resumed where the last import stopped, so importing the same folder again only adds what is new. Keep one case per investigation: a case for "Branch 42 slow replication, November" is easier to reason about than one huge case of everything.

## 3. Bringing logs in

### 3.1 Import files you already have

**File › Import files…** (Ctrl+I) or **Import folder…** opens the Import dialog. It lists every log it found before anything is written: the server each file will be filed under, its Index, the computer named in its header, its first event, size and status (*new*, *will resume*, *already in case* or *unreadable*).

**Server names** decide which server each file belongs to:

- **From each log's header** (default). Every DFSR log names its computer in its header. This is right unless a server was renamed.
- **From the sub-folder each log is in.** Use this when you keep one folder per server (`…\BRANCH42-FS1\Dfsr00001.log`) and the header names are wrong or ambiguous.
- **One name for every file.** For a pile of logs from a single server.

The dialog warns when the header name and the chosen name disagree (a NetBIOS name against an FQDN is tolerated), when one name would hold logs from several computers, and when the same Index appears twice for one server. Read the warnings before you press **Import**: two servers' logs filed under one name make every later count wrong.

**Don't keep nested detail for Plumbing events** makes the case about 15% smaller. Plumbing events are internal housekeeping. Their counts are kept, only their multi-line detail is dropped.

The import ends with a summary per server. The full record is in `<case>\logs\import-*.log`.

### 3.2 Collect from servers

**Collect › Collect from servers…** copies logs straight from the servers and imports them.

1. **Directory and credentials.** Leave *Domain or DC* blank for this computer's domain. Tick **Use alternate credentials** to use another account (`DOMAIN\user`). The password is checked against the domain before any server is contacted, and it is never saved.
2. **Replication groups in AD.** **Discover** lists the replication groups and their members. SYSVOL (*Domain System Volume*) is included, with domain controllers marked. Tick the groups or servers you want, or type server names into **Servers** and **Add** them.
3. **Log folder.** Blank means `\\<server>\admin$\debug`. Give a different folder per server if a server logs elsewhere (for example `\\FS1\d$\DfsrLogs`).
4. **Time range.** Only log files that overlap the range are copied. Leave *to* blank for "until now". Narrowing the range is the biggest saving on a busy server: files written entirely before the range are skipped without being opened.
5. **Also collect the DFS Replication event log** reads each server's *DFS Replication* event log for the same range and shows its events on the same timeline (category group *Event log*).

Before the real run:

- **Test connection** checks each server's log share (and event log, if ticked) without copying anything. The *Status* and *Detail* columns say what failed.
- **Dry run** lists exactly which files would be copied, and why others are skipped.

**Collect** copies from all servers in parallel, then imports one server at a time. A failing server never stops the others. **Open log file** shows the run's diagnostic log.

**Profiles.** **Save as profile** stores the domain, the servers, their log folders and the event-log option in the case. Afterwards, **Collect › Top up › *profile*** fetches only what is new since the last collection. It starts a little before the last run to be safe and skips files the case already holds complete.

What collection needs:

- An account that is an administrator on each server (for the admin share).
- SMB (TCP 445) from your machine to every server.
- For the event log: the *Remote Event Log Management* firewall rules enabled on each server.

### 3.3 Event logs exported as files

**Collect › Import event log (.evtx)…** imports a *DFS Replication* event log saved with Event Viewer (*Save All Events As…*), for servers you cannot reach remotely. You are asked which server it came from.

## 4. The main window

```
┌ Menu ─────────────────────────────────────────────────────────────────────┐
│ Toolbar: From [date][time]  To [date][time]  Apply · Last 15 min/1 h/24 h │
│          Zoom out · All time        Window · Stack by · Message contains  │
├──────────────┬────────────────────────────────────────────────────────────┤
│ File name    │  Chart: events per window, stacked                         │
│ Facets       ├────────────────────────────────────────────────────────────┤
│  Category    │  [Pivot] [Top-N] [File trace]                              │
│  Server      │                                                            │
│  Partner     ├────────────────────────────────────────────────────────────┤
│  …           │  Event detail                                              │
└──────────────┴────────────────────────────────────────────────────────────┘
```

Everything on the screen (chart, pivot, Top-N and exports) shows the same **view**: the case narrowed by the facets, the file name filter, the time range, *Message contains* and **View › Show hidden categories**. Change any of them and every part follows. **Clear filters** resets the facets, file name and message search, but not the time range.

### 4.1 Time

- **Times are UTC** by default, as in the logs. **View › Show local time** switches the whole display, and the time range boxes, to this computer's time zone. The choice is remembered.
- **Drag across the chart** to narrow to that period. **Click a bar** to narrow to that one window. **Right-click the chart**, **Zoom out** or Alt+Backspace goes back one step. **All time** removes the time filter.
- **Type a range** into *From* / *To* (time as `HH:mm`, `HH:mm:ss` or `HH:mm:ss.fff`) and press Enter or **Apply**. *From* is inclusive and *To* exclusive.
- **Last of the logs: 15 min / 1 h / 24 h** jumps to the end of the case, which is usually where the problem you were called about is.
- **Window** sets the width of each bar, and the window used for *Peak* figures. *Auto* picks one that suits the range.

### 4.2 Facets and filters

The left pane has one facet per dimension: Category, Group, Server, Partner, Connection, Direction, Origin server, Created on server, Replicated folder, Replication group, Extension, File or folder, Severity, Error code, Event ID, Transfer type, Module, Class::Method and more. Each value shows its count in the current view.

- **Tick values** to keep only those. Several ticks in one facet mean *any of them*. Ticks in different facets mean *all of them*.
- **The search box** inside a facet narrows long lists, with the same wildcards as the file name filter.
- **File name** at the top filters by name: `*` and `?` as usual, `;` between alternatives, `!` to exclude. Examples: `*.pst` · `*.docx;*.xlsx;!~$*` · `budget*`.
- **Message contains** (toolbar) matches text anywhere in the event's message.

**Origin server** is where a change was made. **Server** is the log it was found in. They differ for every replicated change, which is what makes the tool useful: filter *Server* = the hub and stack by *Origin server* to see which branches are feeding it.

### 4.3 Chart

Events per window. **Stack by** splits each bar by any dimension: category (default), server, origin server, partner, extension and so on. The legend on the right names the largest series and groups the rest as *Other*.

Rates matter more than totals. A steady background with a sudden step usually means something started, such as an application, a scheduled job, an antivirus scan or a user copying a folder. Stack by *Origin server*, then by *Extension*, to find out what.

### 4.4 Pivot

A grid of counts grouped by up to three dimensions (**Group by ▸ ▸ ▸ events**). The default is *Category ▸ Server ▸ File name*. Each row shows:

| Column | Meaning |
|---|---|
| Count | Events in the view |
| Per min | Average rate over the time range |
| Peak / window, Peak at | The busiest window and when it was |
| First, Last | First and last event |
| Trend | A small chart of the row over the range |

Expand a row to go down a level. Below the last level are the **events themselves**, a page at a time. Select an event to see its **detail** at the bottom: the raw log line, the nested detail lines, the decoded fields with GUIDs resolved to names ("from BRANCH42-FS1", "last changed on HUB-FS1"), and the note from the rule that categorised it. Notes explain misleading entries, for example that `Meet::LocalDominates` is a routine check and not a conflict.

Right-click a row for **Export events of this row…** or, on a file, **Trace this file**.

### 4.5 Top-N

Rankings for the current view: *Busiest files*, *Busiest folders*, *Extensions*, *Originating servers*, *Partners*, *Servers*, *Categories*, *Error codes* and *Class::Method*, top 10, 25, 50 or 100. Each row shows events, share, distinct files, servers, rate and peak.

**Double-click** a row (or right-click › **Add to filter**) to add it to the view. On a file, **Trace this file** opens the trace.

### 4.6 File trace

Type a file name (wildcards allowed) or a file ID (`{GUID}-v<n>`, shown in the event detail) and press **Trace**. If several files match, pick one from the list.

The trace has two parts:

- **Versions:** one row per version of the file (GVSN) and server: where and when the change was made, when each partner received it, how it was transferred (offered, RAWGET or RDCGET), when it was installed with the **latency** since the change, and the outcome on that partner. *Not installed in these logs* means the install isn't in the logs you have.
- **Events:** every event of the file on every server in time order, with the seconds since the trace began.

By default a trace covers the whole case, so it doesn't stop at the edge of the zoom. Tick **Only the current time range** to limit it.

## 5. Categories and rules

Every event gets a **category** from the case's rules. Categories sit in groups:

| Group | Examples |
|---|---|
| Local change | Local create, Local modify, Local rename/move, Local delete |
| Outbound | Update offered to partner, File requested by partner, File ready to serve |
| Inbound | Update received, Transfer: RAWGET (full), Transfer: RDCGET (differential), Download failed |
| Install | Installed: file content, Installed: rename/move, Installed: delete (tombstone), Moved to ConflictAndDeleted: remote delete |
| Conflict | Conflict detected, Conflict reported (4412), Moved to ConflictAndDeleted: conflict loser |
| RDC | RDC statistics (downstream and upstream) |
| Staging | File staged, Staging above high watermark (4202), Staging cleanup completed (4204) |
| Session, Initial sync | Connection established/reset/in sync; Ready for initial sync (4102), Initial sync complete (4104) |
| Problems | Sharing violation, Access denied, RPC failure, AD/LDAP failure, Partner not ready, Connection failed, Install deferred, Error (other), Warning (other) |
| Service & config, Throttling | Service starting/stopped, AD poll, Bandwidth throttling wait |
| Event log | Event log: error / warning / information (from collected event logs) |
| Plumbing | Internal housekeeping (hidden) |

**Each file change is counted once per server.** The visible categories are the one line per change that matters: the USN line for a local change, the *sent* line, the *received* line, the download line, the install line. Companion lines, such as database updates, install steps and dominance checks, are in **hidden** categories so they don't inflate the rates. **View › Show hidden categories** shows them.

A few problem codes are **benign** and are categorised as such: *Redundant download (benign)* (9047), *Superseded staging (benign)* (9024), and errors DFSR itself marks as ignored. Events no rule matches go to *Other (\<module\>)*.

**Changing the rules.** **Rules › Edit rules file…** opens the case's `rules.json` (JSON with comments; the header of the file documents every condition). Rules are tried in order and the first match wins. A rule can match on module, `Class::Method` (with wildcards), message (regular expression), severity, USN reason, transfer type, event log ID and error codes. For example, to give a line-of-business application's lock files their own category:

```jsonc
// in "categories":
{ "name": "LOB lock files", "group": "Local change", "note": "Created and deleted by the LOB app every few seconds." },
// in "rules", above the general Local create/delete rules:
{ "category": "LOB lock files", "classMethod": "UsnConsumer::*", "message": "\\.lck\\b" },
```

Save the file, then **Rules › Re-apply rules**. That re-categorises the whole case in a second or two, with no re-import. A rule with a bad regular expression is refused, and the error names the rule.

## 6. Exporting

**Export** writes CSV files:

| Menu | Contents |
|---|---|
| Events in view… | Every event in the view, with its decoded fields |
| Grid (every group-by combination)… | One row per combination of the pivot's group-by levels, with counts, rates and peaks |
| Top-N list… | The current Top-N table |
| File trace… | The current trace |

Times are UTC, plus a local-time column when the app shows local time. Exports are for Excel, for sharing figures in a ticket, or for comparing one case with another.

## 7. Typical investigations

**"Replication is slow / backlogged. What is everyone changing?"**
1. Collect from the hub and the affected branches for the last few hours.
2. Chart stacked by *Category*. A large *Local change* or *Inbound* band is volume, while a *Problems* band points at a fault.
3. Top-N › *Busiest files*, then *Extensions*, then *Originating servers*. One user, one application or one folder usually accounts for most of it (temporary files, PST files, log files, a backup tool rewriting everything).
4. Double-click the culprit, stack by *Server*, and narrow the time range to when it started.

**"Did this file replicate, and how long did it take?"**
File trace with the file name. The Versions table gives, per partner, when it was received and installed and the latency since the change. A missing partner row means it never arrived in the logs you have. Check that partner's *Problems* events around that time.

**"Why do we have conflicts?"**
Facet *Group* = *Conflict*, Top-N *Busiest files*. Trace one of them. The events show both servers changing the same file within the replication delay. That is a usage pattern (the same file opened in two sites) rather than a DFSR fault.

**"Staging warnings (4202/4204)."**
Facet *Group* = *Staging*, chart stacked by *Server*. Repeated 4202 → 4204 cycles mean the staging quota is too small for the files being replicated. Narrow the time range to a cycle and use Top-N *Busiest files* and *Extensions* to see what was being staged then: usually a burst of large files.

**"Something broke at 10:40."**
Type the range, facet *Group* = *Problems*, pivot *Category ▸ Server ▸ Error code*. RPC failures (1722, 1723, 1753) on one partner only usually mean that partner restarted or was unreachable. AD/LDAP failures point at a domain controller. If you collected event logs, *Event log* events sit on the same timeline.

## 8. The command line

`dfsra.exe` does imports, collection, analysis and exports from a script or a server console. It is on your PATH after installing. Run `dfsra` with no arguments for the full reference. Times on the command line are local unless you add `--utc`.

```powershell
# Import one folder per server
dfsra import --case D:\Cases\Branch42 --per-folder D:\Logs\Branch42

# What is in the case, and is it consistent?
dfsra stats  --case D:\Cases\Branch42
dfsra verify --case D:\Cases\Branch42

# Busiest originating servers in a two-hour window, then busiest .pst files
dfsra top --case D:\Cases\Branch42 --by origin --from "2026-11-10 08:00" --to "2026-11-10 10:00"
dfsra top --case D:\Cases\Branch42 --by files --file "*.pst" --top 50

# Trace one file, export the trace
dfsra trace  --case D:\Cases\Branch42 --name "Budget 2027.xlsx"
dfsra export --case D:\Cases\Branch42 --what trace --uid "{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v1019" --out trace.csv

# Remote collection: discover, test, collect the last 6 hours and save a profile, top up later
dfsra discover
dfsra test    --case D:\Cases\Branch42 --discover --rg "Branch Data" --event-log
dfsra collect --case D:\Cases\Branch42 --discover --rg "Branch Data" --last 6h --event-log --save-profile branch
dfsra topup   --case D:\Cases\Branch42 --profile branch

# Alternate credentials (prompts for the password)
dfsra collect --case D:\Cases\Branch42 --servers fs1,fs2 --last 2h --user CONTOSO\dfsadmin
```

`dfsra sql --case <folder> "<query>"` runs a read-only SQL query against the case database, for questions the app doesn't ask. `dfsra files` lists imported files, and `dfsra compare` compares two cases file by file.

## 9. Troubleshooting

| Symptom | What to check |
|---|---|
| A server fails in **Test connection** with *access denied* | The account isn't an administrator on that server, or alternate credentials are needed. |
| *Network path not found* | SMB (TCP 445) is blocked, the name doesn't resolve, or the server is down. Try `\\server\admin$` in Explorer. |
| No logs in the time range | The logs have rolled past it (§1), or the server logs to another folder. Set the *Log folder* for that server. |
| Event log fails but the logs copy | *Remote Event Log Management* is not allowed through that server's firewall. |
| Many `unknown {GUID}` partners | The logs of those partners aren't in the case. Collect them too. |
| Counts look doubled | Two servers' logs are filed under one name, or the same logs under two names. Run `dfsra verify` and check the import warnings and `logs\import-*.log`. |
| Lots of *Other (…)* | Events the rules don't know. Look at them in the pivot and add a rule (§5). |

Every import and collection writes a log to `<case>\logs\`. Include it when you report a problem.

## 10. Where things are kept

| What | Where |
|---|---|
| The program | `%LOCALAPPDATA%\Programs\DFSR Analyser` |
| App settings (recent cases, UTC/local) | `%LOCALAPPDATA%\DfsrAnalyser\settings.json` |
| Cases | Wherever you created them. Uninstalling never touches them |
| Credentials | Nowhere. Alternate credentials last for one run |
