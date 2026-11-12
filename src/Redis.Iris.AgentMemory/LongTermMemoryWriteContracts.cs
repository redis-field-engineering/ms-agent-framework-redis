// Copyright (c) Redis, Inc.

using System.Text.Json;

namespace Redis.Iris.AgentMemory;

/// <summary>Content for a direct memory create or full replacement.</summary>
public record LongTermMemoryContent
{
    /// <summary>Gets the memory text (1-50000 characters).</summary>
    public required string Text { get; init; }
    /// <summary>Gets the owner selected by trusted application code.</summary>
    public required string OwnerId { get; init; }
    /// <summary>Gets a built-in or registered custom type. Defaults to semantic on the server.</summary>
    public string? MemoryType { get; init; }
    /// <summary>Gets the associated session, if any.</summary>
    public string? SessionId { get; init; }
    /// <summary>Gets the existing destination namespace.</summary>
    public NamespaceReferenceInput? NamespaceRef { get; init; }
    /// <summary>Gets up to 50 topics of 1-100 characters each.</summary>
    public IReadOnlyList<string>? Topics { get; init; }
    /// <summary>Gets structured attributes validated against the registered memory type by the server.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Attributes { get; init; }
}

/// <summary>A memory to create. Reuse its ID on retries; creation never overwrites an existing ID.</summary>
public sealed record CreateLongTermMemoryRequest : LongTermMemoryContent
{
    /// <summary>Gets the stable application-provided memory ID.</summary>
    public required string Id { get; init; }
}

/// <summary>Creates a bounded batch without silently discarding excess records.</summary>
public sealed record BulkCreateLongTermMemoriesRequest
{
    /// <summary>Gets 1-100 memories.</summary>
    public required IReadOnlyList<CreateLongTermMemoryRequest> Memories { get; init; }
    /// <summary>Gets a request replay key. Reuse only with an identical request; required by create input hooks.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>A bulk create can succeed for only some records. Always inspect Errors.</summary>
public sealed record BulkCreateLongTermMemoriesResult(IReadOnlyList<string> Created, IReadOnlyList<BulkOperationError>? Errors = null);

/// <summary>A partial update. Null properties are omitted; empty topics clears the tags.</summary>
/// <remarks>The published Iris contract allows an omitted sessionId to clear the link. Supply the current
/// SessionId to preserve it. Namespace removal and attribute replacement require PUT.</remarks>
public sealed record UpdateLongTermMemoryRequest
{
    /// <summary>Gets new text, or null to leave it unchanged.</summary>
    public string? Text { get; init; }
    /// <summary>Gets a new built-in or registered custom type.</summary>
    public string? MemoryType { get; init; }
    /// <summary>Gets replacement topics, or null to leave them unchanged.</summary>
    public IReadOnlyList<string>? Topics { get; init; }
    /// <summary>Gets the authorized destination namespace.</summary>
    public NamespaceReferenceInput? NamespaceRef { get; init; }
    /// <summary>Gets a new owner. Applications must authorize ownership transfers.</summary>
    public string? OwnerId { get; init; }
    /// <summary>Gets the session link. Supply the current value to preserve it, or empty string to clear it.</summary>
    public string? SessionId { get; init; }
    /// <summary>Gets the reviewed revision. Omission requests an unconditional write.</summary>
    public string? ExpectedRevision { get; init; }
    /// <summary>Gets the replay key for this exact request.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Complete replacement content. Omitted optional fields are cleared and the store TTL restarts.</summary>
public sealed record UpsertLongTermMemoryRequest : LongTermMemoryContent
{
    /// <summary>Gets the reviewed revision. Omission permits unconditional replacement or creation.</summary>
    public string? ExpectedRevision { get; init; }
    /// <summary>Gets the replay key for this exact request.</summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Identifies a completed upsert. Read the record to obtain its new revision.</summary>
public sealed record UpsertLongTermMemoryResult(string Id, bool Created);
