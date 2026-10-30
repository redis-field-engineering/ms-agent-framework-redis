using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisContextRetrieverToolsTests
{
    [Fact]
    public async Task LeaseMakesLifetimeOwnershipExplicit()
    {
        var client = new Client();
        var profile = new ContextRetrieverAccessProfile(new Uri("https://context.example.test/mcp"), "service",
            new FixedContextRetrieverCredentialProvider(new("key", "v1")));

        var lease = await RedisContextRetrieverTools.ConnectAsync(profile, new Factory(client));
        var retained = Assert.Single(lease.Tools);
        await lease.DisposeAsync();

        Assert.True(client.Disposed);
        Assert.Throws<ObjectDisposedException>(() => lease.Tools);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => retained.InvokeAsync(new()).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedToolsRejectExpiredCredentials(bool delegated)
    {
        var client = new Client();
        var expires = DateTimeOffset.UtcNow.AddSeconds(2);
        var profile = new ContextRetrieverAccessProfile(new("https://context.example.test/mcp"), "service",
            new FixedContextRetrieverCredentialProvider(new("key", "v1", delegated ? null : expires)),
            DelegatedTokenProvider: delegated ? new FixedContextRetrieverDelegatedTokenProvider(new("jwt", "v1", expires)) : null);
        await using var lease = await RedisContextRetrieverTools.ConnectAsync(profile, new Factory(client));
        var tool = Assert.Single(lease.Tools);
        await tool.InvokeAsync(new());
        Assert.Equal(1, client.Calls);
        var remaining = expires - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(() => tool.InvokeAsync(new()).AsTask());
        Assert.Equal(1, client.Calls);
    }

    private sealed class Factory(Client client) : IContextRetrieverClientFactory
    {
        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default) => Task.FromResult<IContextRetrieverClient>(client);
        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, string? token, CancellationToken cancellationToken = default) => Task.FromResult<IContextRetrieverClient>(client);
    }

    private sealed class Client : IContextRetrieverClient
    {
        public int Calls { get; private set; }
        public IReadOnlyList<AIFunction> Tools { get; }
        public Client() => Tools = [AIFunctionFactory.Create(() => { Calls++; return "ok"; }, "lookup")];
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
