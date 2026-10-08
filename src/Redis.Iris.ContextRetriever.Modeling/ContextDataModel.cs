using System.Text.Json.Serialization;

namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>JSON DataModel accepted by <c>ctxctl surface create --datamodel</c>.</summary>
public sealed record ContextDataModel(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("entities")] IReadOnlyList<ContextEntityDefinition> Entities)
{
    /// <summary>Number of entities in the manifest.</summary>
    [JsonPropertyName("entity_count")]
    public int EntityCount => Entities.Count;
}

/// <summary>An entity in a Context Retriever DataModel.</summary>
public sealed record ContextEntityDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("redis_key_template")] string RedisKeyTemplate,
    [property: JsonPropertyName("fields")] IReadOnlyList<ContextFieldDefinition> Fields,
    [property: JsonPropertyName("relationships")] IReadOnlyList<ContextRelationshipDefinition> Relationships,
    [property: JsonPropertyName("acl_field_mappings")] IReadOnlyList<ContextAclFieldMapping>? AclFieldMappings = null);

/// <summary>Maps a JSON document field to an agent-key access tag for row-level authorization.</summary>
public sealed record ContextAclFieldMapping(
    [property: JsonPropertyName("document_field")] string DocumentField,
    [property: JsonPropertyName("access_tag")] string AccessTag);

/// <summary>An entity field.</summary>
public sealed record ContextFieldDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("is_key_component")] bool IsKeyComponent,
    [property: JsonPropertyName("redis_indices")] IReadOnlyList<ContextIndexDefinition> RedisIndices,
    [property: JsonPropertyName("allowed_values"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? AllowedValues = null);

/// <summary>A Redis index configuration.</summary>
public sealed record ContextIndexDefinition(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("vector_dim"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? VectorDimensions = null,
    [property: JsonPropertyName("distance_metric"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DistanceMetric = null,
    [property: JsonPropertyName("source_field"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceField = null,
    [property: JsonPropertyName("embedding_model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EmbeddingModel = null,
    [property: JsonPropertyName("weight"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Weight = null,
    [property: JsonPropertyName("no_stem"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? NoStem = null,
    [property: JsonPropertyName("sortable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Sortable = null);

/// <summary>An entity relationship.</summary>
public sealed record ContextRelationshipDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("source_field")] string SourceField);

/// <summary>Raised when annotated or fluent model metadata is invalid.</summary>
public sealed class ContextModelValidationException : Exception
{
    /// <summary>Creates a validation exception.</summary>
    public ContextModelValidationException(string message) : base(message) { }
}
