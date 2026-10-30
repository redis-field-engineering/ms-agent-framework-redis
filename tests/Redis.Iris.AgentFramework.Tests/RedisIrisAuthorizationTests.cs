// Copyright (c) Redis, Inc.

using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisIrisAuthorizationTests
{
    [Fact]
    public void RegistrationFailsClosedWithoutHostAuthorizationPolicy()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddRedisIrisAgentMemory(options =>
        {
            options.Endpoint = new("https://memory.example.test");
            options.StoreId = "trusted-store";
            options.Credential = "secret";
            options.IdentityResolver = _ => new("tenant", "owner", "session", "owner", "agent");
        }));
    }

    [Fact]
    public async Task AdministrativeCrossOwnerSearchRequiresExplicitAuthorization()
    {
        var client = new RecordingClient();
        var search = new RedisIrisAdministrativeMemorySearch(client, (_, _) => ValueTask.FromResult(false));
        var request = new RedisIrisAdministrativeSearchRequest("support investigation", "query", new());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => search.SearchAcrossOwnersAsync(request));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task NormalSearchCannotSubstituteOwnerThroughNamespaceOrOtherFilters()
    {
        var client = new RecordingClient();
        var search = new RedisIrisLongTermMemorySearch(client);

        await search.SearchAsync("trusted-owner", "query", new()
        {
            Namespace = new() { Eq = "attacker-owner" },
            NamespaceRef = new() { Eq = "attacker-store" },
            SessionId = new() { Eq = "attacker-session" },
        });

        var filter = Assert.Single(client.Requests).Filter!;
        Assert.Equal("trusted-owner", filter.OwnerId!.Eq);
        Assert.Equal("attacker-owner", filter.Namespace!.Eq);
        Assert.Equal("attacker-store", filter.NamespaceRef!.Eq);
    }

    private sealed class RecordingClient : IRedisAgentMemoryClient
    {
        public List<LongTermMemorySearchRequest> Requests { get; } = [];
        public Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            return Task.FromResult(new LongTermMemorySearchResult([]));
        }
        public Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
