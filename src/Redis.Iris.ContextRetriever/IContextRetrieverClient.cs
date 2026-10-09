using Microsoft.Extensions.AI;

namespace Redis.Iris.ContextRetriever;

/// <summary>An initialized local MCP session and its Context Retriever tools.</summary>
public interface IContextRetrieverClient : IAsyncDisposable
{
    /// <summary>The server-defined tools exposed as Microsoft.Extensions.AI functions.</summary>
    IReadOnlyList<AIFunction> Tools { get; }
}

/// <summary>Creates authenticated Context Retriever MCP sessions.</summary>
public interface IContextRetrieverClientFactory
{
    /// <summary>Connects and discovers the tools authorized for the supplied agent key.</summary>
    Task<IContextRetrieverClient> ConnectAsync(
        ContextRetrieverClientOptions options,
        string agentKey,
        CancellationToken cancellationToken = default);

    /// <summary>Connects with an agent key and a bearer JWT delegated for the current application principal.</summary>
    Task<IContextRetrieverClient> ConnectAsync(
        ContextRetrieverClientOptions options,
        string agentKey,
        string? delegatedBearerToken,
        CancellationToken cancellationToken = default) => delegatedBearerToken is null
            ? ConnectAsync(options, agentKey, cancellationToken)
            : throw new NotSupportedException("This Context Retriever client factory does not support delegated bearer tokens.");
}
