using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Connection policy shared by dynamic and fixed-identity Context Retriever entry points.</summary>
public sealed class RedisContextRetrieverOptions
{
    /// <summary>Agent-key header name documented by the deployed endpoint.</summary>
    public string AgentKeyHeaderName { get; init; } = "X-API-Key";
    /// <summary>Agent-key header scheme; use an empty value for an unprefixed header.</summary>
    public string AgentKeyScheme { get; init; } = "";
    /// <summary>Connect and MCP initialization timeout.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Authorized tool discovery timeout.</summary>
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Per-tool execution timeout.</summary>
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Per-profile concurrent tool execution limit.</summary>
    public int MaximumConcurrentToolExecutions { get; init; } = 8;
}

/// <summary>Supplies principal-authorized Redis Context Retriever tools for each invocation.</summary>
public sealed class RedisContextRetrieverProvider : AIContextProvider, IAsyncDisposable
{
    private readonly IContextRetrieverAccessResolver _accessResolver;
    private readonly ContextRetrieverClientCache _clients;
    private readonly RedisContextRetrieverOptions _options;
    private readonly bool _ownsClients;

    /// <summary>Creates a provider over a shared principal-isolated client cache.</summary>
    public RedisContextRetrieverProvider(
        IContextRetrieverAccessResolver accessResolver,
        ContextRetrieverClientCache clients,
        RedisContextRetrieverOptions? options = null,
        bool ownsClientCache = false)
    {
        _accessResolver = accessResolver ?? throw new ArgumentNullException(nameof(accessResolver));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options ?? new();
        _ownsClients = ownsClientCache;
    }

    /// <summary>The provider intentionally stores no credential or identity material in session state.</summary>
    public override IReadOnlyList<string> StateKeys => [];

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var profile = await _accessResolver.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        var credential = await profile.CredentialProvider.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        credential.Validate();
        var delegatedToken = profile.DelegatedTokenProvider is null
            ? null
            : await profile.DelegatedTokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        delegatedToken?.Validate();
        var client = await _clients.GetAsync(profile, credential, delegatedToken, new ContextRetrieverClientOptions
        {
            Endpoint = profile.Endpoint,
            AgentKeyHeaderName = _options.AgentKeyHeaderName,
            AgentKeyScheme = _options.AgentKeyScheme,
            ConnectionTimeout = _options.ConnectionTimeout,
            DiscoveryTimeout = _options.DiscoveryTimeout,
            ExecutionTimeout = _options.ExecutionTimeout,
            MaximumConcurrentToolExecutions = _options.MaximumConcurrentToolExecutions,
        }, cancellationToken).ConfigureAwait(false);

        var boundArguments = profile.BoundToolArguments ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>();
        var boundFilterValues = profile.BoundFilterValues ?? new Dictionary<string, object?>();
        return new AIContext
        {
            Tools = boundArguments.Count == 0 && boundFilterValues.Count == 0
                ? client.Tools
                : client.Tools.Select(tool => (AIFunction)new AuthorizationBoundaryAIFunction(
                    tool,
                    boundArguments.TryGetValue(tool.Name, out var arguments) ? arguments : null,
                    boundFilterValues)).ToArray(),
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _ownsClients ? _clients.DisposeAsync() : ValueTask.CompletedTask;

    private sealed class AuthorizationBoundaryAIFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly IReadOnlyDictionary<string, object?> _boundArguments;
        private readonly IReadOnlyDictionary<string, object?> _boundFilterValues;
        private readonly JsonElement _jsonSchema;

        public AuthorizationBoundaryAIFunction(
            AIFunction inner,
            IReadOnlyDictionary<string, object?>? boundArguments,
            IReadOnlyDictionary<string, object?> boundFilterValues)
        {
            _inner = inner;
            _boundArguments = boundArguments ?? new Dictionary<string, object?>();
            _boundFilterValues = boundFilterValues;
            _jsonSchema = RewriteSchema(inner.JsonSchema, _boundArguments.Keys);
        }

        public override string Name => _inner.Name;
        public override string Description => _inner.Description +
            (_boundArguments.Count == 0 ? "" : " Authorized identity arguments are supplied automatically; do not provide them.") +
            (_boundFilterValues.Count == 0 ? "" : $" Identity filter values are canonicalized by the application for: {string.Join(", ", _boundFilterValues.Keys)}.");
        public override JsonElement JsonSchema => _jsonSchema;
        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
        public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
        public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (_boundArguments.Count == 0 && _boundFilterValues.Count == 0)
                return _inner.InvokeAsync(arguments, cancellationToken);

            var effectiveArguments = arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var (name, boundValue) in _boundArguments) effectiveArguments[name] = boundValue;
            if (_boundFilterValues.Count != 0 &&
                effectiveArguments.TryGetValue("tag_conditions", out var suppliedConditions) &&
                suppliedConditions is not null &&
                JsonNode.Parse(JsonSerializer.Serialize(suppliedConditions, _inner.JsonSerializerOptions)) is JsonArray conditions)
            {
                foreach (var condition in conditions.OfType<JsonObject>())
                {
                    if (condition["field"]?.GetValue<string>() is { } field &&
                        _boundFilterValues.TryGetValue(field, out var trustedValue))
                    {
                        condition["value"] = JsonSerializer.SerializeToNode(trustedValue, _inner.JsonSerializerOptions);
                    }
                }
                effectiveArguments["tag_conditions"] = JsonSerializer.SerializeToElement(conditions, _inner.JsonSerializerOptions);
            }
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
}
