using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;
using System.Reflection;
using System.Text.Json;

namespace Redis.Iris.AgentFramework;

/// <summary>Connects Context Retriever tools for a single fixed service identity.</summary>
/// <remarks>Every user of the receiving agent gets the fixed profile's full server-authorized scope.</remarks>
public static class RedisContextRetrieverTools
{
    /// <summary>Connects and discovers tools. The returned lease must live as long as the attached tools.</summary>
    public static async Task<ContextRetrieverToolLease> ConnectAsync(
        ContextRetrieverAccessProfile profile,
        IContextRetrieverClientFactory clientFactory,
        RedisContextRetrieverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(clientFactory);
        profile.Validate();
        var credential = await profile.CredentialProvider.GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        credential.Validate();
        var delegatedToken = profile.DelegatedTokenProvider is null
            ? null
            : await profile.DelegatedTokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ContextRetrieverAccessDeniedException("The configured delegated token provider returned no token.");
        delegatedToken?.Validate();
        var policy = options ?? new();
        var client = await clientFactory.ConnectAsync(new()
        {
            Endpoint = profile.Endpoint,
            AgentKeyHeaderName = policy.AgentKeyHeaderName,
            AgentKeyScheme = policy.AgentKeyScheme,
            ConnectionTimeout = policy.ConnectionTimeout,
            DiscoveryTimeout = policy.DiscoveryTimeout,
            ExecutionTimeout = policy.ExecutionTimeout,
            MaximumConcurrentToolExecutions = policy.MaximumConcurrentToolExecutions,
        }, credential.AgentKey, delegatedToken?.Token, cancellationToken).ConfigureAwait(false);
        try
        {
            // Initialization may have outlasted the supplied authorization lifetime.
            credential.Validate();
            delegatedToken?.Validate();
            return new ContextRetrieverToolLease(client, AuthorizationBoundaryAIFunction.Apply(client.Tools, profile.BoundToolArguments), credential, delegatedToken);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Owns the MCP session behind a fixed set of tools.</summary>
public sealed class ContextRetrieverToolLease : IAsyncDisposable
{
    private IContextRetrieverClient? _client;
    private readonly IReadOnlyList<AIFunction> _tools;
    internal ContextRetrieverToolLease(IContextRetrieverClient client, IReadOnlyList<AIFunction> tools,
        ContextRetrieverCredential credential, ContextRetrieverDelegatedToken? token)
    {
        _client = client;
        _tools = tools.Select(tool => (AIFunction)new LifetimeTool(tool, this, credential, token)).ToArray();
    }

    /// <summary>Server-defined Context Retriever tools.</summary>
    public IReadOnlyList<AIFunction> Tools => _client is not null ? _tools : throw new ObjectDisposedException(nameof(ContextRetrieverToolLease));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class LifetimeTool(AIFunction inner, ContextRetrieverToolLease owner,
        ContextRetrieverCredential credential, ContextRetrieverDelegatedToken? token) : AIFunction
    {
        public override string Name => inner.Name;
        public override string Description => inner.Description;
        public override JsonElement JsonSchema => inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;
        public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._client) is null, owner);
            credential.Validate();
            token?.Validate();
            return inner.InvokeAsync(arguments, cancellationToken);
        }
    }
}
