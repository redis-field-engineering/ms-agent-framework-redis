// Copyright (c) Redis, Inc.

using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Microsoft.Agents.AI.Redis.Iris;

/// <summary>Controls conversion between Agent Framework messages and Redis Agent Memory events.</summary>
public sealed class RedisIrisMessageMappingOptions
{
    /// <summary>Gets or sets whether system messages may be stored.</summary>
    public bool StoreSystemMessages { get; set; }

    /// <summary>
    /// Gets or sets an optional projection for messages containing content Redis Agent Memory does not support.
    /// Return <see langword="null"/> or whitespace to skip the message.
    /// </summary>
    public Func<ChatMessage, string?>? UnsupportedContentProjection { get; set; }
}

/// <summary>Maps supported Agent Framework messages to Redis Agent Memory session events.</summary>
public sealed class RedisIrisMessageMapper
{
    private readonly RedisIrisMessageMappingOptions _options;

    /// <summary>Initializes a new mapper.</summary>
    public RedisIrisMessageMapper(RedisIrisMessageMappingOptions? options = null)
    {
        this._options = options ?? new RedisIrisMessageMappingOptions();
    }

    /// <summary>Creates an event request, or returns <see langword="null"/> when the message should be skipped.</summary>
    public AddSessionEventRequest? ToSessionEventRequest(
        ChatMessage message,
        string sessionId,
        string ownerId,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var mapping = this.MapRole(message.Role, ownerId, agentId);
        if (mapping is null)
        {
            AgentMemoryDiagnostics.RecordSkippedMessage("unsupported_role");
            return null;
        }

        string? text;
        if (message.Contents.Count > 0 && message.Contents.All(static content => content is TextContent))
        {
            text = message.Text;
        }
        else
        {
            text = this._options.UnsupportedContentProjection?.Invoke(message);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AgentMemoryDiagnostics.RecordSkippedMessage("unsupported_or_empty_content");
            return null;
        }

        return new AddSessionEventRequest
        {
            SessionId = sessionId,
            ActorId = mapping.Value.ActorId,
            Role = mapping.Value.Role,
            Content = [new SessionEventContent(text)],
            CreatedAt = (message.CreatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        };
    }

    /// <summary>Creates an Agent Framework message from one Redis Agent Memory event.</summary>
    public ChatMessage ToChatMessage(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var role = sessionEvent.Role switch
        {
            SessionEventRole.USER => ChatRole.User,
            SessionEventRole.ASSISTANT => ChatRole.Assistant,
            SessionEventRole.SYSTEM => ChatRole.System,
            _ => throw new ArgumentOutOfRangeException(nameof(sessionEvent), "Unsupported Redis Agent Memory role."),
        };

        return new ChatMessage(role, sessionEvent.Content.Select(static part => (AIContent)new TextContent(part.Text)).ToList())
        {
            AuthorName = sessionEvent.ActorId,
            CreatedAt = sessionEvent.CreatedAt,
            MessageId = sessionEvent.EventId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["redis.iris.session_id"] = sessionEvent.SessionId,
                ["redis.iris.system_timestamp"] = sessionEvent.SystemTimestamp,
            },
        };
    }

    private (SessionEventRole Role, string ActorId)? MapRole(ChatRole role, string ownerId, string agentId)
    {
        if (role == ChatRole.User)
        {
            return (SessionEventRole.USER, ownerId);
        }

        if (role == ChatRole.Assistant)
        {
            return (SessionEventRole.ASSISTANT, agentId);
        }

        if (role == ChatRole.System && this._options.StoreSystemMessages)
        {
            return (SessionEventRole.SYSTEM, agentId);
        }

        return null;
    }
}
