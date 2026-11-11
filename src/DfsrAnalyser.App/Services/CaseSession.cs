using DfsrAnalyser.Core.Query;
using DfsrAnalyser.Core.Storage;

namespace DfsrAnalyser.App.Services;

/// <summary>
/// An open case. The DuckDB connection is not thread-safe, so every piece of work runs on the
/// thread pool one at a time behind a lock; the UI thread never touches the database.
/// </summary>
public sealed class CaseSession : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public CaseStore Store { get; }
    public CaseQuery Query { get; }
    public string Folder => Store.Folder;
    public string Name => Path.GetFileName(Store.Folder.TrimEnd(Path.DirectorySeparatorChar));

    private CaseSession(CaseStore store)
    {
        Store = store;
        Query = new CaseQuery(store);
    }

    public static Task<CaseSession> OpenAsync(string folder) => Task.Run(() => new CaseSession(CaseStore.Open(folder)));

    public async Task<T> RunAsync<T>(Func<CaseStore, CaseQuery, T> work, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => work(Store, Query), ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public Task RunAsync(Action<CaseStore, CaseQuery> work, CancellationToken ct = default) =>
        RunAsync((s, q) => { work(s, q); return true; }, ct);

    public void Dispose()
    {
        _lock.Wait();
        try { Store.Dispose(); }
        finally { _lock.Release(); }
    }
}
