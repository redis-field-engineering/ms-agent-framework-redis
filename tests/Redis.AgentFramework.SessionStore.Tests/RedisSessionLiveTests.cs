// Copyright (c) Redis, Inc.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            session.StateBag.SetValue("nested", new { Values = new[] { 1, 2 }, Enabled = true });
            await first.SaveSessionAsync(agent, "context", session);
            var database = firstConnection.GetDatabase();
            var key = GetKey(options.KeyPrefix, agent.Id, "context");
            Assert.Equal("object", (string?)await database.ExecuteAsync("JSON.TYPE", key, "."));
            var expected = await agent.SerializeSessionAsync(session);
            var stored = (string)(await database.ExecuteAsync("JSON.GET", key, "."))!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(stored)));
            await database.ExecuteAsync("JSON.SET", key, "$.stale", "true");
            await first.SaveSessionAsync(agent, "context", session);
            Assert.Equal("[]", (string?)await database.ExecuteAsync("JSON.GET", key, "$.stale"));
            Assert.Equal("durable", (await second.GetSessionAsync(recreated, "context")).StateBag.GetValue<string>("provider"));
            await second.DeleteSessionAsync(recreated, "context");
            Assert.Equal(0, (await first.GetSessionAsync(agent, "context")).StateBag.Count);

            options.Expiry = TimeSpan.FromMilliseconds(150);
            var expiring = new RedisAgentSessionStore(firstConnection.GetDatabase(), options);
            await expiring.SaveSessionAsync(agent, "expires", session);
            await Task.Delay(400);
            Assert.Equal(0, (await second.GetSessionAsync(recreated, "expires")).StateBag.Count);

            var retainedKey = GetKey(options.KeyPrefix, agent.Id, "context");
            var retained = new RedisAgentSessionStore(database, new() { KeyPrefix = options.KeyPrefix, Expiry = TimeSpan.FromMinutes(1) });
            await retained.SaveSessionAsync(agent, "context", session);
            Assert.NotNull(await database.KeyTimeToLiveAsync(retainedKey));
            await database.KeyExpireAsync(retainedKey, TimeSpan.FromSeconds(5));
            await retained.GetSessionAsync(agent, "context");
            Assert.True(await database.KeyTimeToLiveAsync(retainedKey) <= TimeSpan.FromSeconds(5));
            await retained.SaveSessionAsync(agent, "context", session);
            Assert.True(await database.KeyTimeToLiveAsync(retainedKey) > TimeSpan.FromSeconds(30));
            await second.SaveSessionAsync(agent, "context", session);
            Assert.Null(await database.KeyTimeToLiveAsync(retainedKey));
        }
        finally
        {
            await first.DeleteSessionAsync(agent, "context");
            await first.DeleteSessionAsync(agent, "expires");
        }
    }

    [RedisFact]
    public async Task FreshPrefixLeavesLegacyStringsUntouchedAndTransactionReportsWrongTypes()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("REDIS_SESSION_STORE_TEST_CONNECTION")!);
        var database = connection.GetDatabase();
        var options = new RedisAgentSessionStoreOptions { KeyPrefix = "session-store-test:fresh:" };
        var store = new RedisAgentSessionStore(database, options);
        var agent = RedisAgentSessionStoreTests.CreateAgent();
        var id = Guid.NewGuid().ToString("N");
        var legacyKey = GetKey("agent-framework:sessions:", agent.Id, id);
        var jsonKey = GetKey(options.KeyPrefix, agent.Id, id);
        Assert.NotEqual(legacyKey, jsonKey);
        try
        {
            var session = await agent.CreateSessionAsync();
            session.StateBag.SetValue("marker", "legacy");
            var legacy = (await agent.SerializeSessionAsync(session)).GetRawText();
            await database.StringSetAsync(legacyKey, legacy, TimeSpan.FromMinutes(1));
            Assert.Equal(0, (await store.GetSessionAsync(agent, id)).StateBag.Count);
            await store.SaveSessionAsync(agent, id, session);
            Assert.Equal(legacy, (string?)await database.StringGetAsync(legacyKey));

            Assert.NotNull(await database.KeyTimeToLiveAsync(legacyKey));
            var wrongPrefix = new RedisAgentSessionStore(database);
            await Assert.ThrowsAsync<RedisServerException>(() => wrongPrefix.GetSessionAsync(agent, id).AsTask());
            await Assert.ThrowsAsync<RedisServerException>(() => wrongPrefix.SaveSessionAsync(agent, id, session).AsTask());
            // EXEC still applies PERSIST after JSON.SET reports a runtime type error.
            Assert.Null(await database.KeyTimeToLiveAsync(legacyKey));
            Assert.Equal(legacy, (string?)await database.StringGetAsync(legacyKey));
        }
        finally
        {
            await database.KeyDeleteAsync(legacyKey);
            await database.KeyDeleteAsync(jsonKey);
        }
    }

    [RedisFact]
    public async Task TransactionsWorkWithoutScriptingPermissions()
    {
        var config = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("REDIS_SESSION_STORE_TEST_CONNECTION")!);
        config.AllowAdmin = true;
        using var connection = await ConnectionMultiplexer.ConnectAsync(config);
        var database = connection.GetDatabase();
        var user = $"session-test-{Guid.NewGuid():N}";
        var prefix = $"{user}:";
        var agent = RedisAgentSessionStoreTests.CreateAgent();
        var key = GetKey(prefix, agent.Id, "context");
        try
        {
            await database.ExecuteAsync("ACL", "SETUSER", user, "on", "nopass", $"~{prefix}*", "+@all", "-eval", "-evalsha");
            var original = await agent.CreateSessionAsync();
            original.StateBag.SetValue("marker", "original");
            var unrestricted = new RedisAgentSessionStore(database, new() { KeyPrefix = prefix });
            await unrestricted.SaveSessionAsync(agent, "context", original);

            config.User = user;
            config.Password = null;
            using var restricted = await ConnectionMultiplexer.ConnectAsync(config);
            var changed = await agent.CreateSessionAsync();
            changed.StateBag.SetValue("marker", "changed");
            foreach (var expiry in new TimeSpan?[] { null, TimeSpan.FromMinutes(1) })
            {
                var store = new RedisAgentSessionStore(restricted.GetDatabase(), new() { KeyPrefix = prefix, Expiry = expiry });
                await store.SaveSessionAsync(agent, "context", changed);
                Assert.Equal("changed", (await unrestricted.GetSessionAsync(agent, "context")).StateBag.GetValue<string>("marker"));
            }
        }
        finally
        {
            await database.KeyDeleteAsync(key);
            await database.ExecuteAsync("ACL", "DELUSER", user);
        }
    }

    private static string GetKey(string prefix, string agentId, string contextId) =>
        prefix + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { agentId, contextId })));
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_SESSION_STORE_TEST_CONNECTION")))
            Skip = "Set REDIS_SESSION_STORE_TEST_CONNECTION to run against Redis.";
    }
}
