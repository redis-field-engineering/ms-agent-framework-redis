using Microsoft.Extensions.AI;
using Xunit;

namespace Redis.Iris.ContextRetriever.Tests;

public sealed class ContextRetrieverCacheLifetimeTests
{
    private static readonly Uri Endpoint = new("https://context.example.test/mcp");
    private static ContextRetrieverAccessProfile Profile(string id) => new(Endpoint, id, new FixedContextRetrieverCredentialProvider(new("unused", "v1")));
    private static Task<IContextRetrieverClient> Get(ContextRetrieverClientCache cache, string id, CancellationToken ct = default) =>
        cache.GetAsync(Profile(id), new(id, "v1"), null, new() { Endpoint = Endpoint }, ct);

    [Fact]
    public async Task ActiveCallsKeepTheirConnectionAndRetainedToolsReconnectAfterEviction()
    {
        var factory = new Factory();
        await using var cache = new ContextRetrieverClientCache(factory, new() { MaximumClients = 1 });
        var first = await Get(cache, "a");
        var original = factory.Clients.Single();
        original.Block = true;
        var tool = Assert.Single(first.Tools);
        var call = tool.InvokeAsync(new()).AsTask();
        await original.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ContextRetrieverException>(() => Get(cache, "b"));
        Assert.False(original.Disposed);
        original.Release.TrySetResult();
        Assert.Equal("a", (await call)?.ToString());
        await Get(cache, "b");
        Assert.True(original.Disposed);
        // This is the original model-facing tool, retained across an idle eviction.
        Assert.Equal("a", (await tool.InvokeAsync(new()))?.ToString());
        Assert.Equal(3, factory.Clients.Count);
    }

    [Fact]
    public async Task EvictedPendingInitializationIsDisposedBeforeCapacityIsReused()
    {
        var factory = new Factory { DelayFirst = true };
        await using var cache = new ContextRetrieverClientCache(factory, new() { MaximumClients = 1 });
        using var cancellation = new CancellationTokenSource();
        var pending = Get(cache, "a", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var second = Get(cache, "b");
        Assert.Single(factory.Clients);
        factory.Initialize.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(factory.Clients[0].Disposed);
        Assert.Equal(2, factory.Clients.Count);
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsActiveCallsBeforeDisposingConnections()
    {
        var factory = new Factory();
        var cache = new ContextRetrieverClientCache(factory);
        var client = await Get(cache, "a");
        var raw = factory.Clients.Single();
        raw.Block = true;
        var call = Assert.Single(client.Tools).InvokeAsync(new()).AsTask();
        await raw.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cache.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(raw.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Get(cache, "b"));
        await cache.DisposeAsync();
    }

    private sealed class Factory : IContextRetrieverClientFactory
    {
        public bool DelayFirst;
        public List<Client> Clients { get; } = [];
        public TaskCompletionSource Initialize { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string key, CancellationToken ct = default)
        {
            var client = new Client(key);
            Clients.Add(client);
            if (DelayFirst && Clients.Count == 1) await Initialize.Task.WaitAsync(ct);
            return client;
        }
    }
    private sealed class Client : IContextRetrieverClient
    {
        public bool Block;
        public bool Disposed;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AIFunction> Tools { get; }
        public Client(string id)
        {
            Tools = [AIFunctionFactory.Create(async (CancellationToken ct) =>
            {
                Started.TrySetResult();
                if (Block) await Release.Task.WaitAsync(ct);
                ObjectDisposedException.ThrowIf(Disposed, this);
                return id;
            }, "read")];
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
