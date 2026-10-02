namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>Marks an application POCO as a Context Retriever entity.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ContextEntityAttribute : Attribute
{
    /// <summary>Stable external entity name. Defaults to the CLR type name.</summary>
    public string? Name { get; init; }
    /// <summary>Redis key template, for example <c>customer:{id}</c>.</summary>
    public required string KeyTemplate { get; init; }
    /// <summary>Human-readable entity description.</summary>
    public string Description { get; init; } = "";
}

/// <summary>Marks a POCO property as a Context Retriever field.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class ContextFieldAttribute : Attribute
{
    /// <summary>Stable external field name. Defaults to the JSON property name or CLR property name.</summary>
    public string? Name { get; init; }
    /// <summary>Human-readable field description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Whether this field supplies a key-template component.</summary>
    public bool IsKeyComponent { get; init; }
    /// <summary>Optional Redis index type.</summary>
    public ContextIndex Index { get; init; }
    /// <summary>Allowed values for a tag-indexed categorical field.</summary>
    public string[]? AllowedValues { get; init; }
    /// <summary>Vector dimensions. Required for vector fields.</summary>
    public int VectorDimensions { get; init; }
    /// <summary>Vector distance metric.</summary>
    public string DistanceMetric { get; init; } = "cosine";
    /// <summary>Text field represented by this vector.</summary>
    public string? Embeds { get; init; }
    /// <summary>Model used to produce stored embeddings.</summary>
    public string? EmbeddingModel { get; init; }
    /// <summary>Text-search weight.</summary>
    public double Weight { get; init; } = 1;
    /// <summary>Disable stemming for a text index.</summary>
    public bool NoStem { get; init; }
    /// <summary>Make a numeric index sortable.</summary>
    public bool Sortable { get; init; }
}

/// <summary>Marks a navigation property as a Context Retriever relationship.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class ContextRelationshipAttribute(Type targetType) : Attribute
{
    /// <summary>Target entity CLR type.</summary>
    public Type TargetType { get; } = targetType;
    /// <summary>Stable external relationship name. Defaults to the JSON or CLR property name.</summary>
    public string? Name { get; init; }
    /// <summary>Relationship description.</summary>
    public string Description { get; init; } = "";
    /// <summary>External field containing the target key.</summary>
    public required string SourceField { get; init; }
}

/// <summary>Supported Redis Context Retriever index types.</summary>
public enum ContextIndex
{
    /// <summary>Not indexed.</summary>
    None,
    /// <summary>Full-text index.</summary>
    Text,
    /// <summary>Exact-match tag index.</summary>
    Tag,
    /// <summary>Numeric range index.</summary>
    Numeric,
    /// <summary>Vector similarity index.</summary>
    Vector,
}
