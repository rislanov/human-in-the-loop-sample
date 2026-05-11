namespace HumanLoopBooking.Services;

// Temporary security state has stricter requirements than a generic cache. The
// operations below map directly to Redis-safe primitives such as SET NX EX,
// INCR+EXPIRE, Lua compare-and-set, and single-use token consumption. The sample
// ships with an in-memory implementation, but production should replace it with
// a Redis-backed implementation that preserves these atomic semantics across
// app replicas.
public interface ITemporarySecurityStateStore
{
    object SyncRoot { get; }

    bool SetIfNotExists<T>(string key, T value, TimeSpan ttl) where T : class;

    T? Get<T>(string key) where T : class;

    bool TryGet<T>(string key, out T? value) where T : class;

    void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class;

    bool CompareAndSet<T>(string key, T expected, T value, TimeSpan ttl) where T : class;

    bool TryConsumeOnce(string key);

    long IncrementWithExpiry(string key, TimeSpan ttl);

    bool Delete(string key);

    IReadOnlyList<T> GetByPrefix<T>(string keyPrefix) where T : class;
}
