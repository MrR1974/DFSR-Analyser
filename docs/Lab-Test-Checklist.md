# DFSR Analyser — remote collection lab test

Manual tests for phase 5 (AD discovery, log collection over the admin share, alternate credentials, top-up,
DFS Replication event log). Run them inside the lab; the developer cannot reach it.

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
| A3 | `.\dfsra.exe discover --user LAB\someadmin` | Prompts for a password (hidden). Same result |
| A4 | A3 with a wrong password | Fails with an LDAP invalid-credentials error; nothing else happens |

## B. Connection test (CLI)

| # | Action | Expected |
|---|---|---|
| B1 | `.\dfsra.exe test --case C:\Cases\lab1 --discover` | One `OK` line per member: `\\<host>\admin$\debug: N log file(s), newest Dfsr000NN.log …` |
| B2 | B1 plus `--event-log` | Adds `event log: DFS Replication log readable, N record(s) in the last 24 h` per server |
| B3 | `.\dfsra.exe test --case C:\Cases\lab1 --servers dfsrdc01,nosuchserver` | DFSRDC01 `OK`; NOSUCHSERVER `FAIL` with a network-path error; exit code 1 |
| B4 | Run B1 as a user **without** admin rights on the servers | `FAIL` with access denied for each server |
| B5 | B4 again with `--user LAB\someadmin` | `OK` (alternate credentials applied per server) |

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
| D7 | *Use alternate credentials* with a valid admin and wrong password | Clear access-denied / logon-failure messages per server; correct password then succeeds |
| D8 | Tick the event-log option and collect | Main window: *Source* facet shows *Event log*; the *Event log* group appears in Category |
| D9 | Collect ▸ Import event log (.evtx)… with a DFS Replication log exported from Event Viewer | Asks for the server name; records appear as in D8 |
| D10 | Start a collection with *Everything on the servers* and press *Cancel* mid-copy | Stops; `Cancelled.` in the log; the case keeps what was committed; a later collection resumes |

## Known behaviours to confirm (not failures)

- Windows allows one set of credentials per server per logon session. If you already have a connection to a server
  (e.g. an open Explorer window on `\\server\c$`) under another account, the log shows error **1219** as a warning
  and the existing connection is used. Close it (`net use \\server\IPC$ /delete`) to test alternate credentials.
- Top-up starts 10 minutes before the last collection to allow for clock differences between this machine and the
  servers; overlapping files are skipped or resumed, never duplicated.
- The active log is copied while DFSR writes to it; the last entry may be incomplete and is re-read on the next top-up.
