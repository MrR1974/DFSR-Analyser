using System.Globalization;
using System.Net;
using DfsrAnalyser.Collection;
using DfsrAnalyser.Core.Collection;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.Cli;

/// <summary>Remote collection commands. Every run writes a diagnostic log under &lt;case&gt;\logs (or the current folder).</summary>
internal static class CollectCommands
{
    public const string Usage = """
          dfsra discover [--domain <dns-or-dc>] [--user <DOMAIN\user>]
              List replication groups and member servers from AD (SYSVOL included; DCs marked).
          dfsra test --case <folder> <targets> [--event-log] [--user <DOMAIN\user>]
              Check each server's log share (and event log) without copying anything.
          dfsra collect --case <folder> <targets> [range] [--event-log] [--dry-run] [--user <DOMAIN\user>] [--save-profile <name>]
              Copy the logs overlapping the range from each server and import them. --dry-run lists the plan only.
          dfsra topup --case <folder> --profile <name> [--user <DOMAIN\user>]
              Re-run a saved profile from shortly before its last collection up to now.
          dfsra import-evtx --case <folder> --server <name> <file.evtx>...
              Import exported DFS Replication event logs.

          <targets>  --servers <host>[,<host>...]   host or host|<log folder>, e.g. dfsrdc01|\\dfsrdc01\d$\dfsrlogs
                     --discover [--rg <name>[,<name>...]] [--domain <dns-or-dc>]   members of all (or the named) RGs
          [range]    --last <N>m|h|d   or   --from "yyyy-MM-dd HH:mm" [--to "yyyy-MM-dd HH:mm"]   (local time; omit for everything)
          --user     prompts for the password (never pass it on the command line)
        """;

    private static readonly HashSet<string> Commands = ["discover", "test", "collect", "topup", "import-evtx"];

    public static bool Handles(string cmd) => Commands.Contains(cmd);

    private sealed class Options
    {
        public string Command = "";
        public string? Case, Domain, User, Servers, Rg, From, To, Last, Profile, SaveProfile, Server;
        public bool Discover, EventLog, DryRun;
        public List<string> Files = [];
    }

    public static int Run(string[] args)
    {
        var o = new Options { Command = args[0] };
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--case": o.Case = Next(); break;
                case "--domain": o.Domain = Next(); break;
                case "--user": o.User = Next(); break;
                case "--servers": o.Servers = Next(); break;
                case "--rg": o.Rg = Next(); break;
                case "--from": o.From = Next(); break;
                case "--to": o.To = Next(); break;
                case "--last": o.Last = Next(); break;
                case "--profile": o.Profile = Next(); break;
                case "--save-profile": o.SaveProfile = Next(); break;
                case "--server": o.Server = Next(); break;
                case "--discover": o.Discover = true; break;
                case "--event-log": o.EventLog = true; break;
                case "--dry-run": o.DryRun = true; break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {args[i]}.");
                    o.Files.Add(args[i]);
                    break;
            }
        }

        try
        {
            return o.Command switch
            {
                "discover" => Discover(o),
                "test" => Test(o),
                "collect" => Collect(o),
                "topup" => TopUp(o),
                "import-evtx" => ImportEvtx(o),
                _ => 2,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException
                                       or System.DirectoryServices.Protocols.LdapException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("ERROR: " + CollectionLog.Describe(ex));
            return 1;
        }
    }

    // ------------------------------------------------------------------ commands

    private static int Discover(Options o)
    {
        using var log = NewLog(o.Case);
        var cred = Credential(o.User);
        var result = new LdapDirectoryService(log).Discover(o.Domain, cred);
        Console.WriteLine($"Domain {result.Domain}  ({result.NamingContext})  via DC {result.DomainController}");
        foreach (var g in result.Groups)
        {
            Console.WriteLine($"\n{g.Name}{(g.IsSysvol ? "  [SYSVOL]" : "")}  {{{g.Guid.ToString().ToUpperInvariant()}}}");
            foreach (var m in g.Members)
                Console.WriteLine($"  {m.Name,-20} {m.DnsHostName ?? "(no dNSHostName)",-40}{(m.IsDomainController ? " DC" : "")}");
        }
        Console.WriteLine($"\nLog: {log.Path}");
        return 0;
    }

    private static int Test(Options o)
    {
        Require(o.Case, "--case");
        using var store = CaseStore.Open(o.Case!);
        using var log = NewLog(o.Case);
        var cred = Credential(o.User);
        var targets = Targets(o, cred, log);
        var tests = new Collector(store, new WNetShareConnector(log), new WindowsEventLogSource(), log).Test(targets, cred, o.EventLog);
        foreach (var t in tests)
        {
            Console.WriteLine($"{(t.Ok ? "OK  " : "FAIL")} {t.Server.Name,-18} {t.ShareDetail}");
            if (t.EventLogDetail is not null) Console.WriteLine($"     {"",-18} event log: {t.EventLogDetail}");
        }
        Console.WriteLine($"\nLog: {log.Path}");
        return tests.All(t => t.Ok) ? 0 : 1;
    }

    private static int Collect(Options o)
    {
        Require(o.Case, "--case");
        using var store = CaseStore.Open(o.Case!);
        using var log = NewLog(o.Case);
        var cred = Credential(o.User);
        var targets = Targets(o, cred, log);
        var (from, to) = Range(o);
        var report = Run(store, log, new CollectionRequest(targets, from, to, o.EventLog, o.DryRun, cred));

        if (o.SaveProfile is { } name && !o.DryRun)
        {
            var settings = CaseSettings.Load(o.Case!);
            settings.Upsert(new CollectionProfile
            {
                Name = name, Domain = o.Domain, Servers = [.. targets], IncludeEventLog = o.EventLog,
                LastCollectedUtc = to ?? DateTime.UtcNow,
            });
            settings.Save(o.Case!);
            Console.WriteLine($"Saved profile '{name}' in {CaseSettings.FileName}.");
        }
        return report.Servers.Any(s => s.Status == ServerStatus.Failed) ? 1 : 0;
    }

    private static int TopUp(Options o)
    {
        Require(o.Case, "--case");
        Require(o.Profile, "--profile");
        var settings = CaseSettings.Load(o.Case!);
        var profile = settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, o.Profile, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException($"No profile '{o.Profile}'. Profiles: {string.Join(", ", settings.Profiles.Select(p => p.Name))}");
        using var store = CaseStore.Open(o.Case!);
        using var log = NewLog(o.Case);
        var from = profile.LastCollectedUtc - CollectionProfile.TopUpOverlap;
        var now = DateTime.UtcNow;
        var report = Run(store, log, new CollectionRequest(profile.Servers, from, null, profile.IncludeEventLog, false, Credential(o.User)));
        if (report.Servers.Any(s => s.Status != ServerStatus.Failed))
        {
            profile.LastCollectedUtc = now;
            settings.Save(o.Case!);
        }
        return report.Servers.Any(s => s.Status == ServerStatus.Failed) ? 1 : 0;
    }

    private static int ImportEvtx(Options o)
    {
        Require(o.Case, "--case");
        Require(o.Server, "--server");
        if (o.Files.Count == 0) throw new ArgumentException("No .evtx files given.");
        using var store = CaseStore.Open(o.Case!);
        var source = new WindowsEventLogSource();
        var total = 0;
        foreach (var f in o.Files)
        {
            var records = source.ReadFile(Path.GetFullPath(f)).ToList();
            total += store.ImportEventLog(o.Server!, records);
            Console.WriteLine($"{Path.GetFileName(f)}: {records.Count} record(s).");
        }
        Console.WriteLine($"Imported {total} event-log record(s) for {o.Server}.");
        return 0;
    }

    // ------------------------------------------------------------------ helpers

    private static CollectionReport Run(CaseStore store, CollectionLog log, CollectionRequest req)
    {
        log.Line += Console.WriteLine;
        var report = new Collector(store, new WNetShareConnector(log), new WindowsEventLogSource(), log).Collect(req);
        Console.WriteLine();
        foreach (var s in report.Servers)
            Console.WriteLine($"{s.Status,-8} {s.Server.Name,-18} {s.Summary}");
        Console.WriteLine($"\n{(report.DryRun ? "Dry run" : "Collection")} finished in {report.Elapsed.TotalSeconds:N1} s. Log: {report.LogPath}");
        return report;
    }

    private static List<ServerTarget> Targets(Options o, NetworkCredential? cred, CollectionLog log)
    {
        var targets = new List<ServerTarget>();
        if (o.Servers is { } list)
        {
            foreach (var entry in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('|', 2);
                var host = parts[0];
                targets.Add(new ServerTarget(host.Split('.')[0].ToUpperInvariant(), host, parts.Length > 1 ? parts[1] : null));
            }
        }
        if (o.Discover)
        {
            var wanted = o.Rg?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new LdapDirectoryService(log).Discover(o.Domain, cred);
            foreach (var g in result.Groups.Where(g => wanted is null || wanted.Contains(g.Name)))
                foreach (var m in g.Members)
                    if (targets.All(t => !t.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                        targets.Add(new ServerTarget(m.Name, m.DnsHostName ?? m.Name));
        }
        if (targets.Count == 0) throw new ArgumentException("No servers: give --servers or --discover.");
        log.Info($"Targets: {string.Join(", ", targets.Select(t => $"{t.Name} ({t.EffectiveLogFolder})"))}.");
        return targets;
    }

    private static (DateTime? From, DateTime? To) Range(Options o)
    {
        if (o.Last is { } last)
        {
            var unit = char.ToLowerInvariant(last[^1]);
            var n = double.Parse(last[..^1], CultureInfo.InvariantCulture);
            var span = unit switch { 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), 'd' => TimeSpan.FromDays(n), _ => throw new ArgumentException("--last needs m, h or d.") };
            return (DateTime.UtcNow - span, null);
        }
        DateTime? Parse(string? s) => s is null ? null
            : DateTime.SpecifyKind(DateTime.Parse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal), DateTimeKind.Local).ToUniversalTime();
        return (Parse(o.From), Parse(o.To));
    }

    private static NetworkCredential? Credential(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return null;
        Console.Error.Write($"Password for {user}: ");
        var password = ReadMasked();
        var slash = user.IndexOf('\\');
        return slash > 0 ? new NetworkCredential(user[(slash + 1)..], password, user[..slash]) : new NetworkCredential(user, password);
    }

    private static string ReadMasked()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(k.KeyChar)) chars.Add(k.KeyChar);
        }
        Console.Error.WriteLine();
        return new string([.. chars]);
    }

    private static CollectionLog NewLog(string? caseFolder) =>
        new(Path.Combine(caseFolder ?? Environment.CurrentDirectory, "logs"));

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
    }
}
