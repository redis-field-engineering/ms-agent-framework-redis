using Microsoft.Extensions.AI;
using Xunit;

namespace Redis.Iris.ContextRetriever.Tests;

public sealed class ContextRetrieverClientCacheTests
{
    [Fact]
    public async Task DeduplicatesConcurrentInitializationForTheSamePrincipal()
    {
        var factory = new FakeFactory();
        await using var cache = new ContextRetrieverClientCache(factory);
        var profile = Profile("tenant-a");
        var credential = new ContextRetrieverCredential("secret-a", "v1");

        var clients = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync(profile, credential, null, Options())));

        Assert.Single(clients.Distinct());
        Assert.Equal(1, factory.ConnectCount);
    }

    [Fact]
    public async Task ProfilesAndCredentialVersionsNeverShareProtocolState()
    {
        var factory = new FakeFactory();
        await using var cache = new ContextRetrieverClientCache(factory);

        var a = await cache.GetAsync(Profile("tenant-a"), new("secret-a", "v1"), null, Options());
        var b = await cache.GetAsync(Profile("tenant-b"), new("secret-b", "v1"), null, Options());
        var rotated = await cache.GetAsync(Profile("tenant-a"), new("secret-a-rotated", "v2"), null, Options());

        Assert.NotSame(a, b);
        Assert.NotSame(a, rotated);
        Assert.Equal(3, factory.ConnectCount);
        Assert.DoesNotContain("secret-a", cache.GetHealth().ToString());
    }

    [Fact]
    public async Task DelegatedTokenVersionsNeverShareProtocolState()
    {
        var factory = new FakeFactory();
        await using var cache = new ContextRetrieverClientCache(factory);
        var profile = Profile("same-user");
        var credential = new ContextRetrieverCredential("tenant-key", "tenant-v1");
        var expiry = DateTimeOffset.UtcNow.AddHours(1);

        var first = await cache.GetAsync(profile, credential, new("jwt-one", "jwt-v1", expiry), Options());
        var same = await cache.GetAsync(profile, credential, new("jwt-one", "jwt-v1", expiry), Options());
        var refreshed = await cache.GetAsync(profile, credential, new("jwt-two", "jwt-v2", expiry), Options());

        Assert.Same(first, same);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(["jwt-one", "jwt-two"], factory.DelegatedTokens);
        Assert.DoesNotContain("jwt-one", cache.GetHealth().ToString());
    }

    [Fact]
    public async Task ExpiredDelegatedTokenIsRejectedBeforeConnecting()
    {
        var factory = new FakeFactory();
        await using var cache = new ContextRetrieverClientCache(factory);

        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(() => cache.GetAsync(
            Profile("user"),
            new("tenant-key", "v1"),
            new("expired-jwt", "jwt-v1", DateTimeOffset.UtcNow.AddMinutes(-1)),
            Options()));

        Assert.Equal(0, factory.ConnectCount);
    }

    [Fact]
    public async Task BoundsSizeAndDisposesEvictedAndShutdownClients()
    {
        var factory = new FakeFactory();
        var cache = new ContextRetrieverClientCache(factory, new() { MaximumClients = 1, IdleLifetime = TimeSpan.FromHours(1) });
        await cache.GetAsync(Profile("a"), new("one", "v1"), null, Options());
        var first = factory.Clients.Single();
        await cache.GetAsync(Profile("b"), new("two", "v1"), null, Options());

        Assert.Equal(1, cache.GetHealth().ActiveClients);
        await cache.DisposeAsync();
        Assert.All(factory.Clients, client => Assert.True(client.Disposed));
    }

    [Fact]
    public async Task HostShutdownCancelsInitialization()
    {
        using var shutdown = new CancellationTokenSource();
        await using var cache = new ContextRetrieverClientCache(new BlockingFactory(), shutdownToken: shutdown.Token);
        var pending = cache.GetAsync(Profile("shutdown"), new("secret", "v1"), null, Options());
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, cache.GetHealth().ActiveClients);
    }

    [Fact]
    public async Task OneCancelledWaiterDoesNotCancelOrDuplicateSharedInitialization()
    {
        var factory = new ControlledFactory();
        await using var cache = new ContextRetrieverClientCache(factory);
        using var firstCancellation = new CancellationTokenSource();
        var first = cache.GetAsync(Profile("shared"), new("secret", "v1"), null, Options(), firstCancellation.Token);
        var second = cache.GetAsync(Profile("shared"), new("secret", "v1"), null, Options());

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        factory.Release();
        Assert.NotNull(await second);
        Assert.Equal(1, factory.ConnectCount);
    }

    private static ContextRetrieverAccessProfile Profile(string id) => new(new Uri("https://context.example.test/mcp"), id, new FixedContextRetrieverCredentialProvider(new("unused", "unused")));
    private static ContextRetrieverClientOptions Options() => new() { Endpoint = new Uri("https://context.example.test/mcp") };

    private sealed class FakeFactory : IContextRetrieverClientFactory
    {
        public int ConnectCount;
        public List<FakeClient> Clients { get; } = [];
        public List<string?> DelegatedTokens { get; } = [];
        public async Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ConnectCount);
            await Task.Yield();
            var client = new FakeClient();
            lock (Clients) Clients.Add(client);
            return client;
        }

        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, string? delegatedBearerToken, CancellationToken cancellationToken = default)
        {
            lock (DelegatedTokens) DelegatedTokens.Add(delegatedBearerToken);
            return ConnectAsync(options, agentKey, cancellationToken);
        }
    }

    private sealed class FakeClient : IContextRetrieverClient
    {
        public IReadOnlyList<AIFunction> Tools => [];
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class BlockingFactory : IContextRetrieverClientFactory
    {
        public async Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class ControlledFactory : IContextRetrieverClientFactory
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectCount { get; private set; }
        public void Release() => _release.SetResult();
        public async Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            await _release.Task.WaitAsync(cancellationToken);
            return new FakeClient();
        }
    }
}
