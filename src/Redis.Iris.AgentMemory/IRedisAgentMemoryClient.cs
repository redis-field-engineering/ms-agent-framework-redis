// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory;

/// <summary>Provides access to the Redis Agent Memory data plane.</summary>
public interface IRedisAgentMemoryClient
{
    /// <summary>Adds one event to session memory.</summary>
    Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets the current state of a session.</summary>
    Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default);

    /// <summary>Gets one event from a session.</summary>
    Task<SessionEvent> GetSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Deletes one event from a session.</summary>
    Task DeleteSessionEventAsync(string sessionId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a session and its events. Promoted long-term memories are not deleted.</summary>
    Task DeleteSessionMemoryAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Lists sessions belonging to one owner.</summary>
    Task<SessionListResult> ListSessionsAsync(string ownerId, int limit = 100, string? pageToken = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Searches long-term memory.</summary>
    Task<LongTermMemorySearchResult> SearchLongTermMemoryAsync(LongTermMemorySearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes 1-100 long-term memories by ID.</summary>
    Task<BulkDeleteLongTermMemoriesResult> BulkDeleteLongTermMemoriesAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
