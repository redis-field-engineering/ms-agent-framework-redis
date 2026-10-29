using System.Net;
using System.Text.Json;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentMemory.LiveTests;

/// <summary>Requires an ownership-enforcing deployment and two fresh, signed user JWTs.</summary>
public sealed class DelegatedAgentMemoryLiveTests
{
    [RamDelegatedLiveFact]
    [Trait("Category", "Live")]
    public async Task SignedUsersAreIsolatedOnSessionReadsListsWritesAndDeletes()
    {
        var users = LoadUsers();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var clients = users.Select(user => Client(http, user)).ToArray();
        var sessions = users.Select(_ => "dotnet-jwt-" + Guid.NewGuid().ToString("N")).ToArray();
        var forgedSession = "dotnet-jwt-forged-" + Guid.NewGuid().ToString("N");
        try
        {
            for (var i = 0; i < 2; i++)
            {
                await AddAsync(clients[i], sessions[i], users[i].Subject);
                Assert.Equal(users[i].Subject, (await clients[i].GetSessionMemoryAsync(sessions[i])).OwnerId);
            }

            for (var i = 0; i < 2; i++)
            {
                var other = 1 - i;
                await DeniedAsync(() => clients[i].GetSessionMemoryAsync(sessions[other]), HttpStatusCode.NotFound);
                var beforeDelete = await clients[other].GetSessionMemoryAsync(sessions[other]);
                // An inaccessible session can be reported as absent or silently ignored
                // by an idempotent DELETE. In either case its owner must see it unchanged.
                try { await clients[i].DeleteSessionMemoryAsync(sessions[other]); }
                catch (AgentMemoryException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
                var afterDelete = await clients[other].GetSessionMemoryAsync(sessions[other]);
                Assert.Equal(JsonSerializer.Serialize(beforeDelete), JsonSerializer.Serialize(afterDelete));
                var foreign = await clients[i].ListSessionsAsync(users[other].Subject);
                Assert.DoesNotContain(sessions[other], foreign.Items);
                var own = await clients[i].ListSessionsAsync(users[i].Subject);
                Assert.Contains(sessions[i], own.Items);
                var recall = await clients[i].SearchLongTermMemoryAsync(new()
                {
                    Text = "delegated isolation",
                    Filter = new() { OwnerId = new() { Eq = users[other].Subject } },
                });
                Assert.Empty(recall.Items);
            }

            await DeniedAsync(() => AddAsync(clients[0], forgedSession, users[1].Subject), HttpStatusCode.Forbidden);
        }
        finally
        {
            // IDs are unique to this run. Never clear all records for a real Cognito subject.
            await CleanupAsync(clients[0], sessions[0]);
            await CleanupAsync(clients[1], sessions[1]);
            await CleanupAsync(clients[1], forgedSession);
        }
    }

    [RamDelegatedLiveFact]
    [Trait("Category", "Live")]
    public async Task PersonalSessionCannotBeAccessedWithoutAValidDelegatedToken()
    {
        var user = LoadUsers()[0];
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var client = Client(http, user);
        var session = "dotnet-jwt-auth-" + Guid.NewGuid().ToString("N");
        try
        {
            await AddAsync(client, session, user.Subject);
            var keyOnly = Client(http, null);
            await DeniedAsync(() => keyOnly.GetSessionMemoryAsync(session), HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
            var invalidToken = Client(http, new User(user.Subject, "invalid-jwt", DateTimeOffset.UtcNow.AddMinutes(5)));
            await DeniedAsync(() => invalidToken.GetSessionMemoryAsync(session), HttpStatusCode.Unauthorized);
            var invalidKey = Client(http, user, "invalid-agent-key");
            await DeniedAsync(() => invalidKey.GetSessionMemoryAsync(session), HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
        finally
        {
            await CleanupAsync(client, session);
        }
    }

    [RamDelegatedLiveFact]
    [Trait("Category", "Live")]
    public async Task PopulatedLongTermMemoriesCannotBeReadAcrossSignedUsers()
    {
        var users = LoadUsers();
        await using var first = new LiveMemoryFixture(users[0].AccessToken, users[0].ExpiresAt);
        await using var second = new LiveMemoryFixture(users[1].AccessToken, users[1].ExpiresAt);
        var fixtures = new[] { first, second };
        var topic = "live-topic-" + Guid.NewGuid().ToString("N");
        var ids = new[]
        {
            await first.SeedAsync(users[0].Subject, "Synthetic traveler prefers window seats.", topic),
            await second.SeedAsync(users[1].Subject, "Synthetic traveler prefers aisle seats.", topic),
        };
        // Require both records to be searchable before asserting either cross-user denial.
        for (var i = 0; i < 2; i++)
            await LiveMemoryFixture.WaitForAsync(fixtures[i].Client,
                new() { Text = "", Filter = new() { OwnerId = new() { Eq = users[i].Subject }, Topics = new() { Eq = topic } } },
                result => result.Items.Any(item => item.Id == ids[i]));
        for (var i = 0; i < 2; i++)
        {
            var ownRequest = new LongTermMemorySearchRequest { Text = "", Filter = new() { OwnerId = new() { Eq = users[i].Subject }, Topics = new() { Eq = topic } } };
            var own = await LiveMemoryFixture.WaitForAsync(fixtures[i].Client, ownRequest, result => result.Items.Any(item => item.Id == ids[i]));
            Assert.DoesNotContain(own.Items, item => item.Id == ids[1 - i]);
            var foreign = await fixtures[i].Client.SearchLongTermMemoryAsync(ownRequest with { Filter = new() { OwnerId = new() { Eq = users[1 - i].Subject }, Topics = new() { Eq = topic } } });
            Assert.Empty(foreign.Items);
        }
    }

    private static User[] LoadUsers()
    {
        var path = Environment.GetEnvironmentVariable("REDIS_IRIS_RAM_DELEGATED_USERS_FILE")!;
        var users = JsonSerializer.Deserialize<User[]>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(2, users.Length);
        Assert.NotEqual(users[0].Subject, users[1].Subject);
        foreach (var user in users)
        {
            Assert.False(string.IsNullOrWhiteSpace(user.Subject));
            new AgentMemoryDelegatedToken(user.AccessToken, user.ExpiresAt).Validate();
        }
        return users;
    }

    private static RedisAgentMemoryClient Client(HttpClient http, User? user, string? key = null)
    {
        var config = LiveConfiguration.Load();
        return new(http, new() { Endpoint = config.Endpoint, StoreId = config.StoreId },
            new FixedAgentMemoryCredentialProvider(key ?? config.ApiKey),
            user is null ? null : new FixedAgentMemoryDelegatedTokenProvider(new(user.AccessToken, user.ExpiresAt)));
    }

    private static Task<SessionEvent> AddAsync(IRedisAgentMemoryClient client, string session, string subject) => client.AddSessionEventAsync(new()
    {
        SessionId = session,
        ActorId = subject,
        Role = SessionEventRole.USER,
        Content = [new("Synthetic delegated isolation test.")],
        CreatedAt = DateTimeOffset.UtcNow,
    });

    private static async Task DeniedAsync(Func<Task> operation, params HttpStatusCode[] statuses)
    {
        var error = await Assert.ThrowsAsync<AgentMemoryException>(operation);
        Assert.Contains(error.StatusCode!.Value, statuses);
    }

    private static async Task CleanupAsync(IRedisAgentMemoryClient client, string session)
    {
        try { await client.DeleteSessionMemoryAsync(session); }
        catch (AgentMemoryException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
    }

    // A class deliberately avoids a record-generated ToString that could expose tokens.
    private sealed class User(string subject, string accessToken, DateTimeOffset expiresAt)
    {
        public string Subject { get; } = subject;
        public string AccessToken { get; } = accessToken;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
}

internal sealed class RamDelegatedLiveFactAttribute : FactAttribute
{
    public RamDelegatedLiveFactAttribute()
    {
        var bundle = Environment.GetEnvironmentVariable("REDIS_IRIS_RAM_DELEGATED_USERS_FILE");
        if (string.IsNullOrWhiteSpace(bundle) || !File.Exists(bundle) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_IRIS_ENDPOINT")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REDIS_IRIS_API_KEY")))
        {
            Skip = "Set RAM connection settings and REDIS_IRIS_RAM_DELEGATED_USERS_FILE with two fresh signed users; server ownership enforcement must be on.";
        }
    }
}
