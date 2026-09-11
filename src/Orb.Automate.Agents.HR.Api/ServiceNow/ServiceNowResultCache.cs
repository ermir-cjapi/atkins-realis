using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Options;

namespace Orb.Automate.Agents.HR.Api.ServiceNow;

/// <summary>
/// A successful ServiceNow response retained for fresh use and bounded stale fallback.
/// </summary>
public sealed record ServiceNowCacheEntry(ServiceNowSearchResponse Response, DateTimeOffset RetrievedAt);

/// <summary>
/// Privacy-safe, per-process cache and per-key coordination for ServiceNow results.
/// </summary>
public interface IServiceNowResultCache
{
    /// <summary>Creates a one-way process-salted key from normalized search scope.</summary>
    string CreateKey(string normalizedUpn, string normalizedQuery);

    /// <summary>Gets a retained entry, whether fresh or stale.</summary>
    ServiceNowCacheEntry? Get(string key);

    /// <summary>Stores a successful response until its maximum stale age.</summary>
    void Set(string key, ServiceNowSearchResponse response);

    /// <summary>Acquires the asynchronous single-flight gate for a key.</summary>
    ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// Uses a size-limited <see cref="MemoryCache"/> and process-local keyed gates.
/// </summary>
public sealed class ServiceNowResultCache : IServiceNowResultCache, IDisposable
{
    private readonly ServiceNowOptions _settings;
    private readonly TimeProvider _timeProvider;
    private readonly MemoryCache _cache;
    private readonly byte[] _processSalt = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    /// <summary>Creates a ServiceNow result cache.</summary>
    public ServiceNowResultCache(IOptions<ServiceNowOptions> options, TimeProvider timeProvider)
    {
        _settings = options.Value;
        _timeProvider = timeProvider;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _settings.CacheSizeLimit });
    }

    /// <inheritdoc />
    public string CreateKey(string normalizedUpn, string normalizedQuery)
    {
        var scope = string.Join('\n',
            normalizedUpn,
            normalizedQuery,
            _settings.Endpoint.TrimEnd('/').ToUpperInvariant(),
            _settings.SearchPath.Trim().ToUpperInvariant(),
            _settings.CacheVersion);
        var input = Encoding.UTF8.GetBytes(scope);
        var combined = new byte[_processSalt.Length + input.Length];
        Buffer.BlockCopy(_processSalt, 0, combined, 0, _processSalt.Length);
        Buffer.BlockCopy(input, 0, combined, _processSalt.Length, input.Length);
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    /// <inheritdoc />
    public ServiceNowCacheEntry? Get(string key) => _cache.TryGetValue(key, out ServiceNowCacheEntry? entry) ? entry : null;

    /// <inheritdoc />
    public void Set(string key, ServiceNowSearchResponse response)
    {
        var now = _timeProvider.GetUtcNow();
        var live = response with { Source = ServiceNowSearchSource.Live, RetrievedAt = now };
        _cache.Set(
            key,
            new ServiceNowCacheEntry(live, now),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(_settings.CacheMaximumStaleMinutes),
                Size = 1
            });
    }

    /// <inheritdoc />
    public async ValueTask<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(key, static _ => new Gate());
        Interlocked.Increment(ref gate.Users);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            return new GateLease(this, key, gate);
        }
        catch
        {
            ReleaseReference(key, gate);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
        foreach (var gate in _gates.Values)
        {
            gate.Semaphore.Dispose();
        }
    }

    private void ReleaseReference(string key, Gate gate)
    {
        if (Interlocked.Decrement(ref gate.Users) == 0 && _gates.TryRemove(new KeyValuePair<string, Gate>(key, gate)))
        {
            // Do not dispose here: a concurrent GetOrAdd can briefly observe this gate before removal.
            // Once removed and unreferenced, the gate is reclaimed normally.
        }
    }

    private sealed class Gate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users;
    }

    private sealed class GateLease(ServiceNowResultCache owner, string key, Gate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            gate.Semaphore.Release();
            owner.ReleaseReference(key, gate);
        }
    }
}