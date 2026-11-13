// Copyright (c) Redis, Inc.

using System.Text.Json;

namespace Redis.Iris.AgentMemory;

// Wire differences from the original API stay here so callers can retain the same public contracts.
// Contract: https://redis.io/docs/latest/develop/ai/context-engine/agent-memory/api-reference/openapi-agent-memory.json
internal static class IrisAgentMemoryContract
{
    internal static object EventRequest(AddSessionEventRequest request) => new
    {
        request.ActorId,
        request.Role,
        request.Content,
        request.CreatedAt,
        request.Metadata,
        request.IdempotencyKey,
        NamespaceId = request.NamespaceRef?.Id,
    };

    internal static object SearchRequest(LongTermMemorySearchRequest request)
    {
        // Never silently drop a scope constraint when translating between contracts.
        if (request.Filter?.Namespace is not null)
        {
            throw new NotSupportedException("The Iris API does not support namespace labels. Use NamespaceRef with a namespace ID.");
        }

        if (request.Filter?.NamespaceRef is { Ne: not null } or { All: not null })
        {
            throw new NotSupportedException("The Iris API supports only Eq and In for namespace filters.");
        }

        var op = request.FilterOp?.ToUpperInvariant();
        if (op is not ("ALL" or "ANY"))
        {
            throw new ArgumentException("FilterOp must be all or any.", nameof(request));
        }

        var filter = request.Filter;
        return new
        {
            Query = request.Text,
            MinScore = request.SimilarityThreshold,
            request.Limit,
            Cursor = request.PageToken,
            Filter = filter is null ? null : new
            {
                Op = op,
                filter.OwnerId,
                filter.SessionId,
                NamespaceId = filter.NamespaceRef,
                filter.Unnamespaced,
                filter.Topics,
                filter.MemoryType,
                filter.CreatedAt,
                filter.Attributes,
            },
        };
    }

    internal sealed record SessionResponse(
        string SessionId,
        string OwnerId,
        IReadOnlyList<SessionEvent> Events,
        string? NamespaceId = null,
        SessionSummary? Summary = null)
    {
        internal SessionMemory ToPublic() => new(SessionId, OwnerId, Events,
            NamespaceId is null ? null : new(NamespaceId), Summary);
    }

    internal sealed record SessionListItem(string SessionId);

    internal sealed record SessionListResponse(IReadOnlyList<SessionListItem> Items, string? NextCursor = null)
    {
        internal SessionListResult ToPublic() => new(Items.Select(item => item.SessionId).ToArray(), -1, NextCursor);
    }

    internal sealed record MemoryRecord(
        string MemoryId,
        string Text,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        string? MemoryType = null,
        string? SessionId = null,
        string? OwnerId = null,
        string? NamespaceId = null,
        IReadOnlyList<string>? Topics = null,
        IReadOnlyDictionary<string, JsonElement>? Attributes = null)
    {
        public string? Revision { get; init; }
        public bool? DocumentManaged { get; init; }

        internal LongTermMemoryRecord ToPublic() => new(MemoryId, Text, CreatedAt, UpdatedAt,
            MemoryType, SessionId, OwnerId, NamespaceId is null ? null : new(NamespaceId), Topics, Attributes)
        { Revision = Revision, DocumentManaged = DocumentManaged };
    }

    internal sealed record SearchResponse(IReadOnlyList<MemoryRecord> Items, string? NextCursor = null)
    {
        internal LongTermMemorySearchResult ToPublic() => new(Items.Select(item => item.ToPublic()).ToArray(), NextCursor);
    }

    internal sealed record BulkError(string MemoryId, string Title, string? Detail = null, int? Status = null, string? Type = null)
    {
        internal BulkOperationError ToPublic() => new(MemoryId, Detail ?? Title) { Status = Status, Type = Type };
    }

    internal sealed record BulkDeleteResponse(IReadOnlyList<string> Deleted, IReadOnlyList<BulkError>? Errors = null)
    {
        internal BulkDeleteLongTermMemoriesResult ToPublic() => new(Deleted,
            Errors?.Select(error => error.ToPublic()).ToArray());
    }
}
