# DFSR Analyser — lab tests

Manual tests for phase 5 (AD discovery, log collection over the admin share, alternate credentials, top-up,
DFS Replication event log), phase 6a (manual import, section E) and phase 6b (Top-N, file trace, CSV export, section F). Run them inside the lab; the developer
cannot reach it.

**Every collection command writes a diagnostic log** to `<case>\logs\collect-*.log` (the CLI prints the path;
the Collect dialog shows it with an *Open log file* link). When a step does not match the expected result,
send that log file. It contains no passwords.

## Setup

1. On the build machine: `pwsh tools\Publish-Lab.ps1` → `artifacts\lab\DfsrAnalyser-lab.zip`.
2. Copy the zip into a lab VM that is **joined to the lab domain** (a member server or a third VM; running on a
   DFSR member also works). Extract it, e.g. to `C:\DfsrAnalyser`. No .NET install is needed.
3. Open a PowerShell window in that folder. Commands below use `.\dfsra.exe`; the app is `.\DfsrAnalyser.exe`.
4. Use a fresh case folder for the tests, e.g. `C:\Cases\lab1`.
5. Generate some DFSR activity before step C (copy a few files into a replicated folder on one member).

Record for each step: pass / fail, and the log file name if it failed.

## A. Discovery (CLI)

| # | Action | Expected |
|---|---|---|
| A1 | `.\dfsra.exe discover` (as a domain user) | Domain and the DC used. Every RG listed, **Domain System Volume** marked `[SYSVOL]` with the DCs as members (marked `DC`); your test RG with DFSRDC01 and DFSRMEMBER01 and their DNS names |
| A2 | `.\dfsra.exe discover --domain <dc fqdn>` | Same result, via that DC |
| A3 | `.\dfsra.exe discover --user DFSRTEST\someadmin` | Prompts for a password (hidden). Same result |
| A4 | A3 with a wrong password | Fails with an LDAP invalid-credentials error; nothing else happens |

## B. Connection test (CLI)

| # | Action | Expected |
|---|---|---|
| B1 | `.\dfsra.exe test --case C:\Cases\lab1 --discover` | One `OK` line per member: `\\<host>\admin$\debug: N log file(s), newest Dfsr000NN.log …` |
| B2 | B1 plus `--event-log` | Adds `event log: DFS Replication log readable, N record(s) in the last 24 h` per server |
| B3 | `.\dfsra.exe test --case C:\Cases\lab1 --servers dfsrdc01,nosuchserver` | DFSRDC01 `OK`; NOSUCHSERVER `FAIL` with a network-path error; exit code 1 |
| B4 | Run B1 as a user **without** admin rights on the servers | `FAIL` with access denied for each server |
| B5 | B4 again with `--user DFSRTEST\someadmin` | Log shows `Credentials for DFSRTEST\someadmin accepted by the domain` and `Remote access will use DFSRTEST\someadmin`; `OK` per server |
| B6 | B5 with a **wrong password** (also try it while logged on as a domain admin) | `ERROR: The user name or password for DFSRTEST\someadmin is incorrect …`; no server is contacted; exit code 1 |
| B7 | B5 with a user that exists but has **no rights** on the servers | Credentials accepted; each server `FAIL` with access denied |

## C. Collection (CLI)

| # | Action | Expected |
|---|---|---|
| C1 | `.\dfsra.exe collect --case C:\Cases\lab1 --discover --last 2h --dry-run` | A plan line per file per server: `Copy`, `BeforeRange`, `AfterRange`. Files last written over 2 h ago are `BeforeRange` without an index (not opened). Nothing is copied; the case stays empty |
| C2 | Same without `--dry-run`, plus `--save-profile lab` | Files marked `Copy` are copied and imported; summary per server with events imported; `Saved profile 'lab'` |
| C3 | `.\dfsra.exe stats --case C:\Cases\lab1` | Both servers listed; the activity from Setup step 5 appears (e.g. `Local create` on the member where you copied files, `Transfer: RAWGET` on its partner) |
| C4 | `.\dfsra.exe names --case C:\Cases\lab1` | RG/RF names, connections "from/to" per server, DB GUIDs mapped to servers |
| C5 | Make more changes on a member, wait a minute, then `.\dfsra.exe topup --case C:\Cases\lab1 --profile lab` | Complete files: `AlreadyImported` or `BeforeRange`; the active `Dfsr000NN.log` copied and **Resumed**; new events counted |
| C6 | `.\dfsra.exe collect --case C:\Cases\lab1 --servers "dfsrdc01|\\dfsrdc01\c$\Windows\debug"` | Same files found through the explicit folder (tests the per-server log folder override) |
| C7 | C2 with a server switched off (or `nosuchserver` added via `--servers`) | That server `Failed` with the reason; the others complete normally |
| C8 | C2 with `--event-log` and `--last 24h` | Summary includes `N event-log records`; `.\dfsra.exe sql --case C:\Cases\lab1 "SELECT event_log_id, count(*) FROM event WHERE module='EVTLOG' GROUP BY 1"` lists the event IDs seen in Event Viewer for that period |
| C9 | Repeat C8 | Event-log record count unchanged (records are replaced, not duplicated) |

## D. Collect dialog (app)

| # | Action | Expected |
|---|---|---|
| D1 | `.\DfsrAnalyser.exe`, File ▸ New case (empty folder), Collect ▸ Collect from servers… | Dialog opens |
| D2 | *Discover* | RGs listed with members; ticking an RG adds its members to *Servers*, unticking removes them |
| D3 | *Test connection* | Status `OK` and the share detail per server; diagnostic log streams below |
| D4 | Time range *Last 6 hours*, *Dry run* | Detail column shows `N files listed, N selected, … outside range`; nothing imported |
| D5 | Tick *Save as profile*, name `lab`, *Collect* | Status `Ok` per server; close the dialog; the main window refreshes with the collected events |
| D6 | Collect ▸ Top up ▸ lab | Dialog opens with the profile and *Since last collection (top-up)*; *Collect* copies only new/active files |
| D7 | *Use alternate credentials* with a valid user and a **wrong password**, then *Test connection* and *Collect* | An error box "The user name or password … is incorrect"; every server *Failed* with that message; nothing collected. The correct password then succeeds and the log says `Remote access will use …` |
| D8 | Tick the event-log option and collect | Main window: *Source* facet shows *Event log*; the *Event log* group appears in Category |
| D9 | Collect ▸ Import event log (.evtx)… with a DFS Replication log exported from Event Viewer | Asks for the server name; records appear as in D8 |
| D10 | Start a collection with *Everything on the servers* and press *Cancel* mid-copy | Stops; `Cancelled.` in the log; the case keeps what was committed; a later collection resumes |

## E. Manual import (app and CLI) — phase 6a

Proves that importing log files by hand gives exactly the same case as collecting them, and that bad input
fails file by file. **Every import writes `<case>\logs\import-*.log`** (the app's summary box and status bar give
the path; the CLI prints it). Two checks are used throughout:

- `.\dfsra.exe verify --case <case>`: duplicate events or files, miscounted files, files still marked as growing,
  waiting roll fragments, missing Indexes. `Result: OK` means no defects (warnings explain themselves).
- `.\dfsra.exe compare --case <a> --case <b>`: matches files by server, Index and first line, then compares every
  event (fields, category, nested detail) over the lines both copies hold. `Result: OK` means no shared file
  differs; `SamePrefix` means an active log copied at a different time, identical where they overlap.

Close the app before running `verify` or `compare` on a case it has open (the case database allows one writer).

Setup: on each server, copy `C:\Windows\debug\Dfsr*.log*` (including the active `.log`) into
`C:\Import\<server>\` on the test VM, e.g. `C:\Import\DFSRDC01\`, `C:\Import\DFSRMEMBER01\`.

| # | Action | Expected |
|---|---|---|
| E1 | App: File ▸ New case `C:\Cases\imp-app`, File ▸ Import folder… `C:\Import`, choose **From the sub-folder each log is in** | The dialog lists every file with server, Index, header computer, first event, status *new*; one summary line per server; no warnings. *Import* → a summary box per server (all imported, 0 failed) and the log path |
| E2 | `.\dfsra.exe import --case C:\Cases\imp-cli --per-folder C:\Import` | Same per-server counts as E1; prints the import log path |
| E3 | `verify` on both cases; `compare --case C:\Cases\imp-app --case C:\Cases\imp-cli` | Both `Result: OK`; compare: every file `Identical` |
| E4 | Collect the same servers live (step C or D) into `C:\Cases\imp-collect`, *Everything on the servers*; then `compare --case C:\Cases\imp-app --case C:\Cases\imp-collect` | `Result: OK`. Files copied in setup are `Identical`, the active logs `SamePrefix` (collected later); files that rolled since are *OnlyInB* |
| E5 | App: import `C:\Import` into `imp-app` again (same naming) | The dialog shows every file *already in case* and *Import* is disabled (nothing to do) |
| E6 | Generate DFSR activity, copy the logs again into `C:\Import2\<server>\`, import into `imp-app` | The grown active log shows *in case, will resume*, new Indexes *new*; if the active log rolled, its `.gz` is recognised as the same Index. `verify`: OK |
| E7 | Import `C:\Import` into `imp-collect` (the collected case) | Shared files *already in case*; only files the collection did not take are imported; `verify`: OK |
| E8 | New case `imp-order`: import one server's files one at a time, **highest Index first**, then `compare` with `imp-cli` | After the first files, `verify` warns that opening lines wait for an earlier Index; once all are in: `verify` OK, `compare` all `Identical` |
| E9 | New case: Import folder `C:\Import\DFSRDC01` with **One name for every file** = `OTHERNAME` | Dialog warns *the log headers say Computer:DFSRDC01*; events are filed under OTHERNAME (Server facet) |
| E10 | Bad input, in a copy of one server's folder (`C:\Import-bad\DFSRDC01`): add a text file renamed `Dfsr09999.log` and an empty `Dfsr09998.log`; cut `Dfsr00002.log.gz` short in place (`$b=[IO.File]::ReadAllBytes("$PWD\Dfsr00002.log.gz"); [IO.File]::WriteAllBytes("$PWD\Dfsr00002.log.gz", $b[0..($b.Length/2)])`); copy `Dfsr00003.log.gz` to `Dfsr00003 - Copy.log.gz`. Import into a new case, naming from sub-folders | Dialog marks the text and empty files *unreadable* and warns that Index 3 appears in two files. After import: those two and `Dfsr00002.log.gz` fail (*truncated or damaged … Copy it again*); one Index 3 copy is skipped as *duplicate*; everything else imports; `verify`: OK, with a warning that Index 2 is missing |
| E11 | Import a large folder and press *Cancel* mid-way; import it again | Cancel keeps committed files; the second import completes; `verify` OK; counts equal E2 |
| E12 | On each server, export the *DFS Replication* event log from Event Viewer (.evtx); Collect ▸ Import event log (.evtx)… into `imp-app` | Asks for the server name; records on the timeline (*Source* = Event log). Covers DFSRDC01's event log while C8 is blocked |
| E13 | In `imp-app`: File name box `file*.txt`, then `*.txt;!~$*`; type a From/To inside the span and press Enter; toggle View ▸ local time | Every pane filters; the chart and *Zoom out* follow the typed range; local time shows the same range. Counts in the status bar equal `.\dfsra.exe stats --case C:\Cases\imp-app --file "<pattern>" --from "<from>" --to "<to>"` (local time; add `--utc` if the app shows UTC) |

Send back: the `import-*.log` files, and the `verify` and `compare` output, for any step that did not match.

## F. Top-N, file trace and CSV export — phase 6b

Use `imp-app` (or any case with both servers). Before F3, make a few changes in the replicated folder on one member:
create `trace-test.txt`, then edit it, wait a minute, and delete it. Then collect or top up so those logs are in the case.

| # | Action | Expected |
|---|---|---|
| F1 | *Top-N* tab, *Busiest files*, Top 25; then *Originating servers*, *Partners*, *Extensions* | Ranked lists with share, files (UIDs), servers, per minute, peak and trend. Changing the facets, file name or time range changes the list. Double-click a row: it is added to the filter (facet ticked) |
| F2 | `.\dfsra.exe top --case C:\Cases\imp-app --by files --top 10` (add `--from/--to/--file` to match the app) | Same ranking and counts as F1 (the CLI prints UTC) |
| F3 | *File trace* tab: `trace-test.txt` → Enter | One UID, created on the member where you made the change. One version per change (create, modify, delete). For each: origin, change time, *offered*, and for the partner *received*, RAWGET/RDCGET, *installed* (latency) and outcome. The *Local create/modify/delete* line is marked *[by FID]* |
| F4 | Delete `trace-test.txt` and create it again on the **other** member, top up, trace the name again | Two UIDs listed (same name ≠ same file); pick each one. The new UID's origin is the other member |
| F5 | Pivot grid: expand to an event of a replicated file, then *Trace this file* in Event detail; right-click a File name row ▸ *Trace this file* | The trace tab opens on that file |
| F6 | `.\dfsra.exe trace --case C:\Cases\imp-app --name trace-test.txt`, then `--uid` with one of the listed UIDs | Same milestones and latencies as the app |
| F7 | Export ▸ *Events in view…*, *Grid…*, *Top-N list…*, *File trace…*; open each in Excel | Opens with columns intact (UTF-8). Messages such as `'-> DONE …` start with an apostrophe instead of showing `#NAME?`. With View ▸ local time, each time has a *(local)* column. The events file has as many rows as the status bar shows |
| F8 | Start *Export ▸ Events in view* on the whole case and press *Cancel* | Stops; no partial file is left behind |
| F9 | `.\dfsra.exe export --case C:\Cases\imp-app --what grid --levels category,server --out grid.csv` | Rows sum to the event count for the same filters (`stats`) |
| F10 | Open a case imported or collected **before** this build (e.g. `imp-collect` from round E), then `compare` it with a fresh import of the same logs | `Result: OK`. File names that ran on into `fileSize(approx):…` (DC01 at severity 5) are repaired on open |

## Known behaviours to confirm (not failures)

- Alternate credentials are checked against the domain first (LDAP bind). Remote work then runs under a
  *new-credentials* logon, so existing connections you have to the servers (Explorer windows, earlier runs) do not
  interfere. On the local computer, the event log is read as the logged-on user (logged as a warning).
- If **Remote Event Log Management** is not allowed through a server's firewall, reading its event log fails with
  "The RPC server is unavailable" after about 20 seconds; the log now says so. Enable those firewall rules on the
  server, or export the log there and use *Import event log (.evtx)*.
- Top-up starts 10 minutes before the last collection to allow for clock differences between this machine and the
  servers; overlapping files are skipped or resumed, never duplicated.
- The active log is copied while DFSR writes to it; the last entry may be incomplete and is re-read on the next top-up.
