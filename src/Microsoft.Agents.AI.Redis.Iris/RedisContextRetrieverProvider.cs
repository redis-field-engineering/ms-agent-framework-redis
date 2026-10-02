using Microsoft.Agents.AI;
using Redis.Iris.ContextRetriever;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Connection policy shared by dynamic and fixed-identity Context Retriever entry points.</summary>
public sealed class RedisContextRetrieverOptions
{
    /// <summary>Agent-key header name documented by the deployed endpoint.</summary>
    public string AgentKeyHeaderName { get; init; } = "Authorization";
    /// <summary>Agent-key header scheme; use an empty value for an unprefixed header.</summary>
    public string AgentKeyScheme { get; init; } = "Bearer";
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
        var client = await _clients.GetAsync(profile, credential, new ContextRetrieverClientOptions
        {
            Endpoint = profile.Endpoint,
            AgentKeyHeaderName = _options.AgentKeyHeaderName,
            AgentKeyScheme = _options.AgentKeyScheme,
            ConnectionTimeout = _options.ConnectionTimeout,
            DiscoveryTimeout = _options.DiscoveryTimeout,
            ExecutionTimeout = _options.ExecutionTimeout,
            MaximumConcurrentToolExecutions = _options.MaximumConcurrentToolExecutions,
        }, cancellationToken).ConfigureAwait(false);

        return new AIContext { Tools = client.Tools };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _ownsClients ? _clients.DisposeAsync() : ValueTask.CompletedTask;
}
