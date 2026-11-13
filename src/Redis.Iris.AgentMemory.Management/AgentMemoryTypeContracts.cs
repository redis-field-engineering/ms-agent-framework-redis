// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory.Management;

/// <summary>Field types in the Iris control-plane v2 wire contract.</summary>
public enum CustomMemoryFieldType
{
    /// <summary>A string (str in store configuration).</summary>
    STRING,
    /// <summary>An integer.</summary>
    INTEGER,
    /// <summary>A floating-point number.</summary>
    FLOAT,
    /// <summary>A boolean.</summary>
    BOOLEAN,
    /// <summary>A string list.</summary>
    STRING_LIST,
    /// <summary>A numeric list; not supported by attribute search clauses.</summary>
    FLOAT_LIST,
    /// <summary>An arbitrary JSON object; not supported by attribute search clauses.</summary>
    OBJECT,
}

/// <summary>A structured field in a registered custom memory type.</summary>
public sealed record CustomMemoryFieldDefinition(string Name, string Description, CustomMemoryFieldType Type);

/// <summary>Instructions for background extraction. Set Enabled explicitly when registering a type.</summary>
public sealed record MemoryExtractionStrategy(string Prompt, bool Enabled = true);

/// <summary>A custom server-side memory type. Name, description and fields are immutable after registration.</summary>
public sealed record CustomMemoryTypeDefinition
{
    /// <summary>Gets the unique custom type name.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the description of what the type captures.</summary>
    public required string Description { get; init; }
    /// <summary>Gets optional structured fields. An omitted or empty list allows records without structured attributes.</summary>
    public IReadOnlyList<CustomMemoryFieldDefinition>? Fields { get; init; }
    /// <summary>Gets the optional extraction strategy. A type created without one cannot acquire it through a strategy update.</summary>
    public MemoryExtractionStrategy? ExtractionStrategy { get; init; }
    /// <summary>Gets the server-assigned opaque revision. Not sent on creation.</summary>
    public string? Revision { get; init; }
}

/// <summary>A partial strategy update. Omitted members remain unchanged.</summary>
public sealed record MemoryExtractionStrategyUpdate
{
    /// <summary>Gets replacement extraction instructions.</summary>
    public string? Prompt { get; init; }
    /// <summary>Gets whether to extract new memories, or null to preserve the existing setting.</summary>
    public bool? Enabled { get; init; }
}

/// <summary>A page of custom types ordered by name, with an opaque continuation token.</summary>
public sealed record MemoryTypeListResult(IReadOnlyList<CustomMemoryTypeDefinition> Items, string? NextCursor = null);
