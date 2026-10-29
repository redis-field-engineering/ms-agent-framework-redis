using Microsoft.Extensions.DependencyInjection;

namespace Redis.Iris.ContextRetriever.Management;

/// <summary>Registration for an explicitly opted-in administrative HTTP client.</summary>
public static class ContextRetrieverManagementServiceCollectionExtensions
{
    /// <summary>Registers the management client with redirects, ambient cookies, and default HTTP logging disabled.</summary>
    /// <remarks>Use the returned builder to configure HttpClient.Timeout. Never register admin credentials in an agent host.</remarks>
    public static IHttpClientBuilder AddRedisContextRetrieverManagement(this IServiceCollection services,
        ContextRetrieverManagementClientOptions options, IContextRetrieverManagementCredentialProvider credentialProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentialProvider);
        options.GetBaseUri();
        return services.AddHttpClient("Redis.Iris.ContextRetriever.Management")
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(["X-API-Key"])
            .AddTypedClient<IContextRetrieverManagementClient>((http, _) => new ContextRetrieverManagementClient(http, options, credentialProvider));
    }
}
