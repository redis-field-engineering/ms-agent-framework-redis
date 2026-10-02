using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever.Modeling;

namespace Redis.Iris.ContextRetriever;

/// <summary>Optional application-facing typed access over an authorized Context Retriever MCP session.</summary>
/// <remarks>This is separate from the Agent Framework provider; it never broadens the server-authorized tool surface.</remarks>
public sealed class ContextRetrieverTypedClient
{
    private readonly IContextRetrieverClient _client;
    private readonly ContextDataModel _model;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly Func<Type, string> _entityNameResolver;

    /// <summary>Creates a typed client over an existing explicitly owned MCP session.</summary>
    public ContextRetrieverTypedClient(
        IContextRetrieverClient client,
        ContextDataModel model,
        JsonSerializerOptions? serializerOptions = null,
        Func<Type, string>? entityNameResolver = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _serializerOptions = serializerOptions ?? new(JsonSerializerDefaults.Web);
        _entityNameResolver = entityNameResolver ?? ContextModelCompiler.GetEntityName;
    }

    /// <summary>Invokes a server-discovered tool and deserializes its structured or JSON text result.</summary>
    public async Task<TResult?> InvokeAsync<TResult>(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var tool = _client.Tools.SingleOrDefault(candidate => candidate.Name == toolName)
            ?? throw new ContextRetrieverDiscoveryException($"Authorized Context Retriever tool '{toolName}' was not discovered.");
        var result = await tool.InvokeAsync(new AIFunctionArguments(arguments.ToDictionary(pair => pair.Key, pair => pair.Value)), cancellationToken).ConfigureAwait(false);
        var payload = ExtractPayload(result, toolName);
        return payload.Deserialize<TResult>(_serializerOptions);
    }

    /// <summary>Uses the generated get-by-key tool for an entity with exactly one key component.</summary>
    public Task<TEntity?> GetByIdAsync<TEntity>(object id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var entityName = _entityNameResolver(typeof(TEntity));
        var entity = _model.Entities.SingleOrDefault(candidate => candidate.Name == entityName)
            ?? throw new ContextModelValidationException($"Entity '{entityName}' is not present in the configured DataModel.");
        var key = entity.Fields.Where(field => field.IsKeyComponent).ToArray();
        if (key.Length != 1) throw new ContextModelValidationException($"{entityName}: GetByIdAsync requires exactly one key component.");
        var toolName = $"get_{SnakeCase(entity.Name)}_by_{SnakeCase(key[0].Name)}";
        return InvokeAsync<TEntity>(toolName, new Dictionary<string, object?> { [key[0].Name] = id }, cancellationToken);
    }

    private static JsonElement ExtractPayload(object? result, string toolName)
    {
        if (result is not JsonElement root) root = JsonSerializer.SerializeToElement(result);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new ContextRetrieverException($"Context Retriever tool '{toolName}' returned an application error.");
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("structuredContent", out var structured) && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            return structured;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" && block.TryGetProperty("text", out var text))
                {
                    try { return JsonDocument.Parse(text.GetString() ?? "null").RootElement.Clone(); }
                    catch (JsonException ex) { throw new ContextRetrieverException($"Context Retriever tool '{toolName}' returned non-JSON text.", ex); }
                }
            }
        }
        return root;
    }

    private static string SnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 && value[index - 1] != '_') builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
