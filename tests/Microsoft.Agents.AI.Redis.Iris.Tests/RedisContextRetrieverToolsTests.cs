using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public sealed class RedisContextRetrieverToolsTests
{
    [Fact]
    public async Task LeaseMakesLifetimeOwnershipExplicit()
    {
        var client = new Client();
        var profile = new ContextRetrieverAccessProfile(new Uri("https://context.example.test/mcp"), "service",
            new FixedContextRetrieverCredentialProvider(new("key", "v1")));

        var lease = await RedisContextRetrieverTools.ConnectAsync(profile, new Factory(client));
        Assert.Single(lease.Tools);
        await lease.DisposeAsync();

        Assert.True(client.Disposed);
        Assert.Throws<ObjectDisposedException>(() => lease.Tools);
    }

    private sealed class Factory(Client client) : IContextRetrieverClientFactory
    {
        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default) => Task.FromResult<IContextRetrieverClient>(client);
    }

    private sealed class Client : IContextRetrieverClient
    {
        public IReadOnlyList<AIFunction> Tools { get; } = [AIFunctionFactory.Create(() => "ok", "lookup")];
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
