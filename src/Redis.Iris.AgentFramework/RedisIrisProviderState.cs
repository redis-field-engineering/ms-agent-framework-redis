// Copyright (c) Redis, Inc.

using System.Text.Json.Serialization;

namespace Redis.Iris.AgentFramework;

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

    /// <summary>Gets the canonical user actor identifier exposed to application and model-facing history. This is distinct from the storage owner.</summary>
    public string ActorId { get; }

    /// <summary>Gets the stable agent actor identifier.</summary>
    public string AgentId { get; }
    /// <summary>Gets the host tenant bound to this session; tenant is not a RAM wire-level filter.</summary>
    public string? TenantId { get; init; }

    /// <summary>Gets the trusted namespace resource ID used for event placement and recall.</summary>
    public string? NamespaceId { get; init; }

    /// <summary>Gets the required topics for recall. Session events do not support topic placement.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

}
