namespace DfsrAnalyser.Tests;

/// <summary>Real lines from the lab sample logs (lab domain dfsrtest.com only).</summary>
internal static class Samples
{
    public const string Header1 =
        "* DFSR Log Sequence:2 Index:5 Computer:DFSRDC01 TimeZone:AUS Eastern Daylight Time (GMT+11:00) (TzId>AUS Eastern Standard Time<dIzT) Build:[Jan 01 2016 08:00:00 built by: WinBuild (FRE)]";

    public const string Header2 =
        @"* Configuration logLevel:5 maxEntryCount:200000 maxFileCount:1000 logPath:\\.\C:\Windows\debug\";

    public const string SubmitUpdateSent =
        "20261111 13:14:07.482 4444 JOIN  1253 Join::SubmitUpdate Sent: uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v1786 gvsn:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v1786 name:file_0772.txt connId:{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5} csId:{B2683E40-9D0D-4BF7-A465-298E6B684DA8} csName:DFSFolder";

    public const string RawGet =
        "20261111 13:14:57.712 3244 INCO  6535 InConnection::LogTransferActivity Received RAWGET updateName:file_1643.txt uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v2657 gvsn:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v2657 connId:{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5} csId:{B2683E40-9D0D-4BF7-A465-298E6B684DA8} stagedSize:3008767";

    public const string AddContentSet =
        @"20261111 09:24:56.648 2924 VLMG  5754 VolumeManager::AddContentSet Starting a new ContentSetManager. csId:{5A27B242-B850-46D7-9BD2-A363911C5190} csName:SYSVOL Share volId:{F4A5582F-520D-4C25-AB42-3C09224F9F70} volPath:\\.\C: volState:INITIALIZING";

    public const string ErrorLine =
        "20261112 04:14:36.222  684 SCFG   125 [ERROR] ConfigMonitorTask::Step Local machine is not joined to any domain. Scheduling MarkAndSweepUninitializedVolumes. Error:";

    public static readonly string[] ErrorStack =
    [
        "+\t[Error:9551(0x254f) Migration::SysVolUtil::IsLocalComputerDomainJoined migration.cpp:5537 684 C The local computer is not joined to any domain]",
        "+\t[Error:9551(0x254f) Migration::SysVolUtil::GetLsaDnsDomainName migration.cpp:5416 684 C The local computer is not joined to any domain]",
    ];

    public const string WarnLine =
        "20261111 13:01:52.924 7116 JOIN   155 [WARN] Join::FinalizeJoin (Ignored) Failed to access volume manager data";

    public const string MeetInstall =
        "20261111 13:00:27.345 3888 MEET  1333 Meet::Install Retries:0 updateName:file_1002.txt uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v12 gvsn:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v12 connId:{DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5} csName:DFSFolder updateType:remote";

    public const string EventLogReport =
        "20261111 12:55:38.505 4164 EVNT  1195 EventLog::Report Logging eventId:4412 parameterCount:10";

    public const string CreateNewRecord =
        "20261111 11:47:05.743 3988 USNC  2871 UsnConsumer::CreateNewRecord LDB Inserting ID Record:";

    public static readonly string[] IdRecord =
    [
        "+\tfid                             0x3000000012940",
        "+\tusn                             0x4929abd0",
        "+\tuidVisible                      0",
        "+\tmeetInstallUpdate               0",
        "+\trecUpdateTime                   16010101 00:00:00.000 GMT",
        "+\tpresent                         1",
        "+\tattributes                      0x20",
        "+\tgvsn                            {2314971A-C581-4D65-A9C1-9C297D1261E2}-v34",
        "+\tuid                             {2314971A-C581-4D65-A9C1-9C297D1261E2}-v34",
        "+\tparent                          {B2683E40-9D0D-4BF7-A465-298E6B684DA8}-v1",
        "+\tclock                           20261111 00:47:05.743 GMT (0x1dd71762c7cc6bb)",
        "+\tcsId                            {B2683E40-9D0D-4BF7-A465-298E6B684DA8}",
        "+\thash                            00000000-00000000-00000000-00000000",
        "+\tname                            file_0001.txt",
        "+\t",
    ];

    public const string TombstoneOrDelete =
        "20261111 13:12:44.562 3656 USNC  3049 UsnConsumer::TombstoneOrDelete ID record tombstoned from USN_RECORD:";

    public static readonly string[] UsnRecord =
    [
        "+\tUSN_RECORD:",
        "+\tRecordLength:        88",
        "+\tFileRefNumber:       0x20000000204C3",
        "+\tReason:              Close File Delete ",
        "+\tFileAttributes:      0x20",
        "+\tFileNameLength:      26",
        "+\tFileName:            file_0007.txt",
    ];

    public const string Exit = "20261111 13:15:28.806 1240 MAIN   843 main Service Exiting Successfully";
}
