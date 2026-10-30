// Copyright (c) Redis, Inc.

using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisIrisSessionEventWriterTests
{
    [Fact]
    public async Task ComposedProvidersProduceExactlyOneEvent()
    {
        var client = new RecordingClient();
        var writer = new RedisIrisSessionEventWriter(client, RedisIrisEventWriter.ChatHistoryProvider);
        var state = new RedisIrisProviderState("session", "owner", "agent");
        var message = new ChatMessage(ChatRole.User, "hello");

        var historyCount = await writer.WriteAsync(RedisIrisEventWriter.ChatHistoryProvider, [message], state);
        var memoryCount = await writer.WriteAsync(RedisIrisEventWriter.MemoryProvider, [message], state);

        Assert.Equal(1, historyCount);
        Assert.Equal(0, memoryCount);
        Assert.Single(client.Added);
    }

    [Fact]
    public async Task StandaloneMemoryProviderCanOwnEventCapture()
    {
        var client = new RecordingClient();
        var writer = new RedisIrisSessionEventWriter(client, RedisIrisEventWriter.MemoryProvider);
        var state = new RedisIrisProviderState("session", "owner", "agent");

        var count = await writer.WriteAsync(RedisIrisEventWriter.MemoryProvider, [new(ChatRole.User, "hello")], state);

        Assert.Equal(1, count);
        Assert.Single(client.Added);
    }

    [Fact]
    public async Task UserEventsUseStorageOwnerRequiredByRamSessionContract()
    {
        var client = new RecordingClient();
        var writer = new RedisIrisSessionEventWriter(client, RedisIrisEventWriter.ChatHistoryProvider);
        var state = new RedisIrisProviderState("session", "17-tenant-12-traveler-001", "agent", "traveler-001");

        await writer.WriteAsync(RedisIrisEventWriter.ChatHistoryProvider, [new(ChatRole.User, "hello")], state);

        Assert.Equal("17-tenant-12-traveler-001", Assert.Single(client.Added).ActorId);
        Assert.Equal("traveler-001", state.ActorId);
    }

    private sealed class RecordingClient : IRedisAgentMemoryClient
    {
        public List<AddSessionEventRequest> Added { get; } = [];
        public Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default)
        {
            this.Added.Add(request);
            return Task.FromResult(new SessionEvent("event", request.ActorId, request.SessionId!, request.Role, request.Content, request.CreatedAt, request.CreatedAt));
        }

        public Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
