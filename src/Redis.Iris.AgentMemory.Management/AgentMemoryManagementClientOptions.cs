// Copyright (c) Redis, Inc.

namespace Redis.Iris.AgentMemory.Management;

/// <summary>Connection settings for the Iris control-plane v2 API, separate from runtime data-plane settings.</summary>
public sealed class AgentMemoryManagementClientOptions
{
    /// <summary>Gets the control-plane base URL, including any gateway prefix but excluding /iris/v1/agent-memory.</summary>
    /// <remarks>This is not the Redis Cloud account API (/memory-stores), nor a data-plane URL.</remarks>
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
        if (Endpoint.AbsolutePath.Contains("/iris/", StringComparison.OrdinalIgnoreCase) ||
            Endpoint.AbsolutePath.TrimEnd('/').EndsWith("/memory-stores", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the control-plane base URL, without an API resource path.");
        return new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/");
    }
}
