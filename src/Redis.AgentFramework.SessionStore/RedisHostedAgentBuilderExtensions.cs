// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Redis.AgentFramework.SessionStore;

/// <summary>Registers Redis session storage for hosted agents.</summary>
public static class RedisHostedAgentBuilderExtensions
{
    /// <summary>Uses the shared IConnectionMultiplexer registered in DI to persist agent sessions.</summary>
    /// <param name="builder">The hosted agent builder.</param>
    /// <param name="configure">Optional storage and retention configuration.</param>
    /// <param name="database">Redis database number, or -1 for the connection's configured default.</param>
    /// <param name="withIsolation">Whether to use the framework's caller isolation decorator. Defaults to true.</param>
    /// <returns>The hosted agent builder.</returns>
    /// <remarks>Multi-user hosts must register an AgentIsolationKeyProvider backed by authenticated identity.</remarks>
    public static IHostedAgentBuilder WithRedisSessionStore(
        this IHostedAgentBuilder builder,
        Action<RedisAgentSessionStoreOptions>? configure = null,
        int database = -1,
        bool withIsolation = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new RedisAgentSessionStoreOptions();
        configure?.Invoke(options);
        return builder.WithSessionStore((services, _) => new RedisAgentSessionStore(
            services.GetRequiredService<IConnectionMultiplexer>().GetDatabase(database), options),
            withIsolation: withIsolation);
    }
}
