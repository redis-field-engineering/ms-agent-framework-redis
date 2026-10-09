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
    public async Task DelegatedTokenIsResolvedServerSideAndNeverAddedToAgentState()
    {
        var factory = new DelegatedTokenFactory();
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) => ValueTask.FromResult(
            new ContextRetrieverAccessProfile(
                new Uri("https://context.example.test/mcp"),
                "tenant:user",
                new FixedContextRetrieverCredentialProvider(new("tenant-key", "key-v1")),
                DelegatedTokenProvider: new FixedContextRetrieverDelegatedTokenProvider(new(
                    "signed-user-jwt", "jwt-v1", DateTimeOffset.UtcNow.AddHours(1))))));
        await using var cache = new ContextRetrieverClientCache(factory);
        var provider = new RedisContextRetrieverProvider(resolver, cache);

        await provider.InvokingAsync(new(s_agent, null, new()));

        Assert.Equal("tenant-key", factory.AgentKey);
        Assert.Equal("signed-user-jwt", factory.DelegatedToken);
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

    [Fact]
    public async Task AuthorizationOwnedFieldsAreHiddenAndRejected()
    {
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) =>
            ValueTask.FromResult(new ContextRetrieverAccessProfile(
                new Uri("https://context.example.test/mcp"), "traveler-a",
                new FixedContextRetrieverCredentialProvider(new("secret", "v1")))));
        var inner = AIFunctionFactory.Create(
            (IEnumerable<TagCondition> tag_conditions) => tag_conditions.Count(),
            "filter_traveler");
        await using var cache = new ContextRetrieverClientCache(new FixedToolFactory(inner));
        var provider = new RedisContextRetrieverProvider(resolver, cache, new RedisContextRetrieverOptions
        {
            AuthorizationOwnedFilterFields = ["tenant_id", "traveler_id"],
        });

        var context = await provider.InvokingAsync(new(s_agent, null, new()));
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!));
        var schema = tool.JsonSchema.GetRawText();
        Assert.DoesNotContain("tenant_id", schema);
        Assert.DoesNotContain("traveler_id", schema);

        var denied = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["tag_conditions"] = new[] { new TagCondition("traveler_id", "internal-owner") },
        });
        await Assert.ThrowsAsync<ContextRetrieverAccessDeniedException>(() => tool.InvokeAsync(denied).AsTask());

        var allowed = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["tag_conditions"] = new[] { new TagCondition("status", "confirmed") },
        });
        Assert.Equal("1", (await tool.InvokeAsync(allowed))?.ToString());
    }

    [Fact]
    public async Task TrustedToolArgumentsAreHiddenAndOverrideModelArguments()
    {
        var resolver = new DelegateContextRetrieverAccessResolver((_, _) =>
            ValueTask.FromResult(new ContextRetrieverAccessProfile(
                new Uri("https://context.example.test/mcp"), "traveler-a",
                new FixedContextRetrieverCredentialProvider(new("secret", "v1")),
                new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal)
                {
                    ["get_traveler_by_id"] = new Dictionary<string, object?> { ["id"] = "traveler-001" },
                })));
        var inner = AIFunctionFactory.Create((string id) => id, "get_traveler_by_id");
        await using var cache = new ContextRetrieverClientCache(new FixedToolFactory(inner));
        var provider = new RedisContextRetrieverProvider(resolver, cache);

        var context = await provider.InvokingAsync(new(s_agent, null, new()));
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(context.Tools!));
        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("id", out _));
        Assert.DoesNotContain("id", tool.JsonSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        var supplied = new AIFunctionArguments(new Dictionary<string, object?> { ["id"] = "ram-owner-id" });
        Assert.Equal("traveler-001", (await tool.InvokeAsync(supplied))?.ToString());
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

    private sealed class DelegatedTokenFactory : IContextRetrieverClientFactory
    {
        public string? AgentKey { get; private set; }
        public string? DelegatedToken { get; private set; }

        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Delegated overload expected.");

        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, string? delegatedBearerToken, CancellationToken cancellationToken = default)
        {
            AgentKey = agentKey;
            DelegatedToken = delegatedBearerToken;
            return Task.FromResult<IContextRetrieverClient>(new Client([]));
        }
    }

    private sealed class FixedToolFactory(AIFunction tool) : IContextRetrieverClientFactory
    {
        public Task<IContextRetrieverClient> ConnectAsync(ContextRetrieverClientOptions options, string agentKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<IContextRetrieverClient>(new Client([tool]));
    }

    private sealed record TagCondition(
        [property: System.Text.Json.Serialization.JsonPropertyName("field")] string Field,
        [property: System.Text.Json.Serialization.JsonPropertyName("value")] string Value);

    private sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    }
}
