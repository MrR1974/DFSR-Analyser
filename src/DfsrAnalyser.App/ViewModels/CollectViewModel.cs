using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DfsrAnalyser.App.Services;
using DfsrAnalyser.Collection;
using DfsrAnalyser.Core.Collection;

namespace DfsrAnalyser.App.ViewModels;

public sealed partial class ServerRowViewModel : ObservableObject
{
    [ObservableProperty] private bool _include = true;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _logFolder = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _detail = "";
    public string? FromGroups { get; set; }

    public ServerTarget ToTarget() => new(Name.Trim().ToUpperInvariant(), Host.Trim(), string.IsNullOrWhiteSpace(LogFolder) ? null : LogFolder.Trim());
}

public sealed partial class GroupRowViewModel(DiscoveredGroup group, CollectViewModel owner) : ObservableObject
{
    public DiscoveredGroup Group { get; } = group;
    public string Name => Group.Name + (Group.IsSysvol ? "  (SYSVOL)" : "");
    public string MembersText => string.Join(", ", Group.Members.Select(m => m.Name + (m.IsDomainController ? " (DC)" : "")));

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => owner.OnGroupToggled(this, value);
}

public sealed record RangeChoice(string Label, Func<CollectViewModel, (DateTime? From, DateTime? To)> Range, bool Custom = false)
{
    public override string ToString() => Label;
}

/// <summary>The Collect dialog: discover or type servers, test, dry-run, collect, save as a profile.</summary>
public sealed partial class CollectViewModel : ObservableObject
{
    private readonly CaseSession _session;
    private readonly Action _onCollected;
    private CancellationTokenSource? _cts;
    private readonly StringBuilder _logText = new();

    public CollectViewModel(CaseSession session, Action onCollected, string? profileName = null)
    {
        _session = session;
        _onCollected = onCollected;
        Ranges =
        [
            new("Since last collection (top-up)", vm => (vm.SelectedProfile?.LastCollectedUtc - CollectionProfile.TopUpOverlap, null)),
            new("Last hour", _ => (DateTime.UtcNow.AddHours(-1), null)),
            new("Last 6 hours", _ => (DateTime.UtcNow.AddHours(-6), null)),
            new("Last 24 hours", _ => (DateTime.UtcNow.AddDays(-1), null)),
            new("Last 7 days", _ => (DateTime.UtcNow.AddDays(-7), null)),
            new("Everything on the servers", _ => (null, null)),
            new("Custom (local time)", vm => (ParseLocal(vm.CustomFrom), ParseLocal(vm.CustomTo)), Custom: true),
        ];
        _selectedRange = Ranges[3];
        var settings = CaseSettings.Load(session.Folder);
        foreach (var p in settings.Profiles) Profiles.Add(p);
        if (profileName is not null && Profiles.FirstOrDefault(p => p.Name == profileName) is { } chosen)
        {
            SelectedProfile = chosen;
            SelectedRange = Ranges[0];
        }
    }

    public ObservableCollection<ServerRowViewModel> Servers { get; } = [];
    public ObservableCollection<GroupRowViewModel> Groups { get; } = [];
    public ObservableCollection<CollectionProfile> Profiles { get; } = [];
    public IReadOnlyList<RangeChoice> Ranges { get; }

    /// <summary>Set by the window: the PasswordBox is not bindable, by design.</summary>
    public Func<string>? PasswordProvider { get; set; }

    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private bool _useAlternateCredentials;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _newServer = "";
    [ObservableProperty] private RangeChoice _selectedRange;
    [ObservableProperty] private string _customFrom = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _customTo = "";
    [ObservableProperty] private bool _includeEventLog;
    [ObservableProperty] private CollectionProfile? _selectedProfile;
    [ObservableProperty] private bool _saveProfile;
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string? _logPath;
    [ObservableProperty] private string _discoveryText = "";

    public bool IsCustomRange => SelectedRange.Custom;

    partial void OnSelectedRangeChanged(RangeChoice value) => OnPropertyChanged(nameof(IsCustomRange));

    partial void OnSelectedProfileChanged(CollectionProfile? value)
    {
        if (value is null) return;
        Servers.Clear();
        foreach (var s in value.Servers)
            Servers.Add(new ServerRowViewModel { Name = s.Name, Host = s.Host, LogFolder = s.LogFolder ?? "" });
        Domain = value.Domain ?? "";
        IncludeEventLog = value.IncludeEventLog;
        ProfileName = value.Name;
        SaveProfile = true;
    }

    // ------------------------------------------------------------------ servers

    [RelayCommand]
    private void AddServer()
    {
        foreach (var raw in NewServer.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var host = raw.Trim();
            var name = host.Split('.')[0].ToUpperInvariant();
            if (Servers.All(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                Servers.Add(new ServerRowViewModel { Name = name, Host = host });
        }
        NewServer = "";
    }

    [RelayCommand]
    private void RemoveExcluded()
    {
        foreach (var s in Servers.Where(s => !s.Include).ToList()) Servers.Remove(s);
    }

    internal void OnGroupToggled(GroupRowViewModel g, bool selected)
    {
        foreach (var m in g.Group.Members)
        {
            var existing = Servers.FirstOrDefault(s => s.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            if (selected && existing is null)
                Servers.Add(new ServerRowViewModel { Name = m.Name, Host = m.DnsHostName ?? m.Name, FromGroups = g.Group.Name });
            else if (!selected && existing is not null && existing.FromGroups == g.Group.Name
                     && !Groups.Any(o => o != g && o.IsSelected && o.Group.Members.Any(x => x.Name == m.Name)))
                Servers.Remove(existing);
        }
    }

    // ------------------------------------------------------------------ actions

    [RelayCommand]
    private async Task Discover()
    {
        var cred = Credential();
        await RunAsync("Discovering replication groups…", log =>
        {
            var result = new LdapDirectoryService(log).Discover(string.IsNullOrWhiteSpace(Domain) ? null : Domain, cred, _cts!.Token);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Groups.Clear();
                foreach (var g in result.Groups) Groups.Add(new GroupRowViewModel(g, this));
                DiscoveryText = $"{result.Groups.Count} replication group(s) in {result.Domain} via {result.DomainController}. Tick groups to add their members.";
            });
        });
    }

    [RelayCommand]
    private async Task Test()
    {
        var targets = Targets();
        if (targets is null) return;
        var cred = Credential();
        var eventLog = IncludeEventLog;
        List<ConnectionTest>? tests = null;
        var domain = string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim();
        await RunAsync("Testing connections…", log =>
        {
            using var access = new ImpersonatingRemoteAccess(log);
            tests = _session.RunAsync((store, _) =>
                new Collector(store, access, new WindowsEventLogSource(), log).Test(targets, cred, eventLog, _cts!.Token, domain)).GetAwaiter().GetResult();
        });
        if (tests is null) return;
        foreach (var t in tests)
            if (Row(t.Server) is { } row)
            {
                row.Status = t.Ok ? "OK" : "FAILED";
                row.Detail = t.ShareDetail + (t.EventLogDetail is null ? "" : "  ·  event log: " + t.EventLogDetail);
            }
    }

    [RelayCommand]
    private Task DryRun() => CollectCore(dryRun: true);

    [RelayCommand]
    private Task Collect() => CollectCore(dryRun: false);

    private async Task CollectCore(bool dryRun)
    {
        var targets = Targets();
        if (targets is null) return;
        var (from, to) = SelectedRange.Range(this);
        if (SelectedRange.Custom && from is null && !string.IsNullOrWhiteSpace(CustomFrom))
        {
            MessageBox.Show("Enter times as yyyy-MM-dd HH:mm (local time).", "Collect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var startedUtc = DateTime.UtcNow;
        var req = new CollectionRequest(targets, from, to, IncludeEventLog, dryRun, Credential(), string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim());
        CollectionReport? report = null;
        await RunAsync(dryRun ? "Dry run…" : "Collecting…", log =>
        {
            var progress = new Progress<string>(p => ProgressText = p);
            using var access = new ImpersonatingRemoteAccess(log);
            report = _session.RunAsync((store, _) =>
                new Collector(store, access, new WindowsEventLogSource(), log).Collect(req, progress, _cts!.Token)).GetAwaiter().GetResult();
        });
        if (report is null) return;
        if (report.FatalError is { } fatal)
            MessageBox.Show(fatal, dryRun ? "Dry run" : "Collect", MessageBoxButton.OK, MessageBoxImage.Error);

        foreach (var s in report.Servers)
            if (Row(s.Server) is { } row)
            {
                row.Status = s.Status.ToString();
                row.Detail = s.Summary;
            }

        if (!dryRun)
        {
            if (SaveProfile && !string.IsNullOrWhiteSpace(ProfileName))
            {
                var settings = CaseSettings.Load(_session.Folder);
                var profile = new CollectionProfile
                {
                    Name = ProfileName.Trim(),
                    Domain = string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim(),
                    Servers = [.. targets],
                    IncludeEventLog = IncludeEventLog,
                    LastCollectedUtc = to ?? startedUtc,
                };
                settings.Upsert(profile);
                settings.Save(_session.Folder);
                Profiles.Clear();
                foreach (var p in settings.Profiles) Profiles.Add(p);
            }
            _onCollected();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenLog()
    {
        if (LogPath is not null && File.Exists(LogPath)) Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ helpers

    private async Task RunAsync(string text, Action<CollectionLog> work)
    {
        IsBusy = true;
        ProgressText = text;
        _cts = new CancellationTokenSource();
        using var log = new CollectionLog(Path.Combine(_session.Folder, "logs"));
        LogPath = log.Path;
        log.Line += line => Application.Current.Dispatcher.InvokeAsync(() => AppendLog(line));
        try
        {
            await Task.Run(() => work(log));
        }
        catch (OperationCanceledException)
        {
            log.Warn("Cancelled.");
        }
        catch (Exception ex)
        {
            log.Error("Failed", ex: ex);
            MessageBox.Show(CollectionLog.Describe(ex), text.TrimEnd('…'), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    private void AppendLog(string line)
    {
        _logText.AppendLine(line);
        Log = _logText.ToString();
    }

    private List<ServerTarget>? Targets()
    {
        var targets = Servers.Where(s => s.Include && !string.IsNullOrWhiteSpace(s.Host)).Select(s => s.ToTarget()).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("Add at least one server (Discover, or type a name and press Add).", "Collect", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return targets;
    }

    private ServerRowViewModel? Row(ServerTarget t) =>
        Servers.FirstOrDefault(s => s.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));

    private NetworkCredential? Credential()
    {
        if (!UseAlternateCredentials || string.IsNullOrWhiteSpace(UserName)) return null;
        var password = PasswordProvider?.Invoke() ?? "";
        var user = UserName.Trim();
        var slash = user.IndexOf('\\');
        return slash > 0 ? new NetworkCredential(user[(slash + 1)..], password, user[..slash]) : new NetworkCredential(user, password);
    }

    private static DateTime? ParseLocal(string text) =>
        DateTime.TryParseExact(text.Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var t) ? t.ToUniversalTime() : null;
}
