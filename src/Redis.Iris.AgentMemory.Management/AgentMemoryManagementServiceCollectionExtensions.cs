// Copyright (c) Redis, Inc.

using Microsoft.Extensions.DependencyInjection;

namespace Redis.Iris.AgentMemory.Management;

/// <summary>Explicit opt-in registration for the administrative HTTP client.</summary>
public static class AgentMemoryManagementServiceCollectionExtensions
{
    /// <summary>Registers management with redirects, ambient cookies and default HTTP logging disabled.</summary>
    /// <remarks>Use the returned builder to configure timeouts. Never expose this client as an agent tool.</remarks>
    public static IHttpClientBuilder AddRedisAgentMemoryManagement(this IServiceCollection services,
        AgentMemoryManagementClientOptions options, IAgentMemoryManagementCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        options.GetBaseUri();
        return services.AddHttpClient("Redis.Iris.AgentMemory.Management")
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(["Authorization"])
            .AddTypedClient<IAgentMemoryManagementClient>((http, _) => new AgentMemoryManagementClient(http, options, credentialProvider));
    }
}
