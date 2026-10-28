// Copyright (c) Redis, Inc.

using System.Net;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Redis.Iris;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentMemory.LiveTests;

public sealed class ManagedAgentMemoryLiveTests
{
    private static readonly AIAgent s_agent = new StubAgent();

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task SessionEventLifecycleSupportsMultipleRolesAndDeletion()
    {
        var client = LiveConfiguration.CreateClient();
        var sessionId = Unique("session");
        var ownerId = Unique("owner");
        var agentId = Unique("agent");
        var sessionDeleted = false;
        try
        {
            var first = await AddAsync(client, sessionId, ownerId, SessionEventRole.USER, "My favorite color is vermilion.", 0);
            var second = await AddAsync(client, sessionId, agentId, SessionEventRole.ASSISTANT, "I will remember that preference.", 1);
            var third = await AddAsync(client, sessionId, ownerId, SessionEventRole.USER, "What color did I mention?", 2);

            var individual = await client.GetSessionEventAsync(sessionId, second.EventId);
            var session = await client.GetSessionMemoryAsync(sessionId);

            Assert.Equal(second.EventId, individual.EventId);
            Assert.Equal(second.SessionId, individual.SessionId);
            Assert.Equal(second.ActorId, individual.ActorId);
            Assert.Equal(second.Role, individual.Role);
            Assert.Equal(second.Content.Select(static part => part.Text), individual.Content.Select(static part => part.Text));
            Assert.Equal(ownerId, session.OwnerId);
            Assert.Equal([first.EventId, second.EventId, third.EventId], session.Events.Select(static item => item.EventId));
            Assert.Equal([SessionEventRole.USER, SessionEventRole.ASSISTANT, SessionEventRole.USER], session.Events.Select(static item => item.Role));

            await client.DeleteSessionEventAsync(sessionId, second.EventId);
            var afterEventDelete = await client.GetSessionMemoryAsync(sessionId);
            Assert.Equal([first.EventId, third.EventId], afterEventDelete.Events.Select(static item => item.EventId));

            await client.DeleteSessionMemoryAsync(sessionId);
            sessionDeleted = true;
            var exception = await Assert.ThrowsAsync<AgentMemoryException>(() => client.GetSessionMemoryAsync(sessionId));
            Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        }
        finally
        {
            if (!sessionDeleted)
            {
                await DeleteSessionIfPresentAsync(client, sessionId);
            }
        }
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task AgentFrameworkProvidersPersistReconstructAndSearchWithOwnerScope()
    {
        var client = LiveConfiguration.CreateClient();
        var sessionId = Unique("provider-session");
        var ownerId = Unique("provider-owner");
        var agentId = Unique("provider-agent");
        var state = new RedisIrisProviderState(sessionId, ownerId, agentId);
        try
        {
            var writer = new RedisIrisSessionEventWriter(client, RedisIrisEventWriter.ChatHistoryProvider);
            var history = new RedisIrisChatHistoryProvider(client, _ => state, eventWriter: writer);
            await history.InvokedAsync(new(
                s_agent,
                null,
                [new ChatMessage(ChatRole.User, "I prefer window seats on flights.")],
                [new ChatMessage(ChatRole.Assistant, "Understood.")]));

            // Recreate the provider to prove reconstruction comes from Redis rather than process memory.
            var recreatedHistory = new RedisIrisChatHistoryProvider(client, _ => state, eventWriter: writer);
            var reconstructed = (await recreatedHistory.InvokingAsync(new(
                s_agent,
                null,
                [new ChatMessage(ChatRole.User, "What seat do I prefer?")]))).ToList();

            Assert.Equal(3, reconstructed.Count);
            Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], reconstructed.Select(static item => item.Role));
            Assert.Equal("I prefer window seats on flights.", reconstructed[0].Text);
            Assert.Equal("Understood.", reconstructed[1].Text);

            var memory = new RedisIrisMemoryProvider(
                new RedisIrisLongTermMemorySearch(client),
                _ => state,
                new() { Search = new() { Limit = 5 } });
            var withRecall = (await memory.InvokingAsync(new(
                s_agent,
                null,
                [new ChatMessage(ChatRole.User, "What seat do I prefer?")]))).ToList();

            // Promotion is asynchronous. This verifies the real search endpoint and mandatory owner scope;
            // a newly created session is allowed to have no promoted memories yet.
            Assert.NotEmpty(withRecall);
            Assert.Equal("What seat do I prefer?", withRecall[0].Text);
        }
        finally
        {
            await DeleteSessionIfPresentAsync(client, sessionId);
        }
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task LongTermMemorySearchKeepsUniqueOwnersIsolated()
    {
        var client = LiveConfiguration.CreateClient();
        var firstOwner = Unique("search-owner-a");
        var secondOwner = Unique("search-owner-b");
        var search = new RedisIrisLongTermMemorySearch(client);

        var first = await search.SearchAsync(firstOwner, "integration test", new() { Limit = 10 });
        var second = await search.SearchAsync(secondOwner, "integration test", new() { Limit = 10 });

        Assert.All(first.Items, item => Assert.Equal(firstOwner, item.OwnerId));
        Assert.All(second.Items, item => Assert.Equal(secondOwner, item.OwnerId));
        Assert.Empty(first.Items.Select(static item => item.Id).Intersect(second.Items.Select(static item => item.Id)));
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task InvalidBearerCredentialIsRejected()
    {
        var configuration = LiveConfiguration.Load();
        var client = configuration.CreateClient(configuration.ApiKey + "-invalid");

        var exception = await Assert.ThrowsAsync<AgentMemoryException>(() =>
            client.GetSessionMemoryAsync(Unique("unauthorized-session")));

        Assert.True(exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    private static Task<SessionEvent> AddAsync(
        IRedisAgentMemoryClient client,
        string sessionId,
        string actorId,
        SessionEventRole role,
        string text,
        int seconds) =>
        client.AddSessionEventAsync(new()
        {
            SessionId = sessionId,
            ActorId = actorId,
            Role = role,
            Content = [new(text)],
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(seconds),
        });

    private static async Task DeleteSessionIfPresentAsync(IRedisAgentMemoryClient client, string sessionId)
    {
        try
        {
            await client.DeleteSessionMemoryAsync(sessionId);
        }
        catch (AgentMemoryException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    private static string Unique(string prefix) => $"dotnet-live-{prefix}-{Guid.NewGuid():N}";

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

internal sealed record LiveConfiguration(Uri Endpoint, string StoreId, string ApiKey)
{
    public static LiveConfiguration Load() => new(
        new Uri(Environment.GetEnvironmentVariable("REDIS_IRIS_ENDPOINT")!),
        Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID")!,
        Environment.GetEnvironmentVariable("REDIS_IRIS_API_KEY")!);

    public static RedisAgentMemoryClient CreateClient() => Load().CreateClient();

    public RedisAgentMemoryClient CreateClient(string? apiKey = null) => new(
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }),
        new() { Endpoint = this.Endpoint, StoreId = this.StoreId },
        new FixedAgentMemoryCredentialProvider(apiKey ?? this.ApiKey));
}

internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_IRIS_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_IRIS_API_KEY")))
        {
            this.Skip = "Set REDIS_IRIS_ENDPOINT, REDIS_IRIS_STORE_ID, and REDIS_IRIS_API_KEY to run live tests.";
        }
    }
}
