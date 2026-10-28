using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public class PerInvocationAuthorizationTests
{
    static readonly StubAgent Agent = new();
    static readonly Uri Endpoint = new("https://example.test");
    [Fact]
    public async Task RevokedAuthorizationIsRecheckedForExistingSession()
    {
        var client = new MemoryClient();
        var allow = true;
        var checks = 0;
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(o =>
        {
            o.Endpoint = Endpoint; o.StoreId = "store"; o.Credential = "synthetic-key";
            o.IdentityResolver = _ => new("tenant", "owner", "session", "owner", "agent");
            o.AuthorizationPolicy = _ => { checks++; return allow; };
        });
        services.AddSingleton<IRedisAgentMemoryClient>(client);
        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<RedisIrisChatHistoryProvider>();
        var session = new StubSession();
        await provider.InvokingAsync(new(Agent, session, []));
        allow = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.InvokingAsync(new(Agent, session, [])).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.InvokedAsync(new(Agent, session, [new(ChatRole.User, "write")], [])).AsTask());
        Assert.True(checks >= 3);
        Assert.Equal(1, client.Loads);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task RestoredStateCannotBypassAuthorizationOrChangeIdentity()
    {
        var client = new MemoryClient();
        var allow = false;
        var owner = "owner";
        var services = new ServiceCollection();
        services.AddRedisIrisAgentMemory(o =>
        {
            o.Endpoint = Endpoint; o.StoreId = "store"; o.Credential = "key";
            o.IdentityResolver = _ => new("tenant", owner, "session", owner, "agent");
            o.AuthorizationPolicy = _ => allow;
        });
        services.AddSingleton<IRedisAgentMemoryClient>(client);
        using var sp = services.BuildServiceProvider();
        var history = sp.GetRequiredService<RedisIrisChatHistoryProvider>();
        var memory = sp.GetRequiredService<RedisIrisMemoryProvider>();
        var session = new StubSession();
        session.StateBag.SetValue(nameof(RedisIrisChatHistoryProvider), new RedisIrisProviderState("session", "owner", "agent"));
        session.StateBag.SetValue(nameof(RedisIrisMemoryProvider), new RedisIrisProviderState("session", "owner", "agent"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.InvokingAsync(new(Agent, session, [])).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memory.InvokingAsync(new(Agent, session, [])).AsTask());
        allow = true;
        owner = "different-owner";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => history.InvokingAsync(new(Agent, session, [])).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memory.InvokingAsync(new(Agent, session, [])).AsTask());
        Assert.Equal(0, client.Loads);
        Assert.Null(client.Search);
    }

    sealed class MemoryClient : IRedisAgentMemoryClient
    {
        public int Loads;
        public LongTermMemorySearchRequest? Search;
        public List<AddSessionEventRequest> Writes = [];
        public Task<SessionMemory> GetSessionMemoryAsync(string id, bool includeSummarizedEvents = false, CancellationToken ct = default) { Loads++; return Task.FromResult(new SessionMemory(id, "owner", [])); }
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
