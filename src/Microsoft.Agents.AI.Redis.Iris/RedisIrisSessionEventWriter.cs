// Copyright (c) Redis, Inc.

using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Identifies the provider permitted to persist session events.</summary>
public enum RedisIrisEventWriter
{
    /// <summary>The chat-history provider owns event persistence.</summary>
    ChatHistoryProvider,

    /// <summary>The long-term-memory provider owns event persistence.</summary>
    MemoryProvider,
}

/// <summary>Coordinates session-event persistence across composed Iris providers.</summary>
public sealed class RedisIrisSessionEventWriter
{
    private readonly IRedisAgentMemoryClient _client;
    private readonly RedisIrisMessageMapper _mapper;

    /// <summary>Initializes a designated event writer.</summary>
    public RedisIrisSessionEventWriter(
        IRedisAgentMemoryClient client,
        RedisIrisEventWriter designatedWriter,
        RedisIrisMessageMapper? mapper = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        this._client = client;
        this._mapper = mapper ?? new RedisIrisMessageMapper();
        this.DesignatedWriter = designatedWriter;
    }

    /// <summary>Gets the provider allowed to write events.</summary>
    public RedisIrisEventWriter DesignatedWriter { get; }

    /// <summary>Writes supported messages when the caller is the configured owner.</summary>
    /// <returns>The number of events written.</returns>
    public async ValueTask<int> WriteAsync(
        RedisIrisEventWriter caller,
        IEnumerable<ChatMessage> messages,
        RedisIrisProviderState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(state);
        if (caller != this.DesignatedWriter)
        {
            return 0;
        }

        var count = 0;
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = this._mapper.ToSessionEventRequest(message, state.SessionId, state.ActorId, state.AgentId);
            if (request is null)
            {
                continue;
            }

            await this._client.AddSessionEventAsync(request, cancellationToken).ConfigureAwait(false);
            count++;
        }

        return count;
    }
}
