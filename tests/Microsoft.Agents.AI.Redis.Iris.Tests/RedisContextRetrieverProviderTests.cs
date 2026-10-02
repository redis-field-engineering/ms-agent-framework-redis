using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public sealed class RedisContextRetrieverProviderTests
{
    private static readonly AIAgent s_agent = new StubAgent();

    [Fact]
    public async Task ResolvesEveryInvocationAndReturnsOnlyAuthorizedToolSet()
    {
        var invocation = 0;
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) =>
        {
            var id = ++invocation == 1 ? "tenant-a" : "tenant-b";
            return ValueTask.FromResult(new ContextRetrieverAccessProfile(
                new Uri("https://context.example.test/mcp"), id,
                new FixedContextRetrieverCredentialProvider(new($"secret-{id}", "v1"))));
        });
        var factory = new ProfileToolFactory();
        await using var cache = new ContextRetrieverClientCache(factory);
        var provider = new RedisContextRetrieverProvider(resolver, cache);

        var first = await provider.InvokingAsync(new(s_agent, null, new()));
        var second = await provider.InvokingAsync(new(s_agent, null, new()));

        Assert.Equal("tool_for_secret_tenant_a", Assert.Single(first.Tools!).Name);
        Assert.Equal("tool_for_secret_tenant_b", Assert.Single(second.Tools!).Name);
        Assert.Empty(provider.StateKeys);
    }

    [Fact]
    public async Task AuthorizationAndDiscoveryFailuresFailClosed()
    {
        var denied = new DelegateContextRetrieverAccessResolver((_, _) =>
            ValueTask.FromException<ContextRetrieverAccessProfile>(new ContextRetrieverAccessDeniedException("denied")));
        await using var cache = new ContextRetrieverClientCache(new ProfileToolFactory());
        var provider = new RedisContextRetrieverProvider(denied, cache);

        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(
            () => provider.InvokingAsync(new(s_agent, null, new())).AsTask());
    }

    private sealed class ProfileToolFactory : IContextRetrieverClientFactory
    {
        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = "tool_for_" + agentKey.Replace('-', '_');
            return Task.FromResult<IContextRetrieverClient>(new Client([AIFunctionFactory.Create(() => "ok", name)]));
        }
    }

    private sealed class Client(IReadOnlyList<AIFunction> tools) : IContextRetrieverClient
    {
        public IReadOnlyList<AIFunction> Tools => tools;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    }
}
