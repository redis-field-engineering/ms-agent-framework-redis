using System.Collections.Concurrent;

namespace Redis.Iris.ContextRetriever;

/// <summary>Bounds principal-isolated MCP protocol sessions owned by the connector.</summary>
public sealed class ContextRetrieverClientCache : IAsyncDisposable
{
    private readonly IContextRetrieverClientFactory _factory;
    private readonly ContextRetrieverClientCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _shutdownToken;
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _entries = new();
    private int _disposed;

    /// <summary>Creates a client cache.</summary>
    public ContextRetrieverClientCache(
        IContextRetrieverClientFactory factory,
        ContextRetrieverClientCacheOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken shutdownToken = default)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _shutdownToken = shutdownToken;
    }

    /// <summary>Gets or initializes a session isolated by endpoint, access profile, and credential version.</summary>
    public async Task<IContextRetrieverClient> GetAsync(
        ContextRetrieverAccessProfile profile,
        ContextRetrieverCredential credential,
        ContextRetrieverDelegatedToken? delegatedToken,
        ContextRetrieverClientOptions clientOptions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        profile.Validate();
        if (profile.DelegatedTokenProvider is not null && delegatedToken is null)
            throw new ContextRetrieverAccessDeniedException("The configured delegated token provider returned no token.");
        credential.Validate(_timeProvider);
        delegatedToken?.Validate(_timeProvider);
        if (profile.Endpoint != clientOptions.Endpoint) throw new ArgumentException("Access profile and client endpoint must match.", nameof(clientOptions));

        await EvictExpiredAsync().ConfigureAwait(false);
        var key = new CacheKey(profile.Endpoint, profile.AccessProfileId, credential.Version, delegatedToken?.Version);
        var now = _timeProvider.GetUtcNow();
        var expiresAt = Min(credential.ExpiresAt, delegatedToken?.ExpiresAt);
        var candidate = new CacheEntry(
            new Lazy<Task<IContextRetrieverClient>>(
                () => _factory.ConnectAsync(clientOptions, credential.AgentKey, delegatedToken?.Token, _shutdownToken),
                LazyThreadSafetyMode.ExecutionAndPublication),
            now,
            expiresAt);
        var entry = _entries.GetOrAdd(key, candidate);
        var hit = !ReferenceEquals(entry, candidate);
        ContextRetrieverDiagnostics.Cache(hit);
        if (!hit && _entries.Keys.Any(existing => existing.Endpoint == key.Endpoint && existing.AccessProfileId == key.AccessProfileId && existing != key))
        {
            ContextRetrieverDiagnostics.Reconnect();
        }
        entry.LastAccess = now;

        try
        {
            var client = await entry.Client.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            await TrimAsync(key).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_shutdownToken.IsCancellationRequested)
        {
            // Cancel only this waiter. Shared initialization remains cached for other invocations.
            throw;
        }
        catch
        {
            _entries.TryRemove(new KeyValuePair<CacheKey, CacheEntry>(key, entry));
            if (entry.Client.IsValueCreated && entry.Client.Value.IsCompletedSuccessfully)
            {
                await entry.Client.Value.Result.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Returns a non-invasive health snapshot without querying customer data.</summary>
    public ContextRetrieverClientCacheHealth GetHealth() => new(_entries.Count, Volatile.Read(ref _disposed) != 0);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var entries = _entries.ToArray();
        _entries.Clear();
        foreach (var pair in entries)
        {
            if (!pair.Value.Client.IsValueCreated) continue;
            try { await (await pair.Value.Client.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort shutdown of every independently owned session */ }
        }
    }

    private async Task EvictExpiredAsync()
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.IdleLifetime;
        foreach (var pair in _entries)
        {
            if (pair.Value.LastAccess > cutoff && (pair.Value.ExpiresAt is null || pair.Value.ExpiresAt > _timeProvider.GetUtcNow())) continue;
            if (!_entries.TryRemove(pair)) continue;
            await DisposeCompletedAsync(pair.Value).ConfigureAwait(false);
        }
    }

    private async Task TrimAsync(CacheKey current)
    {
        while (_entries.Count > _options.MaximumClients)
        {
            var oldest = _entries.Where(pair => pair.Key != current).OrderBy(pair => pair.Value.LastAccess).FirstOrDefault();
            if (oldest.Value is null || !_entries.TryRemove(oldest)) return;
            await DisposeCompletedAsync(oldest.Value).ConfigureAwait(false);
        }
    }

    private static async Task DisposeCompletedAsync(CacheEntry entry)
    {
        if (entry.Client.IsValueCreated && entry.Client.Value.IsCompletedSuccessfully)
        {
            await entry.Client.Value.Result.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static DateTimeOffset? Min(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;

    private sealed record CacheKey(Uri Endpoint, string AccessProfileId, string CredentialVersion, string? DelegatedTokenVersion);
    private sealed class CacheEntry(Lazy<Task<IContextRetrieverClient>> client, DateTimeOffset lastAccess, DateTimeOffset? expiresAt)
    {
        public Lazy<Task<IContextRetrieverClient>> Client { get; } = client;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
        public DateTimeOffset? ExpiresAt { get; } = expiresAt;
    }
}

/// <summary>Controls bounded Context Retriever client reuse.</summary>
public sealed class ContextRetrieverClientCacheOptions
{
    /// <summary>Maximum number of independently authorized sessions.</summary>
    public int MaximumClients { get; init; } = 100;
    /// <summary>Maximum idle duration before a session is evicted.</summary>
    public TimeSpan IdleLifetime { get; init; } = TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (MaximumClients <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumClients));
        if (IdleLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(IdleLifetime));
    }
}

/// <summary>Non-sensitive cache health information.</summary>
public sealed record ContextRetrieverClientCacheHealth(int ActiveClients, bool IsDisposed);
