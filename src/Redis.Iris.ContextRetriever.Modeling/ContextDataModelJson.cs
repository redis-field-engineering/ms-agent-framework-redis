using System.Text.Json;
using System.Text.Json.Serialization;

namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>Deterministic JSON serialization for the ctxctl DataModel contract.</summary>
public static class ContextDataModelJson
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>Serializes a model to stable UTF-8 JSON.</summary>
    public static string Serialize(ContextDataModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return JsonSerializer.Serialize(model, s_options) + Environment.NewLine;
    }

    /// <summary>Reads a DataModel JSON artifact.</summary>
    public static ContextDataModel Deserialize(string json) =>
        JsonSerializer.Deserialize<ContextDataModel>(json, s_options)
        ?? throw new ContextModelValidationException("DataModel JSON was empty.");

    /// <summary>Writes a DataModel artifact atomically, replacing an existing file only after serialization succeeds.</summary>
    public static async Task WriteAsync(ContextDataModel model, string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, Serialize(model), cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

/// <summary>Classifies model changes before a surface update.</summary>
public static class ContextDataModelComparer
{
    /// <summary>Compares a deployed/baseline model with a proposed model.</summary>
    public static IReadOnlyList<ContextModelChange> Compare(ContextDataModel baseline, ContextDataModel proposed)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(proposed);
        var changes = new List<ContextModelChange>();
        var beforeEntities = baseline.Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        var afterEntities = proposed.Entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        foreach (var removed in beforeEntities.Keys.Except(afterEntities.Keys, StringComparer.Ordinal)) changes.Add(new(ContextModelChangeKind.Destructive, $"Entity removed: {removed}"));
        foreach (var added in afterEntities.Keys.Except(beforeEntities.Keys, StringComparer.Ordinal)) changes.Add(new(ContextModelChangeKind.Additive, $"Entity added: {added}"));
        foreach (var name in beforeEntities.Keys.Intersect(afterEntities.Keys, StringComparer.Ordinal))
        {
            var before = beforeEntities[name]; var after = afterEntities[name];
            if (before.RedisKeyTemplate != after.RedisKeyTemplate) changes.Add(new(ContextModelChangeKind.Destructive, $"Key template changed: {name}"));
            if (JsonSerializer.Serialize(before.AclFieldMappings ?? []) != JsonSerializer.Serialize(after.AclFieldMappings ?? []))
                changes.Add(new(ContextModelChangeKind.Destructive, $"ACL field mappings changed: {name}"));
            var beforeRelationships = before.Relationships.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var afterRelationships = after.Relationships.ToDictionary(item => item.Name, StringComparer.Ordinal);
            foreach (var removed in beforeRelationships.Keys.Except(afterRelationships.Keys, StringComparer.Ordinal))
                changes.Add(new(ContextModelChangeKind.Destructive, $"Relationship removed: {name}.{removed}"));
            foreach (var added in afterRelationships.Keys.Except(beforeRelationships.Keys, StringComparer.Ordinal))
                changes.Add(new(ContextModelChangeKind.Additive, $"Relationship added: {name}.{added}"));
            foreach (var relationshipName in beforeRelationships.Keys.Intersect(afterRelationships.Keys, StringComparer.Ordinal))
            {
                var oldRelationship = beforeRelationships[relationshipName];
                var newRelationship = afterRelationships[relationshipName];
                if (oldRelationship.Target != newRelationship.Target || oldRelationship.SourceField != newRelationship.SourceField)
                    changes.Add(new(ContextModelChangeKind.Destructive, $"Relationship contract changed: {name}.{relationshipName}"));
            }
            var beforeFields = before.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            var afterFields = after.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            foreach (var removed in beforeFields.Keys.Except(afterFields.Keys, StringComparer.Ordinal)) changes.Add(new(ContextModelChangeKind.Destructive, $"Field removed: {name}.{removed}"));
            foreach (var added in afterFields.Keys.Except(beforeFields.Keys, StringComparer.Ordinal)) changes.Add(new(ContextModelChangeKind.Additive, $"Field added: {name}.{added}"));
            foreach (var fieldName in beforeFields.Keys.Intersect(afterFields.Keys, StringComparer.Ordinal))
            {
                var oldField = beforeFields[fieldName]; var newField = afterFields[fieldName];
                if (oldField.Type != newField.Type || oldField.IsKeyComponent != newField.IsKeyComponent ||
                    JsonSerializer.Serialize(oldField.RedisIndices) != JsonSerializer.Serialize(newField.RedisIndices))
                    changes.Add(new(ContextModelChangeKind.Destructive, $"Field contract changed: {name}.{fieldName}"));
                // Null is unrestricted. Compare as sets so reordering is not a contract change.
                var oldValues = oldField.AllowedValues?.ToHashSet(StringComparer.Ordinal);
                var newValues = newField.AllowedValues?.ToHashSet(StringComparer.Ordinal);
                if (oldValues is null ? newValues is not null : newValues is null || !oldValues.SetEquals(newValues))
                {
                    var kind = newValues is null || oldValues is not null && oldValues.IsSubsetOf(newValues)
                        ? ContextModelChangeKind.Additive : ContextModelChangeKind.Destructive;
                    changes.Add(new(kind, $"Allowed values changed: {name}.{fieldName}"));
                }
            }
        }
        return changes.OrderBy(change => change.Description, StringComparer.Ordinal).ToArray();
    }
}

/// <summary>Change safety classification.</summary>
public enum ContextModelChangeKind
{
    /// <summary>Adds a new surface capability.</summary>
    Additive,
    /// <summary>May remove or reinterpret an existing surface capability.</summary>
    Destructive,
}

/// <summary>One human-reviewable DataModel change.</summary>
public sealed record ContextModelChange(ContextModelChangeKind Kind, string Description);
