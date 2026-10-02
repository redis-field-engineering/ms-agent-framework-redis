using Microsoft.Extensions.DependencyInjection;

namespace Redis.Iris.ContextRetriever.Modeling;

/// <summary>Registers one compiled Context Retriever model for application and deployment code.</summary>
public static class ContextModelServiceCollectionExtensions
{
    /// <summary>Compiles, validates, and registers a singleton DataModel during application startup.</summary>
    public static IServiceCollection AddRedisContextModel(
        this IServiceCollection services,
        string title,
        string description,
        Action<ContextModelBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new ContextModelBuilder(title, description);
        configure(builder);
        services.AddSingleton(builder.Build());
        return services;
    }
}
