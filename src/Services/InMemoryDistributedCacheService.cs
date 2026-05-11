namespace HumanLoopBooking.Services;

public sealed class InMemoryDistributedCacheService : IDistributedCacheService
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, CacheEntry> _entries = [];

    public InMemoryDistributedCacheService(TimeProvider clock)
    {
        _clock = clock;
    }

    // Demo-only process-local lock. A Redis implementation should replace this
    // with atomic commands, transactions, or Lua scripts around multi-key updates.
    public object SyncRoot => _gate;

    public bool TryGet<T>(string key, out T? value) where T : class
    {
        lock (_gate)
        {
            CleanupExpired();
            if (_entries.TryGetValue(key, out var entry) && entry.Value is T typed)
            {
                value = typed;
                return true;
            }

            value = null;
            return false;
        }
    }

    public T? Get<T>(string key) where T : class => TryGet<T>(key, out var value) ? value : null;

    public void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class
    {
        lock (_gate)
        {
            _entries[key] = new CacheEntry(value, ttl is null ? null : _clock.GetUtcNow().Add(ttl.Value));
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    public long Increment(string key, TimeSpan ttl)
    {
        lock (_gate)
        {
            CleanupExpired();

            var now = _clock.GetUtcNow();
            var next = 1L;
            var expiresAt = now.Add(ttl);
            if (_entries.TryGetValue(key, out var entry) && entry.Value is CounterValue counter)
            {
                next = counter.Value + 1;
                expiresAt = entry.ExpiresAt ?? expiresAt;
            }

            // Mirrors Redis INCR + first-write EXPIRE behavior closely enough for the sample.
            _entries[key] = new CacheEntry(new CounterValue(next), expiresAt);
            return next;
        }
    }

    public IReadOnlyList<T> GetByPrefix<T>(string keyPrefix) where T : class
    {
        lock (_gate)
        {
            CleanupExpired();
            return _entries
                .Where(pair => pair.Key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .Select(pair => pair.Value.Value)
                .OfType<T>()
                .ToArray();
        }
    }

    private void CleanupExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _entries
                     .Where(pair => pair.Value.ExpiresAt is { } expiresAt && expiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private sealed record CacheEntry(object Value, DateTimeOffset? ExpiresAt);

    private sealed record CounterValue(long Value);
}
