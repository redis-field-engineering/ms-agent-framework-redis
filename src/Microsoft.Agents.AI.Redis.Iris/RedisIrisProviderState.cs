// Copyright (c) Redis, Inc.

using System.Text.Json.Serialization;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Identities persisted with one Agent Framework session.</summary>
public sealed class RedisIrisProviderState
{
    /// <summary>Initializes session-scoped Redis Iris state.</summary>
    [JsonConstructor]
    public RedisIrisProviderState(string sessionId, string ownerId, string agentId, string? actorId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        this.SessionId = sessionId;
        this.OwnerId = ownerId;
        this.AgentId = agentId;
        this.ActorId = string.IsNullOrWhiteSpace(actorId) ? ownerId : actorId;
    }

    /// <summary>Gets the Redis Agent Memory session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Gets the authorized memory owner identifier.</summary>
    public string OwnerId { get; }

    /// <summary>Gets the user actor identifier written to session events. This is distinct from the storage owner.</summary>
    public string ActorId { get; }

    /// <summary>Gets the stable agent actor identifier.</summary>
    public string AgentId { get; }
}
