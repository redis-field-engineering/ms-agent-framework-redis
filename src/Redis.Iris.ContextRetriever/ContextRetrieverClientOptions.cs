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
    }
}
