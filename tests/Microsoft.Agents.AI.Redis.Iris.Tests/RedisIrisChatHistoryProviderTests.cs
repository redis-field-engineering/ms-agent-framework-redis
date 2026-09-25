// Copyright (c) Redis, Inc.

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris.Tests;

public sealed class RedisIrisChatHistoryProviderTests
{
    private static readonly AIAgent s_agent = new StubAgent();

    [Fact]
    public async Task InvokingAsyncLoadsChronologicalBoundedHistory()
    {
        var client = new RecordingClient
        {
            Memory = new("session-a", "owner-a",
            [
                Event("2", "session-a", "owner-a", SessionEventRole.USER, "second", 2),
                Event("1", "session-a", "owner-a", SessionEventRole.USER, "first", 1),
                Event("3", "session-a", "agent-a", SessionEventRole.ASSISTANT, "third", 3),
            ]),
        };
        var provider = Provider(client, new("session-a", "owner-a", "agent-a"), maxMessages: 2);

        var result = await provider.InvokingAsync(new(s_agent, null, [new(ChatRole.User, "current")]));

        Assert.Equal(["second", "third", "current"], result.Select(static message => message.Text));
    }

    [Fact]
    public async Task InvokedAsyncStoresOnlyExternalRequestAndResponseMessages()
    {
        var client = new RecordingClient();
        var provider = Provider(client, new("session-a", "owner-a", "agent-a"));
        var loadedHistory = new ChatMessage(ChatRole.User, "old").WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory);
        var injectedContext = new ChatMessage(ChatRole.User, "memory").WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider);

        await provider.InvokedAsync(new(s_agent, null,
            [loadedHistory, injectedContext, new(ChatRole.User, "new")],
            [new(ChatRole.Assistant, "answer")]));

        Assert.Equal(["new", "answer"], client.Added.Select(static request => request.Content.Single().Text));
    }

    [Fact]
    public async Task InvokingAsyncRejectsAStoredSessionOwnedBySomeoneElse()
    {
        var client = new RecordingClient { Memory = new("session-a", "attacker", []) };
        var provider = Provider(client, new("session-a", "owner-a", "agent-a"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await provider.InvokingAsync(new(s_agent, null, [])).AsTask());
    }

    [Fact]
    public async Task HistoryLoadCanFailOpen()
    {
        var client = new RecordingClient { Exception = new HttpRequestException("unavailable") };
        var provider = new RedisIrisChatHistoryProvider(
            client,
            _ => new("session-a", "owner-a", "agent-a"),
            options: new() { LoadFailurePolicy = RedisIrisFailurePolicy.FailOpen });

        var result = await provider.InvokingAsync(new(s_agent, null, [new(ChatRole.User, "current")]));

        Assert.Single(result);
    }

    [Fact]
    public void ProviderStateRoundTripsThroughJson()
    {
        var state = new RedisIrisProviderState("session-a", "owner-a", "agent-a");

        var restored = JsonSerializer.Deserialize<RedisIrisProviderState>(JsonSerializer.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(state.SessionId, restored.SessionId);
        Assert.Equal(state.OwnerId, restored.OwnerId);
        Assert.Equal(state.AgentId, restored.AgentId);
    }

    [Fact]
    public async Task ConcurrentSessionsKeepIndependentIdentifiers()
    {
        var client = new RecordingClient();
        var sequence = 0;
        var provider = new RedisIrisChatHistoryProvider(client, _ =>
        {
            var id = Interlocked.Increment(ref sequence).ToString();
            return new($"session-{id}", $"owner-{id}", "agent");
        });

        await Task.WhenAll(
            provider.InvokedAsync(new(s_agent, null, [new ChatMessage(ChatRole.User, "one")], [])).AsTask(),
            provider.InvokedAsync(new(s_agent, null, [new ChatMessage(ChatRole.User, "two")], [])).AsTask());

        Assert.Equal(2, client.Added.Select(static request => request.SessionId).Distinct().Count());
        Assert.Equal(2, client.Added.Select(static request => request.ActorId).Distinct().Count());
    }

    private static RedisIrisChatHistoryProvider Provider(RecordingClient client, RedisIrisProviderState state, int? maxMessages = null) =>
        new(client, _ => state, options: new() { MaxMessagesToRetrieve = maxMessages });

    private static SessionEvent Event(string id, string sessionId, string actorId, SessionEventRole role, string text, int second) =>
        new(id, actorId, sessionId, role, [new(text)], new DateTimeOffset(2026, 1, 1, 0, 0, second, TimeSpan.Zero), new DateTimeOffset(2026, 1, 1, 0, 0, second, TimeSpan.Zero));

    private sealed class RecordingClient : IRedisAgentMemoryClient
    {
        private readonly object _gate = new();
        public SessionMemory Memory { get; set; } = new("missing", "owner", []);
        public Exception? Exception { get; set; }
        public List<AddSessionEventRequest> Added { get; } = [];

        public Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default)
        {
            lock (this._gate)
            {
                this.Added.Add(request);
            }

            return Task.FromResult(Event(Guid.NewGuid().ToString(), request.SessionId!, request.ActorId, request.Role, request.Content.Single().Text, 1));
        }

        public Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) =>
            this.Exception is null ? Task.FromResult(this.Memory) : Task.FromException<SessionMemory>(this.Exception);
        public Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSession : AgentSession;

    private sealed class StubAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new StubSession());
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
