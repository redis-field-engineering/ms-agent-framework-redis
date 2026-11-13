// Copyright (c) Redis, Inc.

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

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

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(3, false, true)]
    [InlineData(2, false, false)]
    [InlineData(null, true, false)]
    public async Task ToolHistoryRoundTripsAndIncompleteExchangesUseText(int? limit, bool missingResult, bool expectNative)
    {
        var client = new RecordingClient();
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var provider = new RedisIrisChatHistoryProvider(client, _ => new("session-a", "owner-a", "agent-a"),
            mapper, new() { MaxMessagesToRetrieve = limit });
        List<ChatMessage> response =
        [
            new(ChatRole.Assistant,
            [
                new TextContent("Checking."),
                new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["id"] = 1 }),
                new FunctionCallContent("call-2", "lookup", new Dictionary<string, object?> { ["id"] = 2 }),
            ]),
            new(ChatRole.Tool, missingResult
                ? [new FunctionResultContent("call-1", "first")]
                : [new FunctionResultContent("call-2", new { value = "second" }), new FunctionResultContent("call-1", "first")]),
            new(ChatRole.Assistant, "Done."),
        ];
        await provider.InvokedAsync(new(s_agent, null, [new(ChatRole.User, "Look up both.")], response));
        Assert.Equal(4, client.Added.Count);
        client.Memory = new("session-a", "owner-a", client.Added.Select((request, index) =>
            new SessionEvent(index.ToString(), request.ActorId, request.SessionId!, request.Role, request.Content,
                request.CreatedAt, DateTimeOffset.UtcNow.AddSeconds(index), request.Metadata)).ToList());

        // Load through a new provider, as on a resumed conversation.
        var reader = new RedisIrisChatHistoryProvider(client, _ => new("session-a", "owner-a", "agent-a"),
            mapper, new() { MaxMessagesToRetrieve = limit });
        var restored = (await reader.InvokingAsync(new(s_agent, null, [new(ChatRole.User, "Next question.")]))).ToList();
        Assert.Equal((limit ?? 4) + 1, restored.Count);
        Assert.Equal("Next question.", restored[^1].Text);
        if (expectNative)
        {
            Assert.Equal(2, restored[^4].Contents.OfType<FunctionCallContent>().Count());
            Assert.Equal(ChatRole.Tool, restored[^3].Role);
            Assert.Equal(["call-2", "call-1"], restored[^3].Contents.OfType<FunctionResultContent>().Select(part => part.CallId));
        }
        else
        {
            Assert.All(restored.SelectMany(message => message.Contents), content => Assert.IsType<TextContent>(content));
            Assert.Contains("Tool result", restored[^3].Text);
        }
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("duplicate")]
    [InlineData("interrupted")]
    public async Task InvalidToolExchangesRemainReadableText(string scenario)
    {
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        List<ChatMessage> messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent("call", "lookup", null)]),
            new(ChatRole.Tool, [new FunctionResultContent(scenario == "wrong" ? "other" : "call", "value")]),
        ];
        if (scenario == "duplicate") messages.Add(new(ChatRole.Tool, [new FunctionResultContent("call", "again")]));
        if (scenario == "interrupted") messages.Insert(1, new(ChatRole.User, "interruption"));
        var events = messages.Select((message, index) =>
        {
            var request = mapper.ToSessionEventRequest(message, "session-a", "owner-a", "agent-a")!;
            return new SessionEvent(index.ToString(), request.ActorId, "session-a", request.Role, request.Content,
                request.CreatedAt, request.CreatedAt, request.Metadata);
        }).ToList();
        var client = new RecordingClient { Memory = new("session-a", "owner-a", events) };
        var provider = new RedisIrisChatHistoryProvider(client, _ => new("session-a", "owner-a", "agent-a"), mapper);
        var result = await provider.InvokingAsync(new(s_agent, null, []));
        Assert.All(result.SelectMany(message => message.Contents), content => Assert.IsType<TextContent>(content));
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
    public async Task MissingSessionIsTreatedAsEmptyHistory()
    {
        var client = new RecordingClient
        {
            Exception = new AgentMemoryException(System.Net.HttpStatusCode.NotFound, "session.get"),
        };
        var provider = Provider(client, new("new-session", "owner-a", "agent-a"));

        var result = await provider.InvokingAsync(new(s_agent, null, [new(ChatRole.User, "first turn")]));

        Assert.Equal("first turn", Assert.Single(result).Text);
    }

    [Fact]
    public void ProviderStateRoundTripsThroughJson()
    {
        var state = new RedisIrisProviderState("session-a", "owner-a", "agent-a", "traveler-001");

        var restored = JsonSerializer.Deserialize<RedisIrisProviderState>(JsonSerializer.Serialize(state));

        Assert.NotNull(restored);
        Assert.Equal(state.SessionId, restored.SessionId);
        Assert.Equal(state.OwnerId, restored.OwnerId);
        Assert.Equal(state.AgentId, restored.AgentId);
        Assert.Equal(state.ActorId, restored.ActorId);
    }

    [Fact]
    public async Task LoadedUserHistoryUsesCanonicalActorInsteadOfLegacyStorageOwner()
    {
        var client = new RecordingClient
        {
            Memory = new("session-a", "17-tenant-12-traveler-001",
            [
                Event("1", "session-a", "17-tenant-12-traveler-001", SessionEventRole.USER, "hello", 1),
            ]),
        };
        var state = new RedisIrisProviderState(
            "session-a", "17-tenant-12-traveler-001", "agent-a", "traveler-001");
        var provider = Provider(client, state);

        var result = await provider.InvokingAsync(new(s_agent, null, []));

        Assert.Equal("traveler-001", Assert.Single(result).AuthorName);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SummaryIsBoundedUntrustedContextBeforeRecentEvents(bool includeSummary)
    {
        var client = new RecordingClient
        {
            Memory = new("session", "owner", [Event("recent", "session", "owner", SessionEventRole.USER, "recent event", 1)],
                Summary: new("older context that is long", "old", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }), 20)),
        };
        var provider = new RedisIrisChatHistoryProvider(client, _ => new("session", "owner", "agent"),
            options: new() { IncludeSessionSummary = includeSummary, MaxSummaryCharacters = 5, MaxMessagesToRetrieve = 1 });
        var messages = (await provider.InvokingAsync(new(s_agent, null, [new(ChatRole.User, "current")]))).ToList();
        Assert.Equal(includeSummary ? 3 : 2, messages.Count);
        if (includeSummary)
        {
            Assert.Equal(ChatRole.User, messages[0].Role);
            Assert.Contains("untrusted remembered context", messages[0].Text);
            Assert.EndsWith("older", messages[0].Text);
            Assert.True((bool)messages[0].AdditionalProperties!["redis.iris.summary"]!);
        }
        Assert.Equal("recent event", messages[^2].Text);
        Assert.Equal("current", messages[^1].Text);
    }

    [Fact]
    public async Task SummaryOnlySessionStillProvidesHistoricalContext()
    {
        var client = new RecordingClient
        {
            Memory = new("session", "owner", [],
            Summary: new("earlier facts", "old", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { }), 20))
        };
        var provider = Provider(client, new("session", "owner", "agent"));
        Assert.Contains("earlier facts", Assert.Single(await provider.InvokingAsync(new(s_agent, null, []))).Text);
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
