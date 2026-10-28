using Microsoft.Extensions.AI;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.AI.Redis.Iris;

internal sealed class AuthorizationBoundaryAIFunction : AIFunction
{
    internal static IReadOnlyList<AIFunction> Apply(IReadOnlyList<AIFunction> tools,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? bindings) =>
        bindings is null || bindings.Count == 0 ? tools : tools.Select(tool => (AIFunction)new AuthorizationBoundaryAIFunction(
            tool, bindings.TryGetValue(tool.Name, out var arguments) ? arguments : null)).ToArray();

    private readonly AIFunction _inner;
    private readonly IReadOnlyDictionary<string, object?> _boundArguments;
    private readonly JsonElement _jsonSchema;

    public AuthorizationBoundaryAIFunction(
        AIFunction inner,
        IReadOnlyDictionary<string, object?>? boundArguments)
    {
        _inner = inner;
        _boundArguments = boundArguments is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(boundArguments, StringComparer.Ordinal);
        _jsonSchema = RewriteSchema(inner.JsonSchema, _boundArguments.Keys);
    }

    public override string Name => _inner.Name;
    public override string Description => _boundArguments.Count == 0
        ? _inner.Description
        : _inner.Description + " Authorized identity arguments are supplied automatically; do not provide them.";
    public override JsonElement JsonSchema => _jsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_boundArguments.Count == 0) return _inner.InvokeAsync(arguments, cancellationToken);

        var effectiveArguments = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (name, boundValue) in _boundArguments) effectiveArguments[name] = boundValue;
        return _inner.InvokeAsync(new AIFunctionArguments(effectiveArguments), cancellationToken);
    }

    private static JsonElement RewriteSchema(
        JsonElement schema,
        IEnumerable<string> boundArgumentNames)
    {
        var root = JsonNode.Parse(schema.GetRawText());
        if (root?["properties"] is JsonObject properties)
        {
            foreach (var name in boundArgumentNames) properties.Remove(name);
        }
        if (root?["required"] is JsonArray required)
        {
            for (var index = required.Count - 1; index >= 0; index--)
            {
                if (required[index] is JsonValue item && item.TryGetValue<string>(out var name) && boundArgumentNames.Contains(name, StringComparer.Ordinal))
                    required.RemoveAt(index);
            }
        }

        return root is null ? schema.Clone() : JsonSerializer.SerializeToElement(root, _innerSchemaOptions);
    }

    private static readonly JsonSerializerOptions _innerSchemaOptions = new();
}
