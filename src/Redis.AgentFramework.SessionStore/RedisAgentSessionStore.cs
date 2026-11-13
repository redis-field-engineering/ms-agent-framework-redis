// Copyright (c) Redis, Inc.

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using StackExchange.Redis;

namespace Redis.AgentFramework.SessionStore;

/// <summary>Persists complete serialized AgentSession snapshots directly in Redis.</summary>
/// <remarks>
/// The caller owns the shared Redis connection. Register through WithRedisSessionStore to enable
/// framework caller isolation. Direct use requires an already authorized, scoped sessionStoreId.
/// Saves atomically replace one snapshot; concurrent saves use last-writer-wins semantics.
/// This store does not coordinate agent execution or atomically commit external history and tool effects.
/// </remarks>
public sealed class RedisAgentSessionStore : AgentSessionStore
{
    private readonly IDatabase _database;
    private readonly string _keyPrefix;
    private readonly TimeSpan? _expiry;

    /// <summary>Creates a session store using a database from a shared connection multiplexer.</summary>
    public RedisAgentSessionStore(IDatabase database, RedisAgentSessionStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        options ??= new();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
        if (options.Expiry is { } expiry && expiry < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options), "Expiry must be at least one millisecond, or null.");

        _database = database;
        _keyPrefix = options.KeyPrefix;
        _expiry = options.Expiry;
    }

    /// <inheritdoc />
    public override async ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, sessionStoreId);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        var state = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // SET with expiration commits the value and its retention together.
        var expiry = _expiry is { } ttl ? new Expiration(ttl) : Expiration.Default;
        var saved = await _database.StringSetAsync(key, state.GetRawText(), expiry).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!saved) throw new InvalidOperationException("Redis did not save the agent session.");
    }

    /// <inheritdoc />
    public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, sessionStoreId);
        cancellationToken.ThrowIfCancellationRequested();
        var state = await _database.StringGetAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (state.IsNull)
            return await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        // Never turn corrupt data or Redis failures into a fresh conversation.
        using var document = JsonDocument.Parse((string)state!);
        return await agent.DeserializeSessionAsync(document.RootElement, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask DeleteSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
    {
        var key = GetKey(agent, sessionStoreId);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.KeyDeleteAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private RedisKey GetKey(AIAgent agent, string sessionStoreId)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(sessionStoreId);
        // JSON frames both opaque IDs unambiguously; hashing keeps caller identifiers out of keys.
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { agent.Id, sessionStoreId });
        return _keyPrefix + Convert.ToHexString(SHA256.HashData(identity));
    }
}
