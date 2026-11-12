// Copyright (c) Redis, Inc.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Redis.Iris.AgentMemory;

/// <summary>A role accepted by Redis Agent Memory session memory.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionEventRole>))]
public enum SessionEventRole
{
    /// <summary>A user message.</summary>
    USER,

    /// <summary>An assistant message.</summary>
    ASSISTANT,

    /// <summary>A system message.</summary>
    SYSTEM,
}

/// <summary>A text content part.</summary>
public sealed record SessionEventContent(string Text);

/// <summary>A request to append one event to session memory.</summary>
public sealed record AddSessionEventRequest
{
    /// <summary>Gets the stable actor identifier.</summary>
    public required string ActorId { get; init; }

    /// <summary>Gets the event role.</summary>
    public required SessionEventRole Role { get; init; }

    /// <summary>Gets the event content.</summary>
    public required IReadOnlyList<SessionEventContent> Content { get; init; }

    /// <summary>Gets the UTC time at which the event occurred.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the session identifier. The server generates one when omitted.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets a reference to a managed namespace resource.</summary>
    public NamespaceReferenceInput? NamespaceRef { get; init; }

    /// <summary>Gets optional JSON metadata.</summary>
    public JsonElement? Metadata { get; init; }

    /// <summary>Gets an optional Iris API idempotency key, required by stores with session.append input hooks.</summary>
    /// <remarks>Reuse only for identical requests. Not supported by the legacy API.</remarks>
    public string? IdempotencyKey { get; init; }
}

/// <summary>A stable namespace reference used on writes.</summary>
public sealed record NamespaceReferenceInput(string Id);

/// <summary>A namespace reference returned by the service.</summary>
public sealed record NamespaceReference(string Id, string? Name = null);

/// <summary>A stored session event.</summary>
public sealed record SessionEvent(
    string EventId,
    string ActorId,
    string SessionId,
    SessionEventRole Role,
    IReadOnlyList<SessionEventContent> Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset SystemTimestamp,
    JsonElement? Metadata = null);

/// <summary>A server-maintained summary of compacted session events.</summary>
public sealed record SessionSummary(
    string Text,
    string SummarizedUpToEventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    JsonElement Metadata,
    int SummarizedEvents);

/// <summary>The current state of one session.</summary>
public sealed record SessionMemory(
    string SessionId,
    string OwnerId,
    IReadOnlyList<SessionEvent> Events,
    NamespaceReference? NamespaceRef = null,
    SessionSummary? Summary = null);

/// <summary>A server-side filter operator.</summary>
public sealed record StringFilter
{
    /// <summary>Gets an equality constraint.</summary>
    public string? Eq { get; init; }

    /// <summary>Gets an inequality constraint.</summary>
    public string? Ne { get; init; }

    /// <summary>Gets an any-of constraint.</summary>
    [JsonPropertyName("in")]
    public IReadOnlyList<string>? In { get; init; }

    /// <summary>Gets an all-values constraint.</summary>
    public IReadOnlyList<string>? All { get; init; }
}

/// <summary>Filters an ISO-8601 timestamp.</summary>
public sealed record DateTimeFilter
{
    /// <summary>Gets an exclusive lower bound.</summary>
    public DateTimeOffset? Gt { get; init; }
    /// <summary>Gets an inclusive lower bound.</summary>
    public DateTimeOffset? Gte { get; init; }
    /// <summary>Gets an exclusive upper bound.</summary>
    public DateTimeOffset? Lt { get; init; }
    /// <summary>Gets an inclusive upper bound.</summary>
    public DateTimeOffset? Lte { get; init; }
    /// <summary>Gets an equality constraint.</summary>
    public DateTimeOffset? Eq { get; init; }
}

/// <summary>Filters applied by the Agent Memory service before ranking.</summary>
public sealed record LongTermMemoryFilter
{
    /// <summary>Gets the required or optional owner constraint.</summary>
    public StringFilter? OwnerId { get; init; }

    /// <summary>Gets the session constraint.</summary>
    public StringFilter? SessionId { get; init; }

    /// <summary>Gets the deprecated namespace-label constraint.</summary>
    public StringFilter? Namespace { get; init; }

    /// <summary>Gets the stable namespace-resource constraint.</summary>
    public StringFilter? NamespaceRef { get; init; }

    /// <summary>Gets whether only memories with no namespace placement should match.</summary>
    public bool? Unnamespaced { get; init; }

    /// <summary>Gets the topic constraint.</summary>
    public StringFilter? Topics { get; init; }

    /// <summary>Gets the memory-type constraint.</summary>
    public StringFilter? MemoryType { get; init; }

    /// <summary>Gets the creation-time constraint.</summary>
    public DateTimeFilter? CreatedAt { get; init; }
}

/// <summary>A request to search long-term memory.</summary>
public sealed record LongTermMemorySearchRequest
{
    /// <summary>Gets the semantic query text.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the normalized cosine-similarity threshold.</summary>
    public double? SimilarityThreshold { get; init; }

    /// <summary>Gets service-side structured filters.</summary>
    public LongTermMemoryFilter? Filter { get; init; }

    /// <summary>Gets the top-level filter conjunction, <c>all</c> or <c>any</c>.</summary>
    public string FilterOp { get; init; } = "all";

    /// <summary>Gets the result limit.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Gets an opaque continuation token.</summary>
    public string? PageToken { get; init; }
}

/// <summary>A long-term memory returned by Redis Agent Memory.</summary>
public sealed record LongTermMemoryRecord(
    string Id,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? MemoryType = null,
    string? SessionId = null,
    string? OwnerId = null,
    NamespaceReference? NamespaceRef = null,
    IReadOnlyList<string>? Topics = null,
    IReadOnlyDictionary<string, JsonElement>? Attributes = null);

/// <summary>A page of long-term memory search results.</summary>
public sealed record LongTermMemorySearchResult(
    IReadOnlyList<LongTermMemoryRecord> Items,
    string? NextPageToken = null);

/// <summary>A page of session IDs.</summary>
/// <param name="Items">The session IDs in this page.</param>
/// <param name="Total">The service-reported total, or -1 when the Iris API does not supply a total.</param>
/// <param name="NextPageToken">The opaque continuation token (the Iris API's nextCursor).</param>
public sealed record SessionListResult(
    IReadOnlyList<string> Items,
    int Total,
    string? NextPageToken = null);

/// <summary>Describes one failed item in a bulk operation.</summary>
public sealed record BulkOperationError(string Id, string Error);

/// <summary>The result of deleting long-term memories in bulk.</summary>
public sealed record BulkDeleteLongTermMemoriesResult(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<BulkOperationError>? Errors = null);
