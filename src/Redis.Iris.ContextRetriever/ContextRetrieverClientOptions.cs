namespace Redis.Iris.ContextRetriever;

/// <summary>Connection settings for a Redis Iris Context Retriever local MCP endpoint.</summary>
public sealed class ContextRetrieverClientOptions
{
    /// <summary>The absolute local MCP endpoint.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>The HTTP header used to carry the agent key.</summary>
    public string AgentKeyHeaderName { get; init; } = "Authorization";

    /// <summary>The value prefix used for the agent key header.</summary>
    public string AgentKeyScheme { get; init; } = "Bearer";

    /// <summary>Maximum time allowed to connect and initialize MCP.</summary>
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Maximum time allowed to discover the authorized tool set.</summary>
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Maximum time allowed for one tool execution.</summary>
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum concurrent tool executions in one authorized protocol session.</summary>
    public int MaximumConcurrentToolExecutions { get; init; } = 8;

    internal void Validate()
    {
        if (!Endpoint.IsAbsoluteUri || (Endpoint.Scheme != Uri.UriSchemeHttps && Endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Context Retriever endpoint must be an absolute HTTP or HTTPS URI.", nameof(Endpoint));
        }

        if (Endpoint.Scheme == Uri.UriSchemeHttp && !Endpoint.IsLoopback)
        {
            throw new ArgumentException("Unencrypted Context Retriever endpoints are allowed only on loopback.", nameof(Endpoint));
        }

        if (string.IsNullOrWhiteSpace(AgentKeyHeaderName))
        {
            throw new ArgumentException("Agent key header name is required.", nameof(AgentKeyHeaderName));
        }

        if (ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
        }
        if (DiscoveryTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(DiscoveryTimeout));
        if (ExecutionTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ExecutionTimeout));
        if (MaximumConcurrentToolExecutions <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentToolExecutions));
    }
}
