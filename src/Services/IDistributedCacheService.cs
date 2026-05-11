namespace HumanLoopBooking.Services;

public interface IDistributedCacheService
{
    object SyncRoot { get; }

    bool TryGet<T>(string key, out T? value) where T : class;

    T? Get<T>(string key) where T : class;

    void Set<T>(string key, T value, TimeSpan? ttl = null) where T : class;

    void Remove(string key);

    long Increment(string key, TimeSpan ttl);

    IReadOnlyList<T> GetByPrefix<T>(string keyPrefix) where T : class;
}
