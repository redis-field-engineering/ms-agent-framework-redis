using Microsoft.Extensions.AI;
using Redis.Iris.ContextRetriever;

namespace Microsoft.Agents.AI.Redis.Iris;

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
            : await profile.DelegatedTokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
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
            return new ContextRetrieverToolLease(client, AuthorizationBoundaryAIFunction.Apply(client.Tools, profile.BoundToolArguments));
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
    internal ContextRetrieverToolLease(IContextRetrieverClient client, IReadOnlyList<AIFunction> tools) { _client = client; _tools = tools; }

    /// <summary>Server-defined Context Retriever tools.</summary>
    public IReadOnlyList<AIFunction> Tools => _client is not null ? _tools : throw new ObjectDisposedException(nameof(ContextRetrieverToolLease));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }
}
