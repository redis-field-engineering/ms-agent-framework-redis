// Copyright (c) Redis, Inc.

namespace Redis.AgentFramework.SessionStore;

/// <summary>Configures Redis storage for serialized Agent Framework sessions.</summary>
public sealed class RedisAgentSessionStoreOptions
{
    /// <summary>Gets or sets the application-specific key prefix.</summary>
    public string KeyPrefix { get; set; } = "agent-framework:sessions:";

    /// <summary>Gets or sets the expiration applied on each save. Null retains sessions until deleted.</summary>
    /// <remarks>Reads do not refresh expiration. Match retention to the application's external history store.</remarks>
    public TimeSpan? Expiry { get; set; }
}
