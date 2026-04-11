namespace VibeSwarm.Client.Services;

/// <summary>
/// Simple time-based cache for service data in Blazor WASM.
/// Scoped services in WASM persist for the app lifetime, so cached data
/// survives page navigations. Returns stale data instantly while refreshing
/// only when the cache expires.
/// </summary>
public class CachedData<T>
{
    private T? _data;
    private DateTime _fetchedAt;
    private Task<T>? _pendingFetch;
    private readonly TimeSpan _staleDuration;

    public CachedData(TimeSpan? staleDuration = null)
    {
        _staleDuration = staleDuration ?? TimeSpan.FromSeconds(60);
    }

    public bool HasData => _data is not null;
    public bool IsStale => DateTime.UtcNow - _fetchedAt > _staleDuration;

    public async Task<T> GetOrFetchAsync(Func<Task<T>> fetchFactory, bool forceRefresh = false)
    {
        if (!forceRefresh && _data is not null && !IsStale)
            return _data;

        // Coalesce concurrent requests — Blazor WASM is single-threaded
        // so this is safe without locks
        if (_pendingFetch is not null)
            return await _pendingFetch;

        _pendingFetch = FetchAndStoreAsync(fetchFactory);
        try
        {
            return await _pendingFetch;
        }
        finally
        {
            _pendingFetch = null;
        }
    }

    private async Task<T> FetchAndStoreAsync(Func<Task<T>> fetchFactory)
    {
        var result = await fetchFactory();
        _data = result;
        _fetchedAt = DateTime.UtcNow;
        return result;
    }

    public void Invalidate() => _fetchedAt = DateTime.MinValue;
}
