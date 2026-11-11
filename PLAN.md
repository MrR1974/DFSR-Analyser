# DFSR Analyser — Plan

A Windows desktop app that ingests DFSR debug logs from many servers, by upload or by remote collection, and shows event rates over time. The aim is to spot trends, rate changes, noisy files and applications, and where changes originate. Drill-down to individual events happens only on request.

Domain reference: `DFSR-Debug-Log-Reference.md` (log anatomy, identifiers, lifecycle, scenario signatures, error codes). Section numbers below (§) refer to it.

---

## 1. Decisions so far

| Area | Decision |
|---|---|
| Form | WPF desktop app, **.NET 10 LTS** (supported to Nov 2028; .NET 8 support ended 2026-11-10), MVVM (CommunityToolkit.Mvvm). Self-contained per-user install |
| Scale | Large: many servers, up to 1000 rotated files of ~200k lines (≈200M lines) per server |
| Storage | One **case** per investigation: a folder holding an embedded DuckDB database. Saved, reopenable, topped up incrementally |
| Input | Upload files/folders (`Dfsr*.log`, `Dfsr*.log.gz`); remote collection over the admin share (SMB) |
| Discovery | Enumerate replication group members from AD |
| Collection scope | Analyst picks a time range; only files that overlap it are copied |
| Credentials | Current user by default; alternate credentials per collection run (never saved) |
| Time | All timestamps converted to UTC using each log's `TimeZone` header. Display toggles between UTC and analyst-local |
| Event type | Curated categories (from the reference), with drill-down to raw Module and Class::Method |
| Taxonomy | Editable rules file; can be re-applied to a case without re-parsing |
| Names | GUIDs resolved from the logs themselves (config dumps, `csName`/`rgName`, inference) |
| Dimensions | Server, partner/connection, RG, RF, file name/folder/extension/file-vs-folder, originating server, severity, error code |
| Layout | Facet filter pane on the left; time-series chart and expandable grid on the right |
| UI controls | WPF built-ins only (virtualised `DataGrid`, lazy DuckDB-backed paging per expanded level) plus ScottPlot. No commercial control suites |
| Detail kept | Nested detail stored compressed for every event. Import option to drop it for the "Plumbing" categories |
| v1 extras | Top-N tables, cross-server file trace, CSV export |
| Errors | Severity and error code are ordinary dimensions; no dedicated errors view |
| OS support | Windows Server 2016 and later (so the 2008+ ID-record fields are always present) |
| SYSVOL | Treated as an ordinary replication group; AD discovery includes domain controllers |
| Event log | Optional switch: also collect the "DFS Replication" event log and show it on the same timeline |
| Samples | `samples/` is excluded from git (`.gitignore`). Test fragments are cut from lab logs only |
| Publishing | Private for now, may go public later. Keep code free of environment-specific names; samples and test fragments must be from lab domains only |

---

## 1a. Findings from the sample logs

Samples (excluded from git): `samples/debugLogs/<member>/`, one folder per DFSR member of a two-member lab group, `dfsrtest.com\testnamespace\dfsfolder` (RF `DFSFolder`, `C:\Data`), plus SYSVOL.

| Folder | Files | Lines | Notes |
|---|---|---|---|
| `dfsrDC01` | Index 1 (sev 4, 154k), 2 (sev 5, 251), 3 (sev 5, 2.1k), **4–6 (sev 5, 200k each: rolled)**, 7 (sev 5, 70k, active) | ~827k | DB GUID `{2314971A…}` |
| `dfsrMember01` | Index 1 (sev 4, 99k), **2 (sev 4, 200k: rolled)**, 3 (sev 4, 189k, active) | ~488k | DB GUID `{BFF1148F…}` |

Scenarios found, 2026-11-11 local time (UTC+11). Each is visible from both sides:

| Time | Scenario | Upstream signature | Downstream signature |
|---|---|---|---|
| 12:51–12:53 | Bulk add, 1000 files on DC01 | `UsnConsumer::CreateNewRecord` (UID == GVSN), `ContentSetManager::UpdateHash`, `StageWriter::CompleteDownloadStage`, `Join::SubmitUpdate Sent`, `OutConnection::OpenFile` | `ReceiveUpdates`, `Meet::Install`, `LogTransferActivity Received RAWGET` ×1000, `InstallRename … completed`, `CommitSession Connection in sync` |
| 12:53:23 | Modify on Member01 (`file_0005.txt`) | `UsnConsumer::UpdateIdRecord`, `Reason: Close Data Overwrite`, GVSN `{BFF1…}-v10` on UID `{2314…}-v2038` | DC01: `LocalDominates Remote version dominates`, `RDCGET`, `MoveOut` / `Purge existing file in Deleted` / `RenameDelete` |
| 12:53:32 | Modify on DC01 (`file_0016.txt`) | as above, reversed | Member01: `RDCGET`, move-out sequence |
| 12:55:38 | Collision: `file_1001.txt` created on both within the same second | Both: `CreateNewRecord` with different UIDs | Member01 loses: `LocalDominates Conflicting`, **event 4412 (10 parameters)**, `MoveOut … Moving to conflict/deleted`, `InstallTombstone` for its own UID. DC01 generates that tombstone in `Join::SubmitUpdate` (`present 0`) and logs `Local version dominates` |
| 12:56:34 | Rename on DC01 (`file_1001.txt` → `oldfile.txt`) | `UpdateIdRecord`, `Reason: Close Rename New Name` | Member01: `Meet::InstallMove -> DONE Install-move completed` |
| 12:58:57 | DC01 severity set to 5 | New file started without a service exit, then a service restart | — |
| 13:00:27 | Add on Member01 (`file_1002.txt`), received by DC01 at sev 5 | `CreateNewRecord`, `Join::SubmitUpdate Sent` | DC01 at sev 5: `Rdc::SeedFile::UseSimilar (SimMatches=16)`, `Staging "SimilarityRelated"`, `StreamToIndex`, `XpressRdcNeedAssembler`, `Rdc Need Assembler Statistics`, `RDCGET` — a new file sent by RDC using a *similar* file as seed |
| 13:09–13:10 | Both services restarted (DC01 still sev 5) | Transient `[ERROR]`/`[WARN]` from `DownstreamTransport::EstablishConnection`, `InConnection::ReConnectAsync`, `AsyncRpcHandler::ProcessReceive` while the partner is down | — |
| 13:12:44 | **Bulk delete, 1002 files on Member01** (the original `file_0001…1000`, `file_1002`, `oldfile`) | `UsnConsumer::TombstoneOrDelete LDB Updating ID Record` + `ID record tombstoned from USN_RECORD` (`Reason: Close File Delete`, `present 0`), `Join::SubmitUpdate Sent`. No staging, no file served | DC01: `ReceiveUpdates`, `Meet::Install`, `ProcessUid Uid related found`, `Remote version dominates`, `InstallTombstone Checking whether deleted resource should be re-animated` → `Moving out existing content` → `MoveOut … Moving to conflict/deleted` → `-> DONE Install Tombstone complete`, `FrsSimilarityManager::Delete` |
| 13:13–13:15 | **Bulk add, 2000 files (~2.1 MB each, max 4 MB) on Member01, reusing the deleted names** | `CreateNewRecord` with **new UIDs** `{BFF1…}`, `UpdateHash`, staging, `Join::SubmitUpdate Sent`, `OpenFile Sent file` | DC01 at sev 5: `Uid related not found`, `RdcCache::Find` / `MatchSuffix … ConflictAndDeleted` (seed search), `RAWGET` ×2000, `TransferToInstalling Read N bytes`, `InstallRename -> DONE` |
| 13:13–13:15 | **Staging quota exceeded** on both (4096 MB quota vs ~4.2 GB of new data) | Event **4202** (above high watermark; 12 params incl. csId, root, staging path, quota MB, usage MB, high %, low %, RF name) then **4204** (cleanup done), three cycles | Same |

A delete followed by a re-create under the same name gives two unrelated UIDs, a live example of "same name ≠ same file" (§6 of the reference). File trace must key on UID and offer name → UID disambiguation.

### Format facts

| Finding | Consequence for the design |
|---|---|
| Plain ASCII; **mixed CRLF and LF** endings; occasional blank lines | Reader accepts both endings and skips blanks |
| Header: `* DFSR Log Sequence:S Index:N Computer:X TimeZone:<name> (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[…]` | Use the **TzId** with `TimeZoneInfo` for DST-correct UTC conversion. `Build` is a fixed placeholder (`Jan 01 2016`) and does not identify the OS |
| Second header: `logLevel:N maxEntryCount:200000 maxFileCount:1000 logPath:…` | Default file count on modern OS is **1000**. Log level is recorded per file, because sev-5 categories only exist where it is 5 |
| **Index** increments for every new file. **Sequence** resets to 1 at service start and increments on each roll within one service run. Confirmed by real `maxEntryCount` rolls: DC01 Index 4→5→6→7 = Sequence 1→2→3→4; Member01 Index 2→3 = Sequence 1→2. The finished file is gzipped as `DfsrNNNNN.log.gz` | Sequence > 1 means "continues Index−1 without a restart". Fingerprint by computer + Index + first line |
| **`maxEntryCount` counts lines**, not entries: rolled files hold 199,997–199,998 dated + nested lines (plus header and blanks) | Expect ~200k lines per rolled file |
| **A roll can split an entry**: the next file can open with orphan `+` lines belonging to the last dated line of the previous file (DC01 Index 5 and 6 both start this way) | Parser stitches them back (§3) |
| Line cost per file: sending side ~150 lines per add, ~78 per delete (sev 4). Receiving side ~98 per add (sev 4); at sev 5 **~245 per full-copy add, ~180 per delete**, ~780 for an RDC add | DC01 at sev 5 wrote ~230k lines/min during the bulk add, a roll every ~50 s. A busy server's 1000 files may cover only hours |
| The `Computer:` name in the header can differ from the collected server (a rename before DC promotion was seen earlier) | Server identity = the server collected from (or chosen on upload); header names are kept as aliases |
| Date-time line: `20261111 11:47:05.743 3988 USNC  2871 [ERROR] Class::Method …` with **variable padding**; severity tag optional, between source line and `Class::Method` | Whitespace-tolerant regex |
| 1–3 ms back-steps between threads are normal | Order events by (file Index, line number); bucket by timestamp |
| `+<TAB>` nested lines are **~85% of all lines** and carry key fields (`name`, `gvsn`, `uid`, `present`, `attributes`, USN `Reason`, `FileName`) | Extraction must parse nested key/value records; nested text is stored compressed, outside the hot `event` table |
| Nested record times are UTC (`clock … GMT`): local 11:47:05.743 = `00:47:05.743 GMT` | Built-in self-check of the time-zone conversion |
| Error stacks: `+ [Error:9551(0x254f) Class::Method file.cpp:5537 684 C <text>]`, outermost first | Extract first and innermost code plus source method |
| Config dumps are ordinary dated `CPAR` lines (`Config::ParamBlock::Print [[ DfsrConnection ]]`, `Config::GuidParam::Print ConnectionGuid: …`) | Resolver walks consecutive CPAR lines on one thread |
| `HIST StateHistory::Update` (sev 5) dumps `contentSetName`, `replicationGroupName`, `memberGuid`, `memberName` | A name source in sev-5 logs |
| Inline names are common: `csName:DFSFolder`, `rgName:…`, `updateName:`, `fileName:`, `name:` | Most dimensions can be filled from the event line itself |
| **`Meet::LocalDominates Remote version dominates` is logged at severity 4** on modern OS (261 on Member01), contrary to the 2009 reference | Treat as a routine check; only `Conflicting` + 4412 mark a conflict |
| Event 4412 has 10 parameters (parameter 10 = member GUID); event IDs seen: 1006, 1008, 1206, 1210, 1314, 4202, 4204, 4412, 5004, 5014, 6102 | Event-ID table extended beyond the reference |
| Modules not in the reference: `MAIN`, `FSVC`, `SSYN`, `VDSN`, `CLUS`, `IINC`, `CRED`, `SCFS`, `W2CH`, `RPCN`, `TASK`, `UPDI` | `Other (<Module>)` fallback; extend the module table |
| High-volume, low-value lines: `AsyncUnbufferedFileReader/Writer::Close` statistics, credit manager, `Task::*`, VV polling | Own categories in a "Plumbing" group, hidden by default |
| `Meet::MoveOut` is shared by update displacement (`Purge existing file in Deleted`), conflict loss (with 4412) and remote delete (inside `InstallTombstone`) | Categorise a move-out from its sibling events for the same UID/GVSN (correlation pass, §3), not from the line alone |
| DB GUID → server: a GVSN from a local `UsnConsumer::*` change carries the writing server's DB GUID (`{2314…}` = DC01, `{BFF1…}` = Member01) | Confirms the origin-inference approach (§5) |


---

## 2. Architecture

```
DfsrAnalyser.slnx
global.json                  pins SDK 10.0.1xx; opts dotnet test into Microsoft.Testing.Platform
Directory.Build.props        net10.0, nullable, implicit usings
src/
  DfsrAnalyser.Core          parsing, categorisation, name resolution, case DB access (no UI)
  DfsrAnalyser.Collection    AD discovery, SMB enumeration and copy, incremental tracking   (phase 5)
  DfsrAnalyser.App           WPF app (`DfsrAnalyser.exe`): MVVM, ScottPlot chart, tree grid
  DfsrAnalyser.Cli           `dfsra` console harness: import / stats / files / bench
tests/
  DfsrAnalyser.Tests         xUnit v3: parser, extractor, importer tests on lab log fragments
src/DfsrAnalyser.Core/Taxonomy/default-rules.json   embedded default taxonomy (copied to each case as rules.json)
```

- **DuckDB** (DuckDB.NET) as the case store. Columnar storage handles `GROUP BY time_bucket, category, dimension` over tens of millions of rows interactively, and compresses repetitive log text well.
- **Charting:** ScottPlot 5 (WPF), chosen for its performance with many bars and series.
- **Grid:** a virtualised hierarchical grid (TreeListView pattern over a WPF `DataGrid`/`ListView` with lazy child loading).
- Parsing runs in the background with progress and cancellation. Bulk inserts go through CSV + DuckDB `COPY` (see below); the DuckDB.NET row appender is kept only for single-event rewrites.

### Import pipeline (built in phase 1)

1. **Probe** each file (header + first dated line) and sort per server by Index.
2. **Plan** in order: match the fingerprint against the case; register new files; trim a partially imported active log back to its last event.
3. **Parse** in parallel (at most N files ahead, N = cores − 1, capped at 8): read, extract fields, write a pair of temp CSVs (`event`, `event_nested`) under `<case>\tmp`.
4. **Commit** in order: `COPY` both CSVs, stitch roll orphans, update `log_file`, mark earlier Indexes complete.

**Event ids are derived**: `(file_id << 32) | line_no`. Workers need no shared counter, and a resumed or re-stitched event keeps its id.

### Measured performance (samples: 10 files, 1.32M lines, 442k events; 12 logical CPUs)

| Stage | Throughput |
|---|---|
| Read + decompress lines | ~4.2M lines/s |
| + parse dated/nested structure | ~2.5M lines/s |
| + field extraction (span scanners; regex version was 20× slower) | ~1.4M lines/s |
| + CSV formatting | ~0.7M lines/s (single thread) |
| DuckDB `COPY` into the on-disk case | ~230k event rows/s; detail-heavy files cost more |
| **Full import** | **~200–240k lines/s** (≈6 s for the samples; 62 MB case) |

The DuckDB.NET row appender managed ~175 ns per value (≈3 s for the event rows alone), and checkpoint/transaction settings made no difference to `COPY`. The full import is therefore within ~1.5× of DuckDB's own load rate on this machine. Projection: ~15 min for a server's full 1000 × 200k-line history, seconds for a typical time-range collection. Remaining levers, if needed: the "drop Plumbing detail" import option (phase 2) and storing nested text compressed as a BLOB.

### Case folder

```
<CaseName>\
  case.duckdb
  rules.json         the case's rule set (written from the default on first open; editable)
  case.json          name, created, collection history, display preferences   (phase 3/5)
  tmp\               transient CSVs during import
```

The original log files are not kept. Everything needed for drill-down, including nested `+` lines, lives in the database.

---

## 3. Parsing

Streaming, line by line, with `GZipStream` for `.gz` files. Line kinds (§2):

| Kind | Handling |
|---|---|
| `*` header | Per-file metadata: computer, TzId (and display name/offset), log level, maxEntryCount, sequence/index |
| Date-time line | New event: timestamp, thread, module, source line, optional `[ERROR]`/`[WARN]`, `Class::Method`, message |
| `+` line | Attached to the preceding event as nested detail; key/value records parsed for field extraction |
| CPAR config dump lines (`[[ DfsrConnection ]]` etc.) | Feed the name-resolution tables |
| Blank line | Ignored |

Timestamps: local → UTC with the file's TzId (DST-aware). Events keep (file Index, line number) for stable ordering.

**Roll continuity:** a file with Sequence > 1 continues Index−1. Leading `+` lines (before its first dated line) belong to the last event of Index−1 and are appended to it. If Index−1 isn't in the case yet (outside the range, or not collected), they are held as a placeholder and attached when it arrives.

**Correlation pass:** after a file is parsed, events are grouped by (server, UID, GVSN) within a short window. Context-dependent categories (move-out as delete, conflict or update; install outcome) are set from the group.

### Field extraction (per event, into columns)

Extracted from the message and nested lines with hand-written span scanners (`FieldExtractor`):

`connId`, `csId`, `csName`, `replicaSetId`, `rgName`, `uid`, `gvsn`, `parent`, `fid`, file `name`/`fileName` → extension, `attributes` → is-folder/temporary, `present`, `meetInstallUpdate`, `Error:N` (first and innermost of the stack), `eventId:N`, transfer type (RAWGET/RDCGET), `stagedSize`, `Retries:N`, `updateType`, `credits`, `updatesLeft`/`processed`/`failures`, USN `Reason`.

**Originating server** = DB GUID part of the GVSN (and of the UID for "created on").

### Tables (outline)

As built (`src/DfsrAnalyser.Core/Storage/Schema.cs`):

- `server(server_id, name)`, `server_alias(server_id, alias)` (header `Computer:` names)
- `log_file(file_id, server_id, file_name, source_path, size_bytes, header_computer, tz_id, tz_name, tz_basis, log_level, max_entry_count, max_file_count, sequence, idx, first_line_hash, first/last_ts_utc, line_count, event_count, orphan_lines, unrecognised, last_event_line, last_event_id, is_complete, imported_at)`
- `event(event_id, file_id, server_id, line_no, ts_utc, ts_local, thread, module, src_line, severity, class_method, message, nested_count, category, conn_id, cs_id, cs_name, replica_set_id, rg_name, uid_db/uid_ver, gvsn_db/gvsn_ver, parent_db/parent_ver, fid, file_name, extension, attributes, is_folder, present, meet_install, usn_reason, error_code, error_code_inner, event_log_id, transfer_type, staged_size, retries, update_type)`. GUIDs are `UUID`; no primary key or indexes (zone maps serve the scans)
- `event_nested(event_id, nested_text)`: one row per event, lines joined by `\n`, read only on drill-down
- `pending_orphan(server_id, idx, nested_text)`: roll orphans waiting for Index−1
- `guid_name(guid, kind, name, source)`, kinds RG / RF / connection / DB GUID / member
- `connection(conn_id, rg, sending_member, receiving_member)`

### Incremental import

Each file is fingerprinted by server + header Index + hash of the first event line. (Confirmed: a new Index starts at each service start or roll, and the previous file is gzipped under its own `DfsrNNNNN.log.gz` name.) On re-import:

- known, complete files are skipped;
- the active `.log` (or the same Index now as `.gz`) is re-read from its last imported line number;
- the `.log`→`.gz` change is recognised by fingerprint, so nothing is duplicated.

Index wrap-around after `maxFileCount` files is handled by also comparing the first-line hash.

---

## 4. Event taxonomy (rules file) — built in phase 2

`rules.json` (JSON with comments) has two lists:

- **`categories`**: `name`, `group`, `hidden` (off by default in views), `note` (shown in event detail; carries the reference's "misleading entry" warnings).
- **`rules`**, evaluated in order, first match wins. Conditions (all optional, all must hold): `module`, `classMethod` (exact, list, or `*` wildcard), `message` (RE2 regex), `severity`, `usnReason` (regex), `transferType`, `eventLogId`, `errorCode` (outer or inner), `innerErrorCode` (innermost only, looks through wrappers like 9027). Unmatched events become `Other (<MODULE>)`.

**Engine:** the rules compile to one DuckDB `CASE` expression (`RuleSql`). Rows are categorised as they load (`INSERT … SELECT * REPLACE (<case> AS category) FROM read_csv(…)`), and re-applying is a single `UPDATE` (0.5 s for 442k events). Regexes are validated against DuckDB's RE2 before a rule set is accepted, with the failing rule number reported.

**Counting convention:** each file change produces one *visible* entry: the line carrying the USN reason (local create/modify/rename/delete), `Join::SubmitUpdate Sent`, `ReceiveUpdates Received`, the RAWGET/RDCGET line, the `-> DONE` install line. Companion lines (LDB updates, install steps, dominance checks) sit in hidden categories so rates are not double-counted.

**Groups (92 categories, 101 rules in the default set):** Local change · Outbound · Inbound · Install · Conflict · RDC · Staging · Session · Initial sync · Problems · Service & config · Throttling · Plumbing (hidden; nested detail optional).

**Problems first:** error-code rules run before routine ones (e.g. 9047 on `RdcGet` is *Redundant download (benign)*, not a failure). Errors DFSR itself tags `(Ignored)`, and routine start/stop noise (event source registration 1717, SYSVOL migration keys), go to a hidden *Ignored by DFSR (benign)* category. Codes learned from the samples: 1723 / 1753 (RPC, partner restarting), 9028 *content set not found* (partner not ready), 1355 (no domain).

**Correlation pass** (after each file load, and after every re-apply): a `Meet::MoveOut … Moving to conflict/deleted` line becomes *conflict loser* when a conflict for the same file is logged on the same server within 5 s, otherwise *remote delete* when it sits inside `Meet::InstallTombstone` on the same thread.

**Result on the samples:** every event lands in a defined category (none in `Other`). Visible counts reproduce the test scenarios: 1,002 local deletes on Member01 ↔ 1,002 *remote delete* move-outs on DC01; one conflict loser; one modify and one rename per side; 3 × 4202/4204; RPC failures only around the two service restarts.

**Drop Plumbing detail** (`--drop-plumbing-detail` / `Importer.DropPlumbingDetail`): nested text of Plumbing events is not stored; `log_file.nested_dropped` records it. On the samples: 71 → 60 MB (−15%); the bulk of nested text is ID records, which are kept. Categorising during load costs ~15% of import time.

CLI: `dfsra rules --case <c> apply [file]`, `dfsra rules export-default <path>`, `dfsra stats --case <c> [--all]` (category matrix per server).

---|---|
| Local change | Created, Modified, Deleted (tombstoned), Renamed, Filtered, Skipped temporary, USN-only update, UID tunnelling |
| Outbound | Update offered (`Join::SubmitUpdate Sent`), File served (`OutConnection::OpenFile`), Staged |
| Inbound | Update received, Install started, Install retry (`Retries>0`), RAWGET, RDCGET, Transfer failed |
| Install | Install-rename completed, Install-move completed, Tombstone installed (remote delete), Move-out (delete / conflict / update, from correlation) |
| Conflict | Conflict check (`LocalDominates Conflicting`), Conflict event 4412 |
| Session | Connection established, Session committed / in sync, No pending updates, VV request/receive |
| Initial sync | State change, 4102, 4104, 4112, Pre-existing move-out |
| Problems | Sharing violation (32/4304), RPC failure (1722 etc.), LDAP/AD failure, Access denied at install, Redundant download (9047, benign) |
| Config / service | Service start, DC bind (1206), AD poll, Config dump |
| Staging | High watermark (4202), Cleanup complete (4204), Staged, Staging cleanup task |
| Throttling | Bandwidth wait |
| Plumbing (hidden by default) | Async I/O statistics, credit manager, `Task::*`, VV polling, update-worker lifecycle |

Re-applying rules is a single `UPDATE event SET category = …` driven by the rules, so no re-parse is needed. Rules carry the reference's "misleading entry" notes (§11), which are shown in event detail.

---

## 5. Name resolution (from logs) — built in phase 4

`Core/Names/NameResolver` rebuilds small name tables after every import (≈0.1 s on the samples; `dfsra names` runs it on demand and prints the result):

| Table | Source |
|---|---|
| `name_rg` (RG GUID → name) | `[[ DfsrReplicationGroup ]]` dumps; inline `replicaSetId`/`rgName` fills gaps |
| `name_rf` (RF GUID → name, RG) | `[[ DfsrReplicatedFolder ]]` dumps; inline `csId`/`csName` fills gaps |
| `name_member` (member GUID → server) | `[[ DfsrMember ]]` and each connection's `PartnerGuid`/`PartnerName` (one member object per RG, so a server has several) |
| `name_conn` (connection, **per logging server**) | `[[ DfsrConnection ]]`: partner, `Inbound`, enabled, RDC, RG. The same GUID is *inbound* on one side and *outbound* on the other |
| `name_db` (DB GUID → server) | **Inferred**: GVSN DB GUID of local database writes (`UsnConsumer::CreateNewRecord/UpdateIdRecord/TombstoneOrDelete`, `LDB` lines). `UpdateUsnOnly` is excluded (it keeps a possibly remote GVSN). Evidence count and an ambiguity flag are kept |
| `rf_config` (per server) | RF dump: root path, staging and conflict quotas, primary, read-only, enabled, file filter |

The dump parser walks `CPAR` lines per (file, thread) in line order; later dumps override earlier ones.

**Dimensions using names**: *Origin server* (GVSN), *Created on server* (UID), *Partner*, *Connection* ("from X" / "to Y"), *Direction*, *Replicated folder* and *Replication group* (resolved even on lines that carry only GUIDs). GUIDs no log explains show as `unknown {2314971A…}`, e.g. a partner whose logs were not collected. Each dimension declares the name tables it needs and queries join only those. **Event detail** shows names beside GUIDs ("from DFSRDC01", "last changed on DFSRMEMBER01", "created on …", RF/RG names).

**Samples**: 2 RGs (including SYSVOL as *Domain System Volume*), 2 RFs, 4 directional connection views, both DB GUIDs mapped with no ambiguity, RF config showing the 4096 MB staging quota behind the 4202 events. Stacking the chart by origin server shows the first bulk add originating on DFSRDC01 and the delete/re-add on DFSRMEMBER01.

---

## 6. Remote collection

1. **Discover:** LDAP (`System.DirectoryServices.Protocols`) under `CN=DFSR-GlobalSettings,CN=System,<domain DN>` lists replication groups. The analyst picks RGs and members, resolved through `msDFSR-ComputerReference` → `dNSHostName`. SYSVOL ("Domain System Volume") is included: its members are the DCs, whose subscription objects sit under each DC's computer object. Manual server entry is also allowed.
2. **Locate:** default `\\<server>\admin$\debug\`, with a per-server path override for custom `debuglogfilepath` settings.
3. **Select by time range:** list `Dfsr*.log*` and read each file's header and first event line (cheap for `.gz`: decompress only the start). A file's span runs from its first event to the next Index's first event. Copy only files that overlap the range. Last-write times are only a fallback.
4. **Copy and import:** parallel across servers (bounded), straight into the parser. Copies go to a temp folder and are deleted afterwards. Progress is shown per server, and failures are reported per server without aborting the run.
5. **Credentials:** current user; optional alternate credentials applied per run via `WNetAddConnection2` and disconnected afterwards.
6. **Top-up:** re-run the same collection profile (saved in `case.json`) to fetch only new or grown files.
7. **Event log (optional switch, off by default):** read the remote "DFS Replication" event log for the same time range using `EventLogReader` with an `EventLogSession` to the server (RPC, same credentials). Upload also accepts exported `.evtx` files. Rows go into an `event_log` table (time UTC, event ID, level, message, parameters) and appear on the timeline as source `EventLog`, with categories keyed on event ID. They can be filtered alongside or apart from debug-log events. Debug-log `EventLog::Report eventId:N` lines link to the matching event-log entry where both exist.

---

## 7. User interface

```
+------------------+-------------------------------------------------+
| Facet pane       |  Toolbar: time range | window [5m v] | UTC/local |
|                  |  | stack by [Category v] | Export               |
|  Category    (n) +-------------------------------------------------+
|  Server      (n) |  Stacked bar / line chart of counts per window  |
|  Partner     (n) |  (click or drag on bars → sets time range)      |
|  RG / RF     (n) +-------------------------------------------------+
|  Origin      (n) |  Grid: rows = Category ▸ chosen 2nd dimension   |
|  Extension   (n) |        columns = time buckets + Total + rate/min |
|  Severity    (n) |  Expand a row → next dimension → events (paged) |
|  Error code  (n) |  Tabs: [Pivot] [Top-N] [File trace]             |
|  File name  [  ] +-------------------------------------------------+
|                  |  Event detail (when an event is selected)       |
+------------------+-------------------------------------------------+
```

- **Facets:** each shows values with counts under the current filters. Multi-select, include/exclude, search box for high-cardinality facets (file name, folder).
- **Aggregation window:** presets (1s, 10s, 1m, 5m, 15m, 1h, 1d) plus custom. A sensible default is picked so the visible range shows about 100–300 buckets.
- **Stack by / group by:** any dimension (category, server, origin, extension…). The grid's row hierarchy is configurable, e.g. Category → Server → File.
- **Rates:** totals, events per minute, and peak bucket for each row.
- **Drill-down:** events load only when a leaf row or bucket is expanded, paged at 500. Event detail shows the raw line plus nested lines, decoded fields with resolved names, the rule's note, and "Trace this file".
- **Top-N:** busiest files, extensions, folders, originating servers and partners for the current filter, with sparklines.
- **File trace:** given a UID (or a file name → pick a UID), lists all events for that UID across all servers in UTC order, grouped into lifecycle stages (§7 of the reference), with stage-to-stage latency.
- **Export:** CSV of the current aggregate grid, Top-N table, or drilled-down events.

---

### As built (phase 3)

- **Query layer** (`Core/Query`, unit-tested): `EventFilter` (immutable: time range, include-sets per dimension, show hidden, message text) and `CaseQuery`, which runs facets (each ignoring its own selection, selected values first), the stacked time series (top 8 + Other, zero-filled), grid rows per level (count, first/last, peak window with ties resolved to the earliest, 40-bin trend), paged events and event detail. Values are always SQL parameters.
- **16 dimensions**: group, category, server, severity, RF, RG, file name (searchable), extension, file/folder, innermost error code, event ID, transfer type, origin DB GUID (names in phase 4), connection, module, Class::Method (searchable).
- **App**: one `CaseSession` serialises all DuckDB work off the UI thread. Each filter change bumps a version and stale results are dropped; changes are debounced 150 ms. Collapsed facets are not queried.
- **Chart**: click a bar to select its window, drag to select a range, right-click or Alt+Backspace to zoom out (history kept), "All time" to reset. Colours are stable per value for the session and match the grid swatches.
- **Grid**: flattened virtualised tree (ListView + GridView). Levels are chosen from any dimension (default Category ▸ Server ▸ File name ▸ events). Children load on expand; events page 500 at a time.
- **Detail**: UTC/local and server-local time, log file and line, category, group and note, decoded fields (GUIDs braced, FID/attributes in hex), full nested detail (or a note that Plumbing detail was dropped).
- **Case and import**: File ▸ New/Open/Recent case, Import files/folder with progress and cancel; Rules ▸ Edit rules file / Re-apply rules; View ▸ Show hidden categories / Local time (persisted in `%LOCALAPPDATA%\DfsrAnalyser\settings.json`).
- **Self-check without a person at the screen**: `DfsrAnalyser.exe --case <folder> [--import <path>] --screenshot <png>` opens (and optionally imports into) a case, expands the grid down to an event, then saves three PNGs: default view, zoomed + one-server filter, and stack-by-server / server-first grouping / hidden shown / message search. Used to verify every change to the UI in this phase.

---

## 8. Phases

| # | Phase | Outcome |
|---|---|---|
| 0 | Samples and spike | **Done** (§1a): both sides of add, modify, collision, rename, bulk delete, name reuse, staging quota, an RDC receive at sev 5, and real `maxEntryCount` rolls on both servers |
| 1 ✅ | Core parser and case DB | Upload → parse → DuckDB, with field extraction from lines and nested records, TzId-based UTC, roll stitching and incremental fingerprints (correlation moved to phase 2, since it sets categories). **Done 2026-11-11:** 40 tests pass; CLI totals match grep exactly (dated lines, nested lines, [ERROR]/[WARN], sends/receives) and every §1a signature count. Proven by unit tests on fragments cut from the samples and by the CLI harness run over `samples/` (counts match the grep figures in §1a; parse speed and DB size recorded) |
| 2 ✅ | Taxonomy | Default rules file, apply/re-apply, `Other (<Module>)` fallback, rule notes, correlation pass (move-out as delete/conflict/update), "drop Plumbing detail" import option |
| 3 ✅ | Main UI | Case open/create, upload, facet pane, chart, aggregate grid, lazy drill-down, event detail |
| 4 ✅ | Name resolution | Config-dump parsing, DB GUID inference, origin/partner/RG/RF dimensions |
| 5 | Remote collection | AD discovery (incl. SYSVOL/DCs), SMB copy, time-range selection, alternate credentials, collection profiles and top-up, optional event-log collection and `.evtx` upload |
| 6 | Analysis extras | Top-N, file trace, CSV export |
| 7 | Packaging | Self-contained per-user installer, app icon, README |

---

## 9. Open questions

None blocking. The samples cover everything phases 1 and 2 need. Not yet seen, to add when real-world logs turn up: filtered/temporary files, sharing violations (4304), RPC/LDAP outages, initial sync (4102/4104/4112) and bandwidth throttling.
