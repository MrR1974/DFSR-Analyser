# DFSR Debug Log Reference

A distilled guide to reading Distributed File System Replication (DFSR) debug logs, consolidated from the 21-part "Understanding DFSR debug logging" series (AskDS blog, Ned Pyle, 2009). Samples in the series came from Windows Server 2003 R2 and Windows Server 2008; the series states the logs do not change meaningfully in later Windows Server releases.

GUIDs in examples below are abbreviated (for example `{5CB1…}-v18`).

---

## 1. Controlling debug logging

Logs are written as circular files to `%windir%\debug` (files named `Dfsr#####.log`) and older logs are compressed to `.gz`. With defaults, total space stays around 50 MB.

| Setting | Default | Range | WMIC property (`dfsrmachineconfig`) |
|---|---|---|---|
| Debug log severity | 4 | 1–5 | `debuglogseverity` |
| Max messages per log file | 200000 | 1000 – 4294967295 | `maxdebuglogmessages` |
| Number of log files kept | 100 | 1 – 10000 | `maxdebuglogfiles` |
| Log path | `%windir%\debug` | any existing folder | `debuglogfilepath` |
| Logging enabled | TRUE | TRUE/FALSE | `enabledebuglog` |

Syntax pattern:

```
wmic /namespace:\\root\microsoftdfs path dfsrmachineconfig set debuglogseverity=5
```

Notes:

- A custom log path must already exist. If it doesn't, the service falls back to `%windir%\debug` at restart.
- These commands write the `<DfsrDebug>` section of `DfsrMachineConfig.XML` in `%systemdrive%\System Volume Information\DFSR\Config`. When everything is at default, those tags are absent.

### Severity levels

| Level | Name | Tag in log | What gets written |
|---|---|---|---|
| 0 | LogLevelNone | – | Nothing |
| 1 | LogLevelAlways | – | Header only |
| 2 | LogLevelError | `[ERROR]` | Errors |
| 3 | LogLevelWarn | `[WARN]` | Warnings + errors |
| 4 | LogLevelInfo | – | Informational + above (**default**) |
| 5 | LogLevelTrace | – | Tracing + everything above |

Severity 4 captures everything except trace detail. Use 5 only for targeted troubleshooting (RDC internals, conflict decisions, RPC extended errors, throttling waits, staging lock states); it produces a lot of chaff.

---

## 2. Anatomy of a log file

Every log contains three kinds of lines.

### 2.1 Header lines (start with `*`)

Always written, cannot be disabled except by disabling logging.

| Field | Meaning |
|---|---|
| FRS Log Sequence / Index | Position in the circular log rotation |
| Computer | Server that wrote the log |
| TimeZone | Server's local time zone and GMT offset |
| Build | OS build; `Enterprise=1` indicates Enterprise edition or higher |
| Configuration logLevel | Current severity |
| maxEntryCount | Lines per file before rolling to a new file |
| maxFileCount | Number of circular files retained |
| logPath | Output folder |

### 2.2 Single-line messages (start with a date-time)

Each maps to one discrete DFSR operation.

```
20080111 15:12:30.996 3876 JOIN 1171 Join::SubmitUpdate Sent: uid:… gvsn:… name:USRSTAT.EXE connId:… csId:… csName:…
```

| Position | Field | Notes |
|---|---|---|
| 1–2 | Date-time | `YYYYMMDD HH:MM:SS.mmm`, **server local time**, not UTC |
| 3 | Thread ID | Thread inside DFSR.EXE |
| 4 | Module ID | DFSR sub-component (see §5) |
| 5 | Source line | Line number in DFSR source |
| 6 | Class::Method | Code being executed |
| 7+ | Data | Free-form details for the operation |

### 2.3 Nested messages (start with `+`)

A single-line message that ends in a colon is followed by `+` lines carrying multi-line detail (ID records, USN records, statistics, error stacks, configuration dumps).

---

## 3. Identifiers and how to decode them

### 3.1 GUID fields

| Log field | Object |
|---|---|
| `replicaSetId` | Replication Group |
| `csId` (content set) | Replicated Folder |
| `connId` | Connection object |
| `parent` | Containing folder (the RF root is `{csId}-v1`) |
| `uid` | Original identity of a file/folder: `{DB GUID of creating server}-v<version>` |
| `gvsn` | Current version: `{DB GUID of server that last changed it}-v<version>` |
| `csName` / `rgName` | Friendly names, when the log includes them |

The GUID portion of a UID/GVSN is the **DFSR database GUID** of a server (one per volume), not the server's computer GUID. The version number is that server's version counter at the time of the change; it increments per change on the server, not per file, so a file's versions are not consecutive.

### 3.2 Mapping GUIDs to names

```
dfsradmin rg list /attr:rgname,rgguid                          # replicaSetId → RG name
dfsradmin rf list /rgname:<RG> /attr:rfname,rfguid             # csId → RF name
dfsradmin conn list /rgname:<RG> /attr:sendmem,recvmem,connguid # connId → sending/receiving member
dfsrdiag guid2name /guid:<GUID> /rgname:<RG>                    # DB GUID → server + volume
```

### 3.3 Reading UID vs GVSN

| Pattern | Interpretation |
|---|---|
| UID == GVSN | File/folder created on that server and never changed since (in DFSR terms, copying a file into an RF is a "create") |
| Same GUID, GVSN version > UID version | Created and later modified on the same server |
| Different GUIDs | Created on one server, last changed on another |
| GVSN increments with `present 0` | Deletion — a delete is a version change too |
| UID never changes | Track a file across its whole life by UID, not by name |

### 3.4 File ID (FID) cross-referencing

`fid` in database records equals `FileRefNumber` in USN records on the same server. Use it to tie database updates to USN journal entries, and especially to follow files that never get a UID (filtered temporaries). FIDs are server-local: the downstream copy has a different FID.

### 3.5 Version vectors

```
vvUp:{EDE2…} |-> { 10..40 }      ← versions from that DB GUID known to the upstream
vvDown:{EDE2…} |-> { 10..39 }    ← versions the downstream already has
delta::{EDE2…} |-> { 40 }        ← versions the downstream will request
```

The downstream compares the upstream's vector with its own and requests the difference. Multiple `+` lines appear when changes originated on several servers.

---

## 4. Nested record field reference

### 4.1 ID record (database) fields

Fields marked * appear only on Windows Server 2008 and later.

| Field | Meaning / how to use it |
|---|---|
| `fid` | NTFS file ID; matches `FileRefNumber` in USN records |
| `usn` | USN of the last journal record processed for this file |
| `uidVisible` | 0 = still being processed locally, not yet offered for replication; 1 = replicable |
| `filtered` | 1 = excluded by an administrator filter |
| `journalWrapped` | 1 = in USN journal-wrap recovery |
| `slowRecoverCheck` | 1 = being re-checked after journal-wrap recovery |
| `pendingTombstone` | 1 = being tombstoned |
| `internalUpdate`*, `dirtyShutdownMismatch`* | Internal state flags |
| `meetInstallUpdate`* | 1 = record changed because the file **arrived through inbound replication** (not a local change) |
| `meetReanimated`* | 1 = file reanimated (e.g. restored) |
| `recUpdateTime` | Time of local change; meaningful only on the originating server (`16010101…` = unset) |
| `present` | 1 = live in the RF; 0 = deleted/tombstoned |
| `nameConflict` | 1 = name conflict was involved |
| `attributes` | File attributes (`GetFileAttributes`): `0x10` folder, `0x20` file, `0x2` hidden, `0x100` temporary. Trust this, not the name/extension |
| `ghostedHeader`*, `data`*, `clockDecrementedInDirtyShutdown`* | Reserved / unshipped features |
| `gvsn` | Current version (see §3.3) |
| `uid` | Original identity |
| `parent` | Parent folder UID |
| `fence` | Fence timestamp. Primary-member records show `P`; records on a non-authoritative member during initial sync show `(I)` |
| `clock` | Time of last change on this server |
| `createTime` | When the object came into this RF |
| `csId` | Replicated Folder GUID |
| `hash` | SHA-1 of the marshaled file (data, alternate streams, security). Zeros = not yet calculated or discarded after a change |
| `similarity` | RDC similarity checksum. Zeros = never staged with RDC (small files, folders) |
| `name` | File/folder name |

Hash and similarity are reset to zeros when a file changes and are filled back in when it is restaged (`ContentSetManager::UpdateHash`).

### 4.2 USN_RECORD fields

| Field | Meaning |
|---|---|
| RecordLength | Bytes used by the record |
| MajorVersion / MinorVersion | Journal record version (2 / 0) |
| FileRefNumber | FID (cross-reference with `fid`) |
| ParentFileRefNumber | FID of the parent folder |
| USN | Journal sequence number |
| TimeStamp | Local time of the change |
| Reason | What happened (see below) |
| SecurityId, SourceInfo | Journal metadata |
| FileAttributes | Same values as `attributes` |
| FileName | Name at the time of the record |

Common `Reason` values seen in the series:

| Reason string contains | Meaning |
|---|---|
| `File Create` (often with `Data Extend`, `Data Overwrite`) | New file (including one copied in) |
| `Data Extend` / `Data Overwrite` / `Data truncation` + `Close` | Content modified |
| `Rename New Name` | Renamed (also how a newly created folder can appear) |
| `File Delete` | Deleted |
| `Security Change` | ACL change |
| `Basic Info Change` | Attributes/timestamps changed |
| `Object ID Change` | Object ID changed (often a close with no data change) |

### 4.3 Content set (replicated folder) record

Logged by `Ldb::Insert/Update … contentSetRecord`. Key fields: `contentSetId`, `memberId`, `state` (`InitialBuilding`, `InitialSync(Sync)`, `InitialSync(Cleanup)`, `Normal`), `startVersion`, `stageFid`, `isTombstone`, `readOnlySince`, `dbLossRecover`.

---

## 5. Module IDs

The original Part 2 module table is not present on the current published page. The list below is compiled from the module codes that appear in the series' sample logs; descriptions reflect the classes each code is attached to.

| Module | Area | Typical classes |
|---|---|---|
| USNC | USN journal consumer | `UsnConsumer::*` |
| DBCJ | Change journal reader | `ChangeJournal::Start` |
| LDBX | DFSR JET database | `Ldb::*`, `LdbManager::*` |
| JOIN | Offering updates to partners | `Join::SubmitUpdate` |
| SRTR | Server-side RPC handlers (upstream) | `SERVER_*`, `InitializeFileTransferAsyncState`, `RpcAsyncPipeState` |
| UPST | Upstream transport | `UpstreamTransport::*`, `UpdateBuffer::*` |
| DOWN | Downstream transport / throttling | `DownstreamTransport::*`, `AsyncRpcHandler`, `BandwidthThrottler` |
| INCO | Inbound connection (downstream logic) | `InConnection::*` |
| OUTC | Outbound connection (serving files) | `OutConnection::*` |
| MEET | Installing updates, conflicts, move-outs | `Meet::*` |
| UPMG | Update manager/worker | `UpdateWorker::ConsumeUpdates`, `UpdateManager` |
| STAG | Staging | `Staging::*`, `StageWriter::*`, `StagingCleanupTask` |
| MRSH | Marshalling file data/metadata | `Marshaller`, `MarshalContext` |
| ASYN | Async unbuffered I/O | `AsyncUnbufferedFileReader/Writer` |
| RDCX | Remote Differential Compression | `Rdc::*`, `StreamToIndex`, `SignatureGenerator` |
| RDCS | RDC similarity data | `FrsSimilarityManager` |
| RDCC | RDC cache lookups | `RdcCache::*` |
| XRNA | XPRESS RDC need assembler | `XpressRdcNeedAssembler` |
| CSMG | Content set (RF) manager | `ContentSetManager::*` |
| VLMG | Volume manager | `VolumeManager::*` |
| DIRW | Directory walker | `DirWalkerTask::*` |
| ISYN | Initial sync | `InitialSyncManager`, `InitialSyncCleanupTask` |
| RSMG | Replica set (RG) manager | `ReplicaSetManager::*` |
| FREP | Replicator core | `FrsReplicator::UpdateVolume` |
| CFAD | AD configuration (LDAP) | `Config::AdConnection`, `AdConfig`, `AdQuery`, `AdReader` |
| ADWR | AD writer (SYSVOL objects) | `Config::AdWriter` |
| SYSM | SYSVOL migration | `Migration::SysVolMigration` |
| CREG | Registry configuration | `Config::RegConfig/RegReader` |
| FCFG | File configuration | `Configuration::ReadVolumeConfig` |
| CXML | XML configuration files | `Config::XmlReader/XmlWriter` |
| CCTX | Configuration context | `ConfigContext`, `VolumeIdTable` |
| CPAR | Configuration parameter dump | `Config::*Param::Print` |
| SCFG | Service configuration / polling | `ServiceConfig::*` |
| EVNT | Event log writes | `EventLog::Report` |
| NTFS | File system operations | `NtfsFileSystem::*` |
| FIDL | File ID locking | `FidLockMan::Lock` |
| SETT | Settings defaults | `Settings::GenericDwordSetting` |
| HIST | Sync history / state | `StateHistory::Update` |

---

## 6. Reading conventions and caveats

- **Local time.** Timestamps are local to each server. Expect up to ~5 minutes of skew between partners in real environments.
- **Pull model.** Replication is pulled by the downstream. The upstream log usually looks "finished" before the downstream, because the downstream still has to stage, decompress, install, and update its database.
- **Thread ordering.** DFSR is multi-threaded, so entries can appear out of logical order (e.g. a database update logged before the USN record that caused it). Correlate by FID, UID/GVSN, and connId rather than strict line order.
- **Same name ≠ same file.** Many files can share a name; always confirm UID/GVSN (or FID locally).
- **Metadata-only changes** (rename, attributes, security, delete) never stage or transfer file data; there is no RAWGET/RDCGET for them.
- **Function names can mislead.** See §11.

---

## 7. The normal replication lifecycle

A large (>64 KB) new or changed file, end to end. Use this as the template against which to compare problem logs.

| # | Server | Log signature | Meaning |
|---|---|---|---|
| 1 | Up | `UsnConsumer::CreateNewRecord LDB Inserting ID Record` (new) or `UsnConsumer::UpdateIdRecord` / `Ldb::Update` (modified) | Local change written to the DB; `uidVisible 0` for a brand-new file |
| 2 | Up | `… ID record created/updated from USN_RECORD` | Matching USN record; check `Reason` |
| 3 | Up | `Ldb::UpdateLastVersion`, `UpstreamTransport::FlushVvUp` (sev 5) | Server version counter advanced; change notification |
| 4 | Down | `InConnection::RequestVvUp … changeType:all/notify` | Downstream asks for the upstream version vector |
| 5 | Up | `SERVER_RequestVersionVector`, `SERVER_AsyncPoll` | Upstream receives the poll |
| 6 | Down | `InConnection::ReceiveVvUp`, `Creating new session` | Vectors compared |
| 7 | Down | `InConnection::RequestUpdates Requesting updates. credits:32 … delta::…` | Downstream requests missing versions and advertises credits |
| 8 | Up | `SERVER_RequestUpdates … credits:32` | Request received |
| 9 | Up | `Join::SubmitUpdate LDB Updating ID Record` (`uidVisible 1`) → `Join::SubmitUpdate Sent:` | Update offered to the partner |
| 10 | Down | `InConnection::ReceiveUpdates Received: uid… gvsn… fileName…` | Downstream learns of the update |
| 11 | Down | `Meet::Install Retries:0 … updateType:remote` | Install task created (Retries > 0 means earlier attempts failed) |
| 12 | Up | `OutConnection::OpenFile Received request for update … rdcDesired:1` | File requested; RDC *may* be used |
| 13 | Up | `Marshaller::Marshal FileAttrs in metadata` | Metadata marshaled |
| 14 | Up | `ContentSetManager::UpdateHash` (hash/similarity filled) | Staged, XPRESS-compressed, signatures computed |
| 15 | Up | `StageWriter::CompleteDownloadStage … -Downloaded.frx` | Staged file ready |
| 16 | Up | `OutConnection::OpenFile Sent file … fileSize:` and `InitializeFileTransferAsyncState::ProcessIoCompletion Initialized … rdc:1` | Upstream ready to serve |
| 17 | Down | `Rdc::SyncClientState::Flush Rdc Need Assembler Statistics` (RDC only) | What crossed the wire |
| 18 | Down | `InConnection::LogTransferActivity Received RAWGET` or `RDCGET … stagedSize:` | Full copy (RAWGET) or differential (RDCGET) |
| 19 | Down | `StageWriter::CompleteDownloadStage` | File now in downstream staging |
| 20 | Down | `MarshalContext::Initialize Create file:[name-{GUID}-vN.ext]` | Unmarshalling |
| 21 | Down | `Meet::TransferToInstalling` | Decompressed into `Installing` |
| 22 | Down | `Meet::Download Download Succeeded : true` | Transfer phase complete |
| 23 | Down | `Meet::InstallRename Move out previous version…` / `Meet::MoveOut` / `Purge existing file in Deleted` / `RenameDelete` | Only for updates: old copy moved out and purged (DFSR never patches files in place) |
| 24 | Down | `Meet::InstallRename File moved.` | Moved into the RF |
| 25 | Down | `Meet::InsertIdRecord` / `Meet::UpdateIdRecord` (`meetInstallUpdate 1`, GVSN/hash match upstream) | Downstream DB updated |
| 26 | Down | `Meet::InstallRename -> DONE Install-rename completed` | File live |
| 27 | Down | `InConnection::UpdateProcessed … updatesLeft:0 processed:N failures:0` | Batch processed |
| 28 | Down | `InConnection::CommitSession Connection in sync` | Session committed with no failures |
| 29 | Down | `UpdateWorker::ConsumeUpdates No pending updates`, `ContentSetContext::Hibernate` | Idle until next change |

Upstream also logs `Rdc::SyncServerState::~SyncServerState RDC Need Reader Statistics` after an RDC transfer.

### Staged and temporary file naming

| Pattern | Meaning |
|---|---|
| `<ver>-{UID}-{GVSN}-Downloaded.frx` | Completed staged file (prefix = GVSN version) |
| `…-Downloading.frx` | Staging in progress |
| `name-{GUID}-vN.ext` (in `MarshalContext::Initialize`) | Working name in Installing |
| `name-{GUID}-vN.ext` (as `newName` in `Meet::MoveOut`) | Name given to the displaced copy (ConflictAndDeleted / Deleted) |

---

## 8. Scenario playbook

### 8.1 Large file added (Part 3)

Follows §7 exactly. Files above the RDC minimum (64 KB by default) are staged and XPRESS-compressed unless their extension is on the compression exclusion list. A new file shows `RAWGET` (there is nothing on the downstream to diff against) and `meetInstallUpdate 1` on the downstream DB record.

### 8.2 Small file added (Part 4)

Files under 64 KB skip the RDC path: no similarity data (all zeros), `RAWGET`, and on the downstream the file moves straight into the RF (`Meet::InstallRename File moved`) without the usual staging→Installing sequence.

### 8.3 File modified (Part 5)

- Upstream: `Ldb::Update` with GVSN > UID and hash/similarity zeroed; USN `Reason: Close Data Extend Data Overwrite`.
- Upstream restages and recomputes hash/similarity.
- Downstream: `RDCGET` plus RDC statistics. In the sample a ~597 KB file required ~6 KB on the wire.
- Downstream displaces the old copy (`Move out previous version`, `MoveOut`, `Purge existing file in Deleted`, `RenameDelete`), then installs the new one. A file locked on either side blocks this step.
- Windows Server 2003 R2 logs show less polling detail than 2008 at severity 4.

### 8.4 Office applications and temporary files (Parts 6 and 7)

Office saves via temporary and renamed files, which generates a lot of log noise and can cause extra replication.

| Signature | Meaning |
|---|---|
| `UsnConsumer::ProcessUsnRecord Filtered USN_RECORD` for `~$name.doc`, `~WRDnnnn.tmp`, `XXXXXXXX.tmp` | Matches the default file filter (`~*`, `*.bak`, `*.tmp`) and/or temporary/hidden attributes; never replicated. These never get a UID/GVSN — follow them by FID |
| `UsnConsumer::UpdateUsnOnly … USN-only update` | Close with no replicable change (e.g. `Close Object ID Change`); nothing replicates |
| `UsnConsumer::TombstoneOrDelete` when the real file is renamed to a filtered name | Treated as a delete (`present 0`, GVSN bumps) |
| `UsnConsumer::UidTunnelReanimate … UID tunnelling` | A temp file renamed back to the original name inherits the old UID with a new GVSN (Word 97–2003 pattern) |
| `UsnConsumer::CreateNewRecord` with UID == GVSN for an existing document name | The temp file was filtered at creation, so the renamed result gets a brand-new UID (Word 2007 pattern). Downstream receives a **tombstone of the old UID** plus a **new file**; result is `RAWGET`, not RDC |
| `UpdateWorker::ConsumeUpdates Available updates were locked` | Another update for the same file is in progress |
| `StageWriter::AbortDownloadStage … Deleted` + `[WARN] Rdc::SeedFile::UseRelated (Ignored) … 9024 meta data is not synchronized` | A newer version superseded one being staged; benign |
| `Rdc::SeedFile::Initialize The hashes coincide. The download is redundant` → `[ERROR] DownstreamTransport::RdcGet Failed … 9047` → `Download Succeeded : false` | The app touched the file without changing content. Not a real error: only version metadata is updated |

Other Office products (Excel, PowerPoint) may behave differently.

### 8.5 File deleted (Part 8)

- Upstream: `Ldb::Update` with `present 0`, GVSN incremented, hash zeroed; `UsnConsumer::TombstoneOrDelete ID record tombstoned`.
- Only metadata travels; no staging.
- Downstream: `Meet::MoveOut Moving contents and children out of replica` (DFSR "moves out" rather than deletes), then `Ldb::Update present 0`, then `Meet::InstallTombstone -> DONE Install Tombstone complete`.
- The file lands in `ConflictAndDeleted` and `ConflictAndDeletedManifest.xml` is updated (not visible at severity 4).
- The tombstone record stays in the database to allow reanimation. The series describes this as roughly 30 days; the sample configuration dump shows `TombstoneExpiryInMin: 86400` (60 days), so check the actual setting.

### 8.6 File renamed (Part 9)

Same FID and UID; GVSN increments; new `name`; USN `Reason: Close Rename New Name`. Metadata only: no RAWGET/RDCGET, no staging. Downstream completes with `Meet::InstallMove -> DONE Install-move completed`.

### 8.7 Conflict (Part 10)

Both servers changed the same file between replication cycles; the last writer wins.

- Downstream (loser): `Meet::LocalDominates Conflicting gvsn:<local> …` — the method name means "checking whether local dominates", not that local won. At severity 5 the explicit result appears as `Local version dominates…` or `Remote version dominates…`.
- Loser's copy: `Meet::InstallRename Move out previous version` → `Meet::MoveOut … newName:name-{localGUID}-vN.ext` into `ConflictAndDeleted` (manifest updated).
- `EventLog::Report Logging eventId:4412` with parameters: csId, full path, RF root, UID, RF name, RG name, RG GUID, member GUID, conflict file name.
- Winner's version is installed; the downstream DB now carries the upstream's UID/GVSN/hash.
- Connection re-establishment lines (`[WARN] ReplicaSetManager::EstablishConnection Shutting down existing connection`, `OutConnection::EstablishSession Replacing content set`) may surround it.

### 8.8 Folder created (Part 11)

- `attributes 0x10`, UID == GVSN.
- A directory-walker job runs to discover children: `Ldb::InsertWalkerJob … moveType:MoveIn`, `DirWalkerTask::QueueMoveinJob`, `Run Start walking directory`, `MoveinStep`, `Job::Finish`, `RemoveJob`, `Ldb::DeleteWalkerJob`.
- Only metadata (name, security, parent) is sent. Logged as `RAWGET` with a small `stagedSize`, though nothing is actually staged; no RDC or compression for folders.

### 8.9 Service start, DC bind, and configuration polling (Part 12)

Typical start-up order:

1. `CREG … ReadConfigFilePath` — locate `Replica_<RG GUID>.XML` and `Volume_<vol GUID>.XML` via the registry.
2. `CXML … PostProcessVolumeConfig` — volume found; DB path set to `<vol>\System Volume Information\DFSR`.
3. `CFAD Config::AdConnection::Connect … BindToAd … BindToDc Bound` — the DC used until the service restarts. If that DC later goes offline, the cached XML configuration is used.
4. `EventLog::Report … eventId:1206` — which DC was bound.
5. `ReadRootNamingContexts` (default, schema, services, DCs, computers, system, sites) and `CheckSchemaVersion … objectVersion` — confirms the forest supports DFSR.
6. SYSVOL checks (`IsSysVolCommitFlagSet`, `[SYSVOL] Not a DC` on member servers).
7. `TranslateDn Cracked Name` — partner's `DOMAIN\NAME$` and SID.
8. `VolumeIdTable::Rebuild` / `ReadVolumeInfo Ignoring volume … (remote, cd-rom or removable)`.
9. `UpdateSpn` — DFSR SPN registered (or already present).
10. XML config files rewritten under System Volume Information.
11. `ServiceConfig::ProcessAdPollConfigEvents Received AD Poll event with N … changes` — N is the count of volumes/RGs, not the number of changes.
12. `ReplicaSetManager::Initialize`, `AddInConnection`, `AddOrUpdateConnection`.
13. Configuration dump blocks: `[[ DfsrReplicationGroup ]]`, `[[ DfsrMember ]]`, `[[ DfsrConnection ]]` (once per connection), `[[ DfsrVolume ]]`, `[[ DfsrReplicatedFolder ]]`. **These print the XML-cached copy of the LDAP results**, so when troubleshooting configuration, check both the LDAP queries and the local XML files.
14. `ChangeJournal::Start Start reading journal … at usnNumber`, `VolumeManager::Initialize Volume initialized`.
15. `ContentSetManager::Run … state:Normal`, `Staging::ScanStagingDirectory Staging space usage`.
16. `DownstreamTransport::SetupBinding Setting authentication information for partner` (Kerberos; NTLM is not supported) and `stringBinding:[5bc1ed07-f5f5-485f-9dfd-6fd0acf9a23c@ncacn_ip_tcp:<partner>]` (the DFSR RPC interface UUID).
17. `DownstreamTransport::EstablishConnection Established`, `InConnection::ReConnectAsync Connection established with partner`, version vector exchange, `SERVER_EstablishConnection Succeeded`.

Periodic `Config::AdReader::Peek Subscriptions Checksum, old = X, new = X` lines are routine AD polling; equal values mean no configuration change.

Useful configuration values in the dump:

| Block | Parameters of interest |
|---|---|
| DfsrReplicationGroup | `LastChangeNumber/Time/Source` (AD metadata), `ReplicationGroupType`, `TombstoneExpiryInMin`, `DefaultSchedule.<day>` |
| DfsrMember | `MemberName`, `MemberDn`, `MemberDns` |
| DfsrConnection | `ConnectionGuid`, `PartnerName/Dns/PrincName/Sid`, `Enabled`, `Inbound`, `RdcEnabled`, `RdcMinFileSizeInKb` (64), `Schedule.<day>` |
| DfsrVolume | `VolumePath`, `DatabasePath`, `MinNtfsJournalSizeInMb` (USN journal size; verifiable with `fsutil`), `MaxJetSessions`. The serial number here is DFSR-internal and won't match DISKPART |
| DfsrReplicatedFolder | `RootPath`, `StagingPath`, `StagingSizeInMb` (quota, not actual usage), `ConflictPath`, `ConflictSizeInMb`, `FileFilter` (default `~*, *.bak, *.tmp`), `DirectoryFilter`, `IsPrimary`, `ReadOnly`, `DisableSaveDeletes`, `CompressedExtensions` |

`Ghosted`, `CacheObeyConnectionSchedule`, and the cache-age values relate to an unshipped feature and can be ignored.

Default compression exclusion list (`CompressedExtensions`): WMA, WMV, ZIP, JPG, MPG, MPEG, M1V, MP2, MP3, MPA, CAB, WAV, SND, AU, ASF, WM, AVI, Z, GZ, TGZ, FRX.

#### Schedule hex decoding

Each `Schedule.<day>` line has 24 comma-separated groups (one per hour); each group has 4 hex digits (one per 15-minute block). Each digit is a bandwidth level in **bits** per second:

| Hex | Bandwidth | Hex | Bandwidth |
|---|---|---|---|
| F | Full | 7 | 2 Mbps |
| E | 256 Mbps | 6 | 1 Mbps |
| D | 128 Mbps | 5 | 512 Kbps |
| C | 64 Mbps | 4 | 256 Kbps |
| B | 32 Mbps | 3 | 128 Kbps |
| A | 16 Mbps | 2 | 64 Kbps |
| 9 | 8 Mbps | 1 | 16 Kbps |
| 8 | 4 Mbps | 0 | No replication |

### 8.10 New replication group and initial sync (Part 13)

Members discover new configuration on their own AD polling schedule, so they start at different times.

**Primary (authoritative) member** — `IsPrimary: TRUE`:

`CheckContentSetState state:InitialBuilding` → `CreateRootRecord` (RF root record, UID `{csId}-v1`) → `Ldb::Insert contentSetRecord state:InitialBuilding` → staging registered → directory walk (`DirWalkerTask::CreateOneRecord` per existing file, UID == GVSN, `fence … P`) → **event 4112** (primary finished initial building, ready to serve) → `UpdateRootFence` → `SetContentSetState newState:Normal` → `DbBuildComplete … Transition from InitialBuilding to Normal`.

**Non-authoritative member** — `IsPrimary: FALSE`:

`InitialBuilding` → root record → directory walk → **event 4102** (ready for initial sync) → `newState:InitialSync(Sync)` → replication from the primary → `InitialSyncManager::ReturnToken … sync step finished` → `InitialSync(Cleanup)` → `InitialSyncCleanupTask` → **event 4104** (initial sync complete) → `Normal` → `InitialSyncComplete`.

Expected, transient errors while members catch up:

| Error | Meaning |
|---|---|
| `9026 (0x2342) The connection is invalid` (with `9027 A failure was reported by the remote partner`) during `EstablishConnection` | Partner has not yet picked up the new topology from AD |
| `9051 (0x235b) The content set is not ready` during `EstablishSession` | Partner is still in initial building/sync |
| `[WARN] DownstreamTransport::EstablishConnection Failed. Try flat name.` | Retry using the NetBIOS name |

These clear once both members have polled AD and finished initial building.

### 8.11 Sharing violation / locked file (Part 14)

- Downstream: `[ERROR] DownstreamTransport::RdcGet Failed …` with `Error:32 (0x20) The process cannot access the file because it is being used by another process`, plus `Failed to receive RAWGET … stagedSize:0`.
- Upstream: `StageWriter::AbortDownloadStage … -Downloading.frx. Deleted.`, **event 4304** (sharing violation, includes path and UID), and an error stack under `InitializeFileTransferAsyncState::ProcessIoCompletion Failed` ending in `MarshalContext::OpenFileForInfo`.
- Downstream retries: watch `Meet::Install Retries:N` climb.
- Not permanent: once the file is unlocked, `UpdateHash` succeeds upstream and replication proceeds normally.
- A lock on the **downstream** copy blocks the move-out step during an update (§8.3).

### 8.12 Pre-seeded data during initial sync (Part 15)

Data copied in advance (e.g. robocopy) is compared by hash.

| Downstream situation | Log behaviour | Outcome |
|---|---|---|
| Identical file (same hash) | `Meet::Install` appears but **no** `Download Succeeded` or `MoveOut`; `UpdateHash` shows matching hash; record takes upstream UID/GVSN; `Meet::InstallMove -> DONE` | Only metadata exchanged |
| Same name/path, different content | `LocalDominates Conflicting` (downstream always loses during initial sync); upstream serves the file; `RDCGET` using the local copy as seed; old copy moved to `ConflictAndDeleted`; event 4412 | Only differing blocks sent |
| File only on downstream | `InitialSyncCleanupTask::MoveOut Moving <path> to pre-existing`, `ContentSetManager::DeleteChildren LDB Deleting ID Record` | Moved to `PreExisting`; `PreExistingManifest.xml` updated |
| RF root folder | `Meet::InstallOverwrite Moving away content that is in the way … -> DONE` | Root metadata aligned |

`Meet::Install … updateType:remote` for a matched file is misleading — it appears even though no data moves.

### 8.13 RDC in detail, severity 5 (Part 16)

Upstream:

| Signature | Meaning |
|---|---|
| `FrsSimilarityManager::Delete Deleteing similarity data` | Old cross-file similarity data dropped after a change |
| `Rdc::MyFRS_RDC_FILEINFO::ComputeRecursionDepth RecursionDepth = N, filesize=` | Signature levels needed (signatures of signatures for larger files) |
| `OutConnection::OpenFile rdcDesired=1, filesize=…, rdcMinFileSizeInKb=64, rdcSignatureLevels=N` | RDC decision inputs |
| `GetStageReaderOrWriter … Failed to get stage reader as the file is not staged` | Needs staging (normal after a change) |
| `Staging::LockedFiles::Lock … state: Downloading/Downloaded/Partial (refCount==N)` | Staging lock states |
| `Staging::FindAllFiles Older version found` | Older staged copy will be cleaned up |
| `Staging::OpenForWrite` | Copying into staging with XPRESS compression |
| `FrsSignatureIndexFile::Open … for write Levels=` | RDC signature stream opened |
| `StreamToIndex RDC generate begin/end`, `SignatureGenerator Level=… Depth=…`, `Similarity enabled` | Signatures generated |
| `Staging::AddCommitFile File added: … new usage:` | Staging space accounting |
| `OutConnection::OpenFile Prepare to serve over RDC` | Ready to send signatures |
| `StagingCleanupTask::DeleteQueuedFiles Deleted` | Previous staged version removed |
| `OutConnection::OpenFile Sent file` | **Misleading** — the downstream is still deciding what it needs |
| `SERVER_RdcGetSignatures`, `SERVER_RdcPushSourceNeeds offset/length … needCount:N`, `SERVER_RdcGetFileData Sent`, `SERVER_RdcClose` | The RDC conversation |

Downstream:

| Signature | Meaning |
|---|---|
| `Rdc::SeedFile::Initialize RDC signatureLevels … enableSim=1` | Preparing a local seed |
| `Rdc::SeedFile::UseSimilar similarrelated (SimMatches=N)` | Seed chosen by similarity |
| `Rdc::SeedFile::UseRelated Staging "UIDRelated" file` | Prior version of the same UID used as seed (staged locally first) |
| `RdcCache::Find / MatchSuffix / MatchPaths` | Similarity lookups, including in `ConflictAndDeleted` |
| `Rdc::SeedFile::OpenSeedSigDB Using seed file … seed(type:UIDRelated …)` | Seed in use |
| `XpressRdcNeedAssembler … All needs processed` | Reconstruction from seed + received blocks |
| `Meet::FindUidRelated`, `Meet::ProcessUid Uid related found` | Locating the existing copy |
| `Meet::LocalDominates Remote version dominates localgvsn` | Routine dominance check at severity 5 — **not** a conflict |
| `Meet::GetNameRelated Name related not found` | No name collision |
| `Meet::InstallRename Tunneling last access time in Installing` | Old copy's last-access time carried to the new copy |
| `Meet::TransferToInstalling Read N bytes, wrote N bytes` | Decompressed size |
| `InConnection::CommitSession Committing … newVersionVector` | New vector recorded |
| `StateHistory::Update Fire sync info record state update` | Summary: `state In Sync`, `updatedTransferred`, `byteTransferred`, `tombstonesGenerated`, `conflictsGenerated` |

#### RDC statistics blocks

Downstream `Rdc Need Assembler Statistics`:

| Field | Meaning |
|---|---|
| Compression Ratio | Overall savings percentage |
| Target Uncompress Size | Real file size in the RF |
| Target Compress Size | XPRESS-compressed size in staging (little difference for already-compressed content such as JPG-heavy documents) |
| Bytes Received (Signatures / Data) | Actual bytes on the wire, split into signature bytes and data blocks |
| Number of remote calls | Signature / Needs / Data RPC calls |
| SEED / SOURCE sections | Blocks reused from the local seed vs. fetched from the upstream |

Upstream `RDC Need Reader Statistics`: `RDC Need Size`, `Bytes sent to downstream` (should match the downstream's Bytes Received), counts of compressed/uncompressed/copied XPRESS blocks.

The series notes that on Windows Server 2008 these statistics may require severity 5.

### 8.14 RPC blocked (Part 17)

```
[WARN] DownstreamTransport::EstablishConnection Failed. Try flat name.
  [Error:9027(0x2343) … A failure was reported by the remote partner]
  [Error:1722(0x6ba) … The RPC server is unavailable.]
```

Followed (severity 5) by `LogExtendedErrorInformation` blocks containing process ID, generating component, status, and parameters (protocol `ncacn_ip_tcp`, target host). Statuses seen: **1722** (RPC server unavailable), **1237** (operation could not be completed; retry), **10060** (connection attempt timed out). Translate codes with `err.exe` (WINERROR.H). Verify RPC endpoint mapper and dynamic port connectivity between partners.

### 8.15 LDAP / DC unreachable (Part 18)

| Signature | Meaning |
|---|---|
| `[ERROR] Config::AdQuery::Search Failed to ldap_search_ext_s(). Error:Server Down base:CN=<self>,…` | DC not answering a search for the server's own object |
| `Config::AdConfig::IsConnected Lost connection to AD` with `Error:58` and `Error:81 … Server Down` | 58 = "specified server cannot perform the requested operation" (WINERROR.H); 81 = `LDAP_SERVER_DOWN` (WINLDAP.H) |
| `Config::AdConnection::Connect Binding to dcAddr:\\<IP> dcDnsName:\\<DC>` | DC being retried (both IP and DNS name shown) |
| `[ERROR] Config::AdSession::Connect Failed to ldap_connect(). timeout:30 Error:Server Down` | Bind attempt by DNS name, then by IP |
| `[WARN] ServiceConfig::DsPollIsDue Failed to enable lightweight polling` with `Error:160 One or more arguments are not correct` | **Misleading** knock-on error from the LDAP failure |

DC connectivity errors aren't always LDAP-coded; expect Win32 errors mixed in.

### 8.16 Blocked by a file-screen filter driver (Part 19)

Replication and RDC reconstruction succeed, but the final move into the RF fails:

```
Meet::InstallRename Moving contents from Installing to final destination.
Meet::InstallStep Done installing file / Deleting fid in installing
Meet::Install -> WAIT Error processing update. … code:5
  [Error:5(0x5) … FileUtil::RenameByHandle … Access is denied.]
```

The "Access is denied" comes from the FSRM file-screen filter driver (e.g. the default "block executable files" screen), not from NTFS permissions. Align FSRM file screens with DFSR file filters.

### 8.17 Files that never replicate: temporary and filtered (Part 20)

| Signature | Cause |
|---|---|
| `UsnConsumer::ProcessUsnRecord Skipping USN_RECORD with FILE_ATTRIBUTE_TEMPORARY flag` with `FileAttributes: 0x120` | File has the temporary attribute (0x100) set; DFSR skips it regardless of name |
| `UsnConsumer::ProcessUsnRecord Filtered USN_RECORD` | Name matches the RF file filter (default `~*`, `*.bak`, `*.tmp`) |

### 8.18 Slow replication from bandwidth throttling (Part 21, severity 5)

Upstream opens a data pipe (`RpcAsyncPipeState::Start`, `RawGetFileDataAsyncState::DataPipeThread Entering data pipe thread`) and exits it when done. Downstream:

```
DOWN BandwidthThrottler::WaitOneInterval Throttling: Waited for 4100 ms (waitResult 258)
… repeated many times …
DownstreamTransport::RpcAsyncPipeDownload Wait completion
InConnection::LogTransferActivity Received RAWGET … stagedSize:…
Meet::Download Done downloading content
```

The downstream transfers a burst at full speed, then pauses to honour the schedule's bandwidth level. Many `WaitOneInterval` lines spanning the transfer indicate deliberate throttling (check the `Schedule.<day>` hex values, §8.9), not a fault. In the sample, a ~12.6 MB staged file took about 14 minutes at 128 Kbps.

---

## 9. DFSR event IDs referenced in the logs

Event writes appear as `EVNT EventLog::Report Logging eventId:N parameterCount:M` followed by one line per parameter, which makes it easy to map an event log entry back to the debug log.

| Event | Meaning |
|---|---|
| 1206 | DC the service bound to |
| 4102 | Non-authoritative member ready for initial sync |
| 4104 | Initial sync completed on the member |
| 4112 | Primary member finished initial building; ready to serve |
| 4304 | Sharing violation (file locked) |
| 4412 | Conflict resolved; local copy lost and was moved to ConflictAndDeleted |

---

## 10. Error codes seen in the series

| Code | Text | Context / meaning |
|---|---|---|
| 5 | Access is denied | Final install blocked; in the series caused by an FSRM file screen |
| 32 | File in use by another process | Sharing violation (event 4304) |
| 58 | Server cannot perform the requested operation | AD/LDAP connectivity |
| 81 | Server Down (LDAP_SERVER_DOWN) | LDAP connectivity |
| 160 | One or more arguments are not correct | Knock-on after LDAP failure; misleading |
| 1237 | Operation could not be completed; retry | RPC extended error |
| 1722 | The RPC server is unavailable | RPC blocked / unreachable |
| 10060 | Connection attempt timed out | RPC extended error |
| 9024 (0x2340) | File metadata not synchronized with file system | RDC seed superseded; ignored warning |
| 9026 (0x2342) | The connection is invalid | Partner doesn't know the topology yet |
| 9027 (0x2343) | A failure was reported by the remote partner | Wrapper — read the next error in the stack |
| 9047 (0x2357) | The download is redundant | Hashes match; not a real error |
| 9051 (0x235b) | The content set is not ready | Partner still in initial build/sync |

Error stacks list the outermost wrapper first; the most specific cause is usually the last entry. Translate unfamiliar codes with `err.exe`.

---

## 11. Misleading entries — quick list

| Entry | Reality |
|---|---|
| `Meet::LocalDominates Conflicting …` | Name of the check, not the outcome; local may well have lost |
| `Meet::LocalDominates Remote version dominates` (sev 5) during a normal update | Routine check, not a conflict |
| `[ERROR] DownstreamTransport::RdcGet Failed … 9047 The download is redundant` | Content identical; only metadata updated |
| `Meet::Download Download Succeeded : false` after a 9047 | Same as above |
| `OutConnection::OpenFile Sent file` | Upstream is ready to serve; transfer may not have happened yet |
| `Meet::Install … updateType:remote` for pre-seeded matching files | No data is transferred |
| `RAWGET … stagedSize:` for folders | Folders aren't staged |
| `[WARN] … UseRelated (Ignored) Failed … 9024` | Superseded staging, harmless |
| `Error:160` after LDAP failure | Side effect of the LDAP failure |
| `Error:5 Access is denied` at install | Often a filter driver, not permissions |
| Downstream lines timed before the upstream change | Clock skew / lab timing; correlate by IDs |
| Ghosting / cache / `ReadOnly` fields | Unshipped or unsupported features |

---

## 12. Search strings cheat sheet

| Looking for | Search for |
|---|---|
| A specific file's history | its name, then pivot to its UID and FID |
| Local changes picked up | `UsnConsumer::CreateNewRecord`, `UsnConsumer::UpdateIdRecord`, `TombstoneOrDelete` |
| Files excluded | `Filtered USN_RECORD`, `FILE_ATTRIBUTE_TEMPORARY` |
| Updates offered to partners | `Join::SubmitUpdate Sent` |
| Updates received | `InConnection::ReceiveUpdates Received` |
| Transfers | `LogTransferActivity` (`RAWGET` / `RDCGET` / `Failed to receive`) |
| Successful installs | `Install-rename completed`, `Install-move completed`, `Install Tombstone complete` |
| Retries | `Meet::Install Retries:` with a non-zero value |
| Conflicts | `LocalDominates`, `eventId:4412` |
| Sync state | `Connection in sync`, `No pending updates`, `StateHistory::Update` |
| Initial sync progress | `SetContentSetState`, `eventId:4102`, `eventId:4112`, `eventId:4104` |
| Pre-existing files | `pre-existing` |
| Locked files | `Error:32`, `eventId:4304`, `AbortDownloadStage` |
| RPC connectivity | `EstablishConnection`, `1722`, `LogExtendedErrorInformation` |
| AD/LDAP problems | `CFAD` with `[ERROR]`, `Server Down`, `Lost connection to AD` |
| DC in use | `BindToDc Bound`, `eventId:1206` |
| Throttling | `BandwidthThrottler::WaitOneInterval` |
| RDC efficiency | `Rdc Need Assembler Statistics`, `RDC Need Reader Statistics` |
| Problems in general | `[ERROR]`, `[WARN]` |
