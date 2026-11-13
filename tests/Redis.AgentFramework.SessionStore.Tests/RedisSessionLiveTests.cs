// Copyright (c) Redis, Inc.

using Redis.AgentFramework.SessionStore;
using StackExchange.Redis;
using Xunit;

namespace Redis.AgentFramework.SessionStore.Tests;

public sealed class RedisSessionLiveTests
{
    [RedisFact]
    public async Task RedisPersistsAcrossConnectionsAndHonorsExpiryAndDeletion()
    {
        var connectionString = Environment.GetEnvironmentVariable("REDIS_SESSION_STORE_TEST_CONNECTION")!;
        using var firstConnection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        using var secondConnection = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var options = new RedisAgentSessionStoreOptions { KeyPrefix = $"session-store-test:{Guid.NewGuid():N}:" };
        var first = new RedisAgentSessionStore(firstConnection.GetDatabase(), options);
        var second = new RedisAgentSessionStore(secondConnection.GetDatabase(), options);
        var agent = RedisAgentSessionStoreTests.CreateAgent();
        var recreated = RedisAgentSessionStoreTests.CreateAgent();
        try
        {
            var session = await first.GetSessionAsync(agent, "context");
            session.StateBag.SetValue("provider", "durable");
            await first.SaveSessionAsync(agent, "context", session);
            Assert.Equal("durable", (await second.GetSessionAsync(recreated, "context")).StateBag.GetValue<string>("provider"));
            await second.DeleteSessionAsync(recreated, "context");
            Assert.Equal(0, (await first.GetSessionAsync(agent, "context")).StateBag.Count);

            options.Expiry = TimeSpan.FromMilliseconds(150);
            var expiring = new RedisAgentSessionStore(firstConnection.GetDatabase(), options);
            await expiring.SaveSessionAsync(agent, "expires", session);
            await Task.Delay(400);
            Assert.Equal(0, (await second.GetSessionAsync(recreated, "expires")).StateBag.Count);
        }
        finally
        {
            await first.DeleteSessionAsync(agent, "context");
            await first.DeleteSessionAsync(agent, "expires");
        }
    }
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_SESSION_STORE_TEST_CONNECTION")))
            Skip = "Set REDIS_SESSION_STORE_TEST_CONNECTION to run against Redis.";
    }
}
