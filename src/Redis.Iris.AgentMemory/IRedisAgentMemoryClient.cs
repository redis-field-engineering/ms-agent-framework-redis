// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory;

/// <summary>Provides access to the Redis Agent Memory data plane.</summary>
public interface IRedisAgentMemoryClient
{
    /// <summary>Adds one event to session memory.</summary>
    Task<SessionEvent> AddSessionEventAsync(AddSessionEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets the current state of a session.</summary>
    Task<SessionMemory> GetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default);

    /// <summary>Gets the current state of a session, or <see langword="null"/> when it has not been created.</summary>
    async Task<SessionMemory?> TryGetSessionMemoryAsync(string sessionId, bool includeSummarizedEvents = false, CancellationToken cancellationToken = default)
    {
        try
        {
            return await this.GetSessionMemoryAsync(sessionId, includeSummarizedEvents, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentMemoryException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

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

    /// <summary>Creates 1-100 memories using the current Iris API. Existing IDs are not overwritten.</summary>
    Task<BulkCreateLongTermMemoriesResult> BulkCreateLongTermMemoriesAsync(BulkCreateLongTermMemoriesRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Reads a memory by ID using the current Iris API. The caller must authorize record access.</summary>
    Task<LongTermMemoryRecord> GetLongTermMemoryAsync(string memoryId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Partially updates a memory using the current Iris API.</summary>
    Task<LongTermMemoryRecord> UpdateLongTermMemoryAsync(string memoryId, UpdateLongTermMemoryRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Creates or fully replaces a memory using the current Iris API. Omitted optional fields are cleared.</summary>
    Task<UpsertLongTermMemoryResult> UpsertLongTermMemoryAsync(string memoryId, UpsertLongTermMemoryRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    /// <summary>Deletes 1-100 long-term memories by ID.</summary>
    Task<BulkDeleteLongTermMemoriesResult> BulkDeleteLongTermMemoriesAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
