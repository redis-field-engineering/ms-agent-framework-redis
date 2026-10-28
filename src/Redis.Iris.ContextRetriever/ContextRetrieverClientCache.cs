using Microsoft.Extensions.AI;
using System.Reflection;
using System.Text.Json;

namespace Redis.Iris.ContextRetriever;

/// <summary>Bounds principal-isolated MCP protocol sessions owned by the connector.</summary>
public sealed class ContextRetrieverClientCache : IAsyncDisposable
{
    private readonly IContextRetrieverClientFactory _factory;
    private readonly ContextRetrieverClientCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries = [];
    private readonly HashSet<CacheEntry> _owned = [];
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Creates a client cache.</summary>
    public ContextRetrieverClientCache(IContextRetrieverClientFactory factory,
        ContextRetrieverClientCacheOptions? options = null, TimeProvider? timeProvider = null,
        CancellationToken shutdownToken = default)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
    }

    /// <summary>Gets a borrowed tool view isolated by endpoint, profile, and credential version.</summary>
    /// <remarks>The cache owns connections. Disposing the borrowed view is a no-op. Each tool call leases a
    /// connection for its duration and reconnects after idle eviction; expired credentials are rejected.</remarks>
    public async Task<IContextRetrieverClient> GetAsync(ContextRetrieverAccessProfile profile,
        ContextRetrieverCredential credential, ContextRetrieverDelegatedToken? delegatedToken,
        ContextRetrieverClientOptions clientOptions, CancellationToken cancellationToken = default)
    {
        using var lease = await AcquireAsync(profile, credential, delegatedToken, clientOptions, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return lease.Entry.View ??= new ClientView(lease.Client.Tools.Select(tool => (AIFunction)new CachedTool(
                tool, this, profile, credential, delegatedToken, clientOptions)).ToArray());
        }
    }

    private async Task<ClientLease> AcquireAsync(ContextRetrieverAccessProfile profile,
        ContextRetrieverCredential credential, ContextRetrieverDelegatedToken? delegatedToken,
        ContextRetrieverClientOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        profile.Validate();
        credential.Validate(_timeProvider);
        if (profile.DelegatedTokenProvider is not null && delegatedToken is null)
            throw new ContextRetrieverAccessDeniedException("The configured delegated token provider returned no token.");
        delegatedToken?.Validate(_timeProvider);
        options.Validate();
        if (profile.Endpoint != options.Endpoint) throw new ArgumentException("Access profile and client endpoint must match.", nameof(options));
        var key = new CacheKey(profile.Endpoint, profile.AccessProfileId, credential.Version, delegatedToken?.Version);
        CacheEntry entry;
        while (true)
        {
            Task? cleanup = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _lifetime.Token.ThrowIfCancellationRequested();
                var now = _timeProvider.GetUtcNow();
                foreach (var pair in _entries.ToArray())
                {
                    if (pair.Value.ExpiresAt <= now || pair.Value.References == 0 && pair.Value.LastAccess <= now - _options.IdleLifetime)
                        Remove(pair.Key, pair.Value);
                }
                var hit = _entries.TryGetValue(key, out entry!);
                if (!hit && _owned.Count >= _options.MaximumClients)
                {
                    var oldest = _entries.Where(pair => pair.Value.References == 0).OrderBy(pair => pair.Value.LastAccess).FirstOrDefault();
                    if (oldest.Value is not null) Remove(oldest.Key, oldest.Value);
                    var retiring = _owned.Where(item => item.Retired).Select(item => item.Disposed.Task).ToArray();
                    if (retiring.Length == 0)
                        throw new ContextRetrieverException("Context Retriever client capacity is occupied by active operations. Try again after an operation completes.");
                    cleanup = Task.WhenAny(retiring);
                }
                if (cleanup is null)
                {
                    ContextRetrieverDiagnostics.Cache(hit);
                    if (!hit)
                    {
                        if (_entries.Keys.Any(existing => existing.Endpoint == key.Endpoint && existing.AccessProfileId == key.AccessProfileId))
                            ContextRetrieverDiagnostics.Reconnect();
                        entry = new(new Lazy<Task<IContextRetrieverClient>>(
                            () => _factory.ConnectAsync(options, credential.AgentKey, delegatedToken?.Token, _lifetime.Token),
                            LazyThreadSafetyMode.ExecutionAndPublication), now, Min(credential.ExpiresAt, delegatedToken?.ExpiresAt));
                        _entries.Add(key, entry);
                        _owned.Add(entry);
                    }
                    entry.LastAccess = now;
                    entry.References++;
                    break;
                }
            }
            // Count retiring/pending connections against capacity until teardown actually finishes.
            await cleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
            credential.Validate(_timeProvider);
            delegatedToken?.Validate(_timeProvider);
        }
        try
        {
            var client = await entry.Client.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            credential.Validate(_timeProvider);
            delegatedToken?.Validate(_timeProvider);
            return new(this, entry, client);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            Release(entry); // Cancel this waiter, not shared initialization.
            throw;
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) Remove(key, entry);
            }
            Release(entry);
            throw;
        }
    }

    // All ownership transitions happen under _gate; teardown never runs under the lock.
    private void Remove(CacheKey key, CacheEntry entry)
    {
        _entries.Remove(key);
        entry.Retired = true;
        QueueCleanup(entry);
    }

    private void Release(CacheEntry entry)
    {
        lock (_gate)
        {
            entry.References--;
            entry.LastAccess = _timeProvider.GetUtcNow();
            QueueCleanup(entry);
        }
    }

    private void QueueCleanup(CacheEntry entry)
    {
        if (!entry.Retired || entry.References != 0 || entry.CleanupStarted) return;
        entry.CleanupStarted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // Also own connections that finish initializing after eviction or waiter cancellation.
                if (entry.Client.IsValueCreated)
                    await (await entry.Client.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch { /* Observe failures and continue disposing every owned connection. */ }
            finally
            {
                lock (_gate) _owned.Remove(entry);
                entry.Disposed.TrySetResult();
            }
        });
    }

    /// <summary>Returns a non-invasive health snapshot without querying customer data.</summary>
    public ContextRetrieverClientCacheHealth GetHealth()
    {
        lock (_gate) return new(_entries.Count, _disposed);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            var owned = _owned.ToArray();
            _entries.Clear();
            foreach (var entry in owned) { entry.Retired = true; QueueCleanup(entry); }
            return new(_disposeTask = FinishShutdownAsync(owned));
        }
    }

    private async Task FinishShutdownAsync(CacheEntry[] owned)
    {
        await Task.Yield(); // Do not invoke cancellation callbacks under _gate.
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (AggregateException) { /* A callback failure must not skip draining owned clients. */ }
        await Task.WhenAll(owned.Select(entry => entry.Disposed.Task)).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static DateTimeOffset? Min(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;
    private sealed record CacheKey(Uri Endpoint, string AccessProfileId, string CredentialVersion, string? DelegatedTokenVersion);
    private sealed class CacheEntry(Lazy<Task<IContextRetrieverClient>> client, DateTimeOffset lastAccess, DateTimeOffset? expiresAt)
    {
        public Lazy<Task<IContextRetrieverClient>> Client { get; } = client;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
        public DateTimeOffset? ExpiresAt { get; } = expiresAt;
        public int References;
        public bool Retired;
        public bool CleanupStarted;
        public IContextRetrieverClient? View;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ClientLease(ContextRetrieverClientCache owner, CacheEntry entry, IContextRetrieverClient client) : IDisposable
    {
        public CacheEntry Entry { get; } = entry;
        public IContextRetrieverClient Client { get; } = client;
        public void Dispose() => owner.Release(Entry);
    }
    private sealed class ClientView(IReadOnlyList<AIFunction> tools) : IContextRetrieverClient
    {
        public IReadOnlyList<AIFunction> Tools { get; } = tools;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class CachedTool(AIFunction schema, ContextRetrieverClientCache owner,
        ContextRetrieverAccessProfile profile, ContextRetrieverCredential credential,
        ContextRetrieverDelegatedToken? token, ContextRetrieverClientOptions options) : AIFunction
    {
        public override string Name => schema.Name;
        public override string Description => schema.Description;
        public override JsonElement JsonSchema => schema.JsonSchema;
        public override JsonElement? ReturnJsonSchema => schema.ReturnJsonSchema;
        public override JsonSerializerOptions JsonSerializerOptions => schema.JsonSerializerOptions;
        public override MethodInfo? UnderlyingMethod => schema.UnderlyingMethod;
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            // Get the token while the owner is still alive, avoiding a disposed CTS race.
            CancellationToken lifetime;
            lock (owner._gate) { ObjectDisposedException.ThrowIf(owner._disposed, owner); lifetime = owner._lifetime.Token; }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
            using var lease = await owner.AcquireAsync(profile, credential, token, options, linked.Token).ConfigureAwait(false);
            var tool = lease.Client.Tools.SingleOrDefault(tool => tool.Name == Name)
                ?? throw new ContextRetrieverDiscoveryException("The previously discovered tool is no longer authorized or available.");
            return await tool.InvokeAsync(arguments, linked.Token).ConfigureAwait(false);
        }
    }
}

/// <summary>Controls bounded Context Retriever client reuse.</summary>
public sealed class ContextRetrieverClientCacheOptions
{
    /// <summary>Maximum cached sessions. A new principal is rejected if every slot has an active operation.</summary>
    public int MaximumClients { get; init; } = 100;
    /// <summary>Maximum idle duration before a session is eligible for eviction on the next acquisition.</summary>
    public TimeSpan IdleLifetime { get; init; } = TimeSpan.FromMinutes(10);
    internal void Validate()
    {
        if (MaximumClients <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumClients));
        if (IdleLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(IdleLifetime));
    }
}

/// <summary>Non-sensitive cache health information.</summary>
public sealed record ContextRetrieverClientCacheHealth(int ActiveClients, bool IsDisposed);
