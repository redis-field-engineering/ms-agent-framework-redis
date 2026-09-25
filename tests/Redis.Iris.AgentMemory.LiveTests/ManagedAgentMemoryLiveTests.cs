// Copyright (c) Redis, Inc.

using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentMemory.LiveTests;

public sealed class ManagedAgentMemoryLiveTests
{
    [LiveFact]
    [Trait("Category", "Live")]
    public async Task CanRoundTripAndDeleteASessionEvent()
    {
        var endpoint = Environment.GetEnvironmentVariable("REDIS_IRIS_ENDPOINT");
        var storeId = Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID");
        var apiKey = Environment.GetEnvironmentVariable("REDIS_IRIS_API_KEY");
        var client = new RedisAgentMemoryClient(new HttpClient(), new()
        {
            Endpoint = new(endpoint!),
            StoreId = storeId!,
        }, new FixedAgentMemoryCredentialProvider(apiKey!));
        var sessionId = $"dotnet-live-{Guid.NewGuid():N}";
        var ownerId = $"dotnet-live-owner-{Guid.NewGuid():N}";
        try
        {
            var created = await client.AddSessionEventAsync(new()
            {
                SessionId = sessionId,
                ActorId = ownerId,
                Role = SessionEventRole.USER,
                Content = [new("live test")],
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var loaded = await client.GetSessionMemoryAsync(sessionId);

            Assert.Equal(created.EventId, Assert.Single(loaded.Events).EventId);
            Assert.Equal(ownerId, loaded.OwnerId);
        }
        finally
        {
            await client.DeleteSessionMemoryAsync(sessionId);
        }
    }
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
