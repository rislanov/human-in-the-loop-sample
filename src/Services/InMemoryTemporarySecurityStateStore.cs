namespace HumanLoopBooking.Services;

// Demo-only process-local implementation of ITemporarySecurityStateStore. It is
// intentionally written with the same operation names a Redis adapter should
// expose, so the booking flow depends on security semantics rather than on a
// vague "cache" abstraction. Replace this class with Redis before running more
// than one app instance.
public sealed class InMemoryTemporarySecurityStateStore : ITemporarySecurityStateStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, CacheEntry> _entries = [];

    public InMemoryTemporarySecurityStateStore(TimeProvider clock)
    {
        _clock = clock;
    }

    // Compound demo workflows still use a process-local lock. A Redis-backed
    // store should replace multi-key critical sections with Lua scripts or
    // transactions, while preserving the single-key primitives below.
    public object SyncRoot => _gate;

    public bool SetIfNotExists<T>(string key, T value, TimeSpan ttl) where T : class
    {
        lock (_gate)
        {
            CleanupExpired();
            if (_entries.ContainsKey(key))
            {
                return false;
            }

            _entries[key] = new CacheEntry(value, _clock.GetUtcNow().Add(ttl));
            return true;
        }
    }

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

    public bool CompareAndSet<T>(string key, T expected, T value, TimeSpan ttl) where T : class
    {
        lock (_gate)
        {
            CleanupExpired();
            if (!_entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Value, expected))
            {
                return false;
            }

            _entries[key] = new CacheEntry(value, _clock.GetUtcNow().Add(ttl));
            return true;
        }
    }

    public bool TryConsumeOnce(string key)
    {
        lock (_gate)
        {
            CleanupExpired();
            if (!_entries.ContainsKey(key))
            {
                return false;
            }

            _entries.Remove(key);
            return true;
        }
    }

    public long IncrementWithExpiry(string key, TimeSpan ttl)
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

            // Mirrors Redis INCR with first-write EXPIRE. Production Redis code
            // should make the increment and expiry creation atomic with Lua or a
            // transaction to avoid immortal rate-limit keys.
            _entries[key] = new CacheEntry(new CounterValue(next), expiresAt);
            return next;
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            return _entries.Remove(key);
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
