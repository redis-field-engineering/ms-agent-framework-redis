namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Connection settings for the administrative API, independent of runtime MCP credentials.</summary>
public sealed class ContextRetrieverManagementClientOptions
{
    /// <summary>Control-plane base URL, including any gateway prefix (for example /context-surfaces), not /mcp.</summary>
    public required Uri Endpoint { get; init; }

    internal Uri GetBaseUri()
    {
        ArgumentNullException.ThrowIfNull(Endpoint);
        if (!Endpoint.IsAbsoluteUri || Endpoint.Scheme != Uri.UriSchemeHttps && Endpoint.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("The management endpoint must be an absolute HTTP or HTTPS URL.");
        if (Endpoint.Scheme == Uri.UriSchemeHttp && !Endpoint.IsLoopback)
            throw new ArgumentException("Unencrypted management endpoints are allowed only on loopback.");
        if (Endpoint.UserInfo.Length != 0 || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0)
            throw new ArgumentException("The management endpoint must not contain credentials, a query, or a fragment.");
        if (Endpoint.AbsolutePath.TrimEnd('/').EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the control-plane endpoint, not the runtime MCP endpoint.");
        return new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/");
    }
}
