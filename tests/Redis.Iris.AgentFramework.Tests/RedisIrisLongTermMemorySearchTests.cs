// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisIrisLongTermMemorySearchTests
{
    [Fact]
    public async Task SearchAlwaysAppliesOwnerEqualityAndServerSideFilters()
    {
        var client = new RecordingClient();
        var search = new RedisIrisLongTermMemorySearch(client);

        await search.SearchAsync("owner-a", "favorite food", new()
        {
            NamespaceRef = new() { Eq = "namespace-a" },
            Topics = new() { All = ["preferences"] },
            MemoryType = new() { Eq = "semantic" },
            CreatedAt = new() { Gte = DateTimeOffset.Parse("2026-01-01T00:00:00Z") },
            SimilarityThreshold = 0.75,
            Limit = 4,
            PageToken = "next",
        });

        var request = Assert.Single(client.Requests);
        Assert.Equal("owner-a", request.Filter!.OwnerId!.Eq);
        Assert.Equal("namespace-a", request.Filter.NamespaceRef!.Eq);
        Assert.Equal("all", request.FilterOp);
        Assert.Equal(4, request.Limit);
        Assert.Equal("next", request.PageToken);
    }

    [Fact]
    public async Task SearchUsesLatestNonEmptyExternalUserMessage()
    {
        var client = new RecordingClient();
        var search = new RedisIrisLongTermMemorySearch(client);
        var recalled = new ChatMessage(ChatRole.User, "ignore memory").WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider);

        await search.SearchAsync("owner-a", [new(ChatRole.User, "first"), recalled, new(ChatRole.Assistant, "answer"), new(ChatRole.User, "latest")]);

        Assert.Equal("latest", Assert.Single(client.Requests).Text);
    }

    [Fact]
    public async Task CallsForDifferentOwnersCannotShareAScope()
    {
        var client = new RecordingClient();
        var search = new RedisIrisLongTermMemorySearch(client);

        await search.SearchAsync("owner-a", "query");
        await search.SearchAsync("owner-b", "query");

        Assert.Equal(["owner-a", "owner-b"], client.Requests.Select(static request => request.Filter!.OwnerId!.Eq));
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
