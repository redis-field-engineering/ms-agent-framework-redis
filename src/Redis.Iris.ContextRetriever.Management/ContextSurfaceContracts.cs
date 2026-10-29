using System.Text.Json.Serialization;
using Redis.Iris.ContextRetriever.Modeling;

namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Administrative creation settings. Keep Redis credentials out of logs and agent context.</summary>
public sealed class ContextSurfaceDeployment
{
    /// <summary>Surface name, from 1 to 255 characters.</summary>
    public required string Name { get; init; }
    /// <summary>Surface description, at most 1000 characters.</summary>
    public string Description { get; init; } = "";
    /// <summary>Redis address in host:port form, reachable by the service.</summary>
    public required string RedisAddress { get; init; }
    /// <summary>Optional Redis ACL username.</summary>
    public string? RedisUsername { get; init; }
    /// <summary>Redis password sent only in the administrative HTTPS request body.</summary>
    [JsonIgnore]
    public string? RedisPassword { get; init; }
    /// <summary>Redis logical database number.</summary>
    public int RedisDatabase { get; init; }
    /// <summary>Whether the service should use TLS to connect to Redis.</summary>
    public bool RedisTls { get; init; } = true;
    /// <summary>Optional surface metadata.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    internal void Validate()
    {
        ContextSurfaceUpdate.ValidateName(Name);
        ContextSurfaceUpdate.ValidateDescription(Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(RedisAddress);
        if (RedisDatabase < 0) throw new ArgumentOutOfRangeException(nameof(RedisDatabase));
    }
}

/// <summary>Surface changes. Null properties are omitted; a supplied model replaces the whole model.</summary>
public sealed class ContextSurfaceUpdate
{
    /// <summary>Optional replacement name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    /// <summary>Optional replacement description. An empty string clears the description.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
    /// <summary>Optional replacement metadata. An empty dictionary clears metadata.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    /// <summary>Optional complete replacement DataModel, not a patch or data migration.</summary>
    [JsonPropertyName("data_model")]
    public ContextDataModel? DataModel { get; init; }

    internal void Validate()
    {
        if (Name is not null) ValidateName(Name);
        if (Description is not null) ValidateDescription(Description);
        if (Name is null && Description is null && Metadata is null && DataModel is null)
            throw new ArgumentException("At least one surface update property is required.");
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 255) throw new ArgumentException("Surface name must not exceed 255 characters.", nameof(name));
    }

    internal static void ValidateDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.Length > 1000) throw new ArgumentException("Surface description must not exceed 1000 characters.", nameof(description));
    }
}

/// <summary>Surface metadata returned by the service. Unknown response fields are not retained.</summary>
public sealed record ContextSurface(
    [property: JsonPropertyName("id")] string SurfaceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("data_model")] ContextDataModel? DataModel = null);
