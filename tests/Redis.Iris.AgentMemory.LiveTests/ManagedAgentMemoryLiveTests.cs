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
                new() { FailClosed = true, Search = new() { Limit = 5 } });
            var withRecall = (await memory.InvokingAsync(new(
                s_agent,
                null,
                [new ChatMessage(ChatRole.User, "What seat do I prefer?")]))).ToList();

            // FailClosed makes endpoint failures fail this test. Empty recall is allowed here;
            // the separate populated-memory and promotion gates prove owner isolation and recall.
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
        await using var fixture = new LiveMemoryFixture();
        var firstOwner = Unique("owner-a");
        var secondOwner = Unique("owner-b");
        var topic = Unique("topic");
        var firstId = await fixture.SeedAsync(firstOwner, "Synthetic traveler prefers a window seat.", topic);
        var secondId = await fixture.SeedAsync(secondOwner, "Synthetic traveler prefers an aisle seat.", topic);
        // Establish both positive baselines before either negative assertion: indexing is asynchronous.
        foreach (var (owner, id) in new[] { (firstOwner, firstId), (secondOwner, secondId) })
            await LiveMemoryFixture.WaitForAsync(fixture.Client,
                new() { Text = "", Filter = new() { OwnerId = new() { Eq = owner }, Topics = new() { Eq = topic } } },
                result => result.Items.Any(item => item.Id == id));
        foreach (var (owner, own, foreign) in new[] { (firstOwner, firstId, secondId), (secondOwner, secondId, firstId) })
        {
            var request = new LongTermMemorySearchRequest { Text = "", Filter = new() { OwnerId = new() { Eq = owner }, Topics = new() { Eq = topic } } };
            var results = await LiveMemoryFixture.WaitForAsync(fixture.Client, request, result => result.Items.Any(item => item.Id == own));
            Assert.All(results.Items, item => Assert.Equal(owner, item.OwnerId));
            Assert.DoesNotContain(results.Items, item => item.Id == foreign);
        }
        var provider = new RedisIrisMemoryProvider(new RedisIrisLongTermMemorySearch(fixture.Client),
            _ => new(Unique("recall"), firstOwner, "live-agent"), new() { FailClosed = true, Search = new() { Topics = new() { Eq = topic } } });
        var recalled = (await provider.InvokingAsync(new(s_agent, null, [new ChatMessage(ChatRole.User, "What seat does the traveler prefer?")]))).ToArray();
        Assert.Contains(recalled, message => message.AdditionalProperties?.TryGetValue("redis.iris.memory_ids", out var ids) == true && ((string[])ids!).Contains(firstId));
    }

    [PromotionLiveFact]
    [Trait("Category", "Live")]
    public async Task SessionFactsArePromotedAndRecalledWithOwnerScope()
    {
        await using var fixture = new LiveMemoryFixture();
        var owner = Unique("promotion-owner");
        var session = Unique("promotion-session");
        try
        {
            // A dedicated store must enable extraction with a threshold below this conversation size.
            for (var index = 0; index < 24; index++)
                await AddAsync(fixture.Client, session, owner, SessionEventRole.USER,
                    "Synthetic traveler prefers window seats and vegetarian meals on flights. Please remember these travel preferences.", index);
            var request = new LongTermMemorySearchRequest { Text = "", Limit = 100, Filter = new() { OwnerId = new() { Eq = owner }, SessionId = new() { Eq = session } } };
            var promoted = await LiveMemoryFixture.WaitForAsync(fixture.Client, request, result => result.Items.Count > 0, TimeSpan.FromSeconds(PromotionTimeoutSeconds()));
            TrackOwned(fixture, promoted, owner, session);
            var provider = new RedisIrisMemoryProvider(new RedisIrisLongTermMemorySearch(fixture.Client),
                _ => new(session, owner, "live-agent"), new() { FailClosed = true });
            var messages = (await provider.InvokingAsync(new(s_agent, null, [new ChatMessage(ChatRole.User, "What flight seat and meal do I prefer?")]))).ToArray();
            Assert.Contains(messages, message => message.AdditionalProperties?.ContainsKey("redis.iris.memory_ids") == true);
            var other = await fixture.Client.SearchLongTermMemoryAsync(request with { Filter = new() { OwnerId = new() { Eq = Unique("foreign") }, SessionId = new() { Eq = session } } });
            Assert.Empty(other.Items);
        }
        finally
        {
            await DeleteSessionIfPresentAsync(fixture.Client, session);
            // Find only this run's synthetic owner, including records promoted before a failed assertion.
            var page = await fixture.Client.SearchLongTermMemoryAsync(new() { Text = "", Limit = 100, Filter = new() { OwnerId = new() { Eq = owner }, SessionId = new() { Eq = session } } });
            TrackOwned(fixture, page, owner, session);
            Assert.Null(page.NextPageToken); // More than one page requires explicit cleanup, never silently truncate.
        }
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

    private static void TrackOwned(LiveMemoryFixture fixture, LongTermMemorySearchResult page, string owner, string session)
    {
        // The filter itself is under test. Never trust it to select IDs for a destructive operation.
        fixture.Track(page.Items.Where(item => item.OwnerId == owner && item.SessionId == session).Select(item => item.Id));
        Assert.All(page.Items, item =>
        {
            Assert.Equal(owner, item.OwnerId);
            Assert.Equal(session, item.SessionId);
        });
    }

    private static int PromotionTimeoutSeconds()
    {
        var value = Environment.GetEnvironmentVariable("REDIS_IRIS_PROMOTION_TIMEOUT_SECONDS");
        if (value is null) return 480;
        if (!int.TryParse(value, out var seconds) || seconds is < 30 or > 900)
            throw new ArgumentException("REDIS_IRIS_PROMOTION_TIMEOUT_SECONDS must be between 30 and 900.");
        return seconds;
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

internal sealed class PromotionLiveFactAttribute : FactAttribute
{
    public PromotionLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDIS_IRIS_RUN_PROMOTION_TESTS") != "1")
            Skip = "Set REDIS_IRIS_RUN_PROMOTION_TESTS=1 only for a dedicated store with extraction enabled and a short session threshold.";
    }
}
