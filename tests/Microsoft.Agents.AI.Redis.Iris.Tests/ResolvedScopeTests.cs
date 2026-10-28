using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public class ResolvedScopeTests
{
    static readonly StubAgent Agent = new();

    [Fact]
    public async Task ResolvedScopesReachRecallWritesAndSerializedState()
    {
        var client = new MemoryClient { NamespaceId = "namespace" };
        var tenant = "tenant";
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(o =>
        {
            o.Endpoint = new("https://example.test"); o.StoreId = "store"; o.Credential = "key";
            o.IdentityResolver = _ => new(tenant, "owner", "session", "owner", "agent", "namespace", ["topic"]);
            o.AuthorizationPolicy = _ => true;
            o.Memory.Search = new() { Topics = new() { All = ["configured"] } };
        });
        services.AddSingleton<IRedisAgentMemoryClient>(client);
        using var sp = services.BuildServiceProvider();
        var session = new StubSession();
        var history = sp.GetRequiredService<RedisIrisChatHistoryProvider>();
        var memory = sp.GetRequiredService<RedisIrisMemoryProvider>();
        await memory.InvokingAsync(new(Agent, session, [new(ChatRole.User, "question")]));
        await history.InvokedAsync(new(Agent, session, [new(ChatRole.User, "message")], []));
        Assert.Equal("namespace", client.Search!.Filter!.NamespaceRef!.Eq);
        Assert.Equal(["topic", "configured"], client.Search.Filter.Topics!.All);
        Assert.Equal("namespace", client.Writes.Single().NamespaceRef!.Id);
        Assert.True(session.StateBag.TryGetValue<RedisIrisProviderState>(nameof(RedisIrisMemoryProvider), out var state));
        var restored = JsonSerializer.Deserialize<RedisIrisProviderState>(JsonSerializer.Serialize(state))!;
        Assert.Equal("tenant", restored.TenantId);
        Assert.Equal("namespace", restored.NamespaceId);
        Assert.Equal(["topic"], restored.Topics);
        tenant = "other-tenant";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memory.InvokingAsync(new(Agent, session, [])).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.InvokedAsync(new(Agent, session, [], [])).AsTask());
        tenant = "tenant";
        client.NamespaceId = "different-namespace";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.InvokingAsync(new(Agent, session, [])).AsTask());
    }

    [Fact]
    public async Task ConflictingStaticNamespaceFailsEvenWhenRecallIsFailOpen()
    {
        var client = new MemoryClient();
        var provider = new RedisIrisMemoryProvider(new RedisIrisLongTermMemorySearch(client),
            _ => new("session", "owner", "agent") { NamespaceId = "trusted" },
            new() { Search = new() { NamespaceRef = new() { Eq = "different" } } });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.InvokingAsync(new(Agent, null, [])).AsTask());
        Assert.Null(client.Search);
    }

    [Theory]
    [InlineData("namespace-in", true)]
    [InlineData("namespace-ne", true)]
    [InlineData("namespace-excluded", false)]
    [InlineData("topic-eq", true)]
    [InlineData("topic-in", false)]
    [InlineData("topic-in-implied", true)]
    [InlineData("topic-ne", false)]
    public async Task IntersectionsUseOneOperatorOrRejectBeforeDispatch(string kind, bool allowed)
    {
        var client = new MemoryClient();
        var options = kind switch
        {
            "namespace-in" => new RedisIrisMemorySearchOptions { NamespaceRef = new() { In = ["trusted", "other"] } },
            "namespace-ne" => new() { NamespaceRef = new() { Ne = "other" } },
            "namespace-excluded" => new() { NamespaceRef = new() { Ne = "trusted" } },
            "topic-eq" => new() { Topics = new() { Eq = "billing" } },
            "topic-in" => new() { Topics = new() { In = ["billing", "support"] } },
            "topic-in-implied" => new() { Topics = new() { In = ["private", "support"] } },
            _ => new() { Topics = new() { Ne = "billing" } },
        };
        var provider = new RedisIrisMemoryProvider(new RedisIrisLongTermMemorySearch(client),
            _ => new("session", "owner", "agent") { NamespaceId = "trusted", Topics = ["private"] }, new() { Search = options });
        if (!allowed)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.InvokingAsync(new(Agent, null, [])).AsTask());
            Assert.Null(client.Search);
            return;
        }
        await provider.InvokingAsync(new(Agent, null, []));
        Assert.Equal("trusted", client.Search!.Filter!.NamespaceRef!.Eq);
        Assert.Null(client.Search.Filter.NamespaceRef.In);
        Assert.Null(client.Search.Filter.NamespaceRef.Ne);
        Assert.Contains("private", client.Search.Filter.Topics!.All!);
        Assert.Null(client.Search.Filter.Topics.Eq);
        Assert.Null(client.Search.Filter.Topics.In);
        Assert.Null(client.Search.Filter.Topics.Ne);
        if (kind == "topic-eq") Assert.Contains("billing", client.Search.Filter.Topics.All!);
    }

    sealed class MemoryClient : IRedisAgentMemoryClient
    {
        public int Loads;
        public string? NamespaceId;
        public LongTermMemorySearchRequest? Search;
        public List<AddSessionEventRequest> Writes = [];
        public Task<SessionMemory> GetSessionMemoryAsync(string id, bool includeSummarizedEvents = false, CancellationToken ct = default) { Loads++; return Task.FromResult(new SessionMemory(id, "owner", [], NamespaceRef: NamespaceId is null ? null : new(NamespaceId))); }
        public Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest r, CancellationToken ct = default) { Search = r; return Task.FromResult(new LongTermMemorySearchResult([])); }
        public Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest r, CancellationToken ct = default) { Writes.Add(r); return Task.FromResult(new SessionEvent("1", r.ActorId, r.SessionId!, r.Role, r.Content, r.CreatedAt, r.CreatedAt)); }
        public Task<SessionEvent> GetSessionEventAsync(string a, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionEventAsync(string a, string b, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionMemoryAsync(string a, CancellationToken ct = default) => throw new NotSupportedException();
    }
    sealed class StubSession : AgentSession;
    sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken ct = default) => new(new StubSession());
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession s, JsonSerializerOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement s, JsonSerializerOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> m, AgentSession? s = null, AgentRunOptions? o = null, CancellationToken ct = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> m, AgentSession? s = null, AgentRunOptions? o = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    }
}
