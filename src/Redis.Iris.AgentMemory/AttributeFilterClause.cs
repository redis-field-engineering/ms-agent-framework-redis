// Copyright (c) Redis, Inc.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Redis.Iris.AgentMemory;

/// <summary>One typed equality or string-list containment condition on a custom memory attribute.</summary>
/// <remarks>Matches the Iris AttributeFilterClause union. Object paths, ranges, and numeric lists are not supported.</remarks>
[JsonConverter(typeof(AttributeFilterClauseJsonConverter))]
public sealed class AttributeFilterClause
{
    internal string Member { get; }
    internal JsonElement Value { get; }

    private AttributeFilterClause(string member, JsonElement value)
    {
        Member = member;
        Value = value;
    }

    /// <summary>Matches a string attribute exactly.</summary>
    public static AttributeFilterClause StringEquals(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new("string", JsonSerializer.SerializeToElement(value));
    }

    /// <summary>Matches a numeric attribute using the service's double-precision numeric representation.</summary>
    /// <remarks>Integers beyond 2^53 may lose precision. NaN and infinity are rejected.</remarks>
    public static AttributeFilterClause NumberEquals(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A finite number is required.");
        return new("number", JsonSerializer.SerializeToElement(value));
    }

    /// <summary>Matches a boolean attribute, including false.</summary>
    public static AttributeFilterClause BooleanEquals(bool value) => new("boolean", JsonSerializer.SerializeToElement(value));

    /// <summary>Matches a string-list attribute containing every supplied value. Copies the values.</summary>
    public static AttributeFilterClause ContainsAll(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => value is null)) throw new ArgumentException("List values cannot be null.", nameof(values));
        return new("list", JsonSerializer.SerializeToElement(values));
    }

    internal static void Validate(IReadOnlyDictionary<string, AttributeFilterClause>? attributes)
    {
        if (attributes is null) return;
        foreach (var (name, clause) in attributes)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || !char.IsAsciiLetter(name[0]) ||
                name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
                throw new ArgumentException("Attribute names must be 1-64 characters: a letter followed by letters, digits, underscores or hyphens.", nameof(attributes));
            if (clause is null) throw new ArgumentException("Attribute clauses cannot be null.", nameof(attributes));
        }
    }
}

internal sealed class AttributeFilterClauseJsonConverter : JsonConverter<AttributeFilterClause>
{
    public override AttributeFilterClause Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1)
            throw new JsonException("An attribute clause must contain exactly one typed member.");
        var property = root.EnumerateObject().Single();
        return property.Name switch
        {
            "string" when property.Value.ValueKind == JsonValueKind.String => AttributeFilterClause.StringEquals(property.Value.GetString()!),
            "number" when property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number) && double.IsFinite(number) => AttributeFilterClause.NumberEquals(number),
            "boolean" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False => AttributeFilterClause.BooleanEquals(property.Value.GetBoolean()),
            "list" when property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) =>
                AttributeFilterClause.ContainsAll(property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray()),
            _ => throw new JsonException("Unsupported attribute clause or value type."),
        };
    }

    public override void Write(Utf8JsonWriter writer, AttributeFilterClause value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(value.Member);
        value.Value.WriteTo(writer);
        writer.WriteEndObject();
    }
}
