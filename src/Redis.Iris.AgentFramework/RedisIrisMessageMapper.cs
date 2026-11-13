// Copyright (c) Redis, Inc.

using System.Text.Json;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework;

/// <summary>Controls conversion between Agent Framework messages and Redis Agent Memory events.</summary>
public sealed class RedisIrisMessageMappingOptions
{
    /// <summary>Gets or sets whether system messages may be stored.</summary>
    public bool StoreSystemMessages { get; set; }

    /// <summary>Gets or sets whether tool calls and results are stored and restored as structured messages.</summary>
    /// <remarks>Uses event metadata (limited to 16 KiB) and a readable text representation for server summaries.</remarks>
    public bool StoreToolMessages { get; set; }

    /// <summary>
    /// Gets or sets an optional projection for messages containing content Redis Agent Memory does not support.
    /// Return <see langword="null"/> or whitespace to skip the message.
    /// </summary>
    public Func<ChatMessage, string?>? UnsupportedContentProjection { get; set; }
}

/// <summary>Maps supported Agent Framework messages to Redis Agent Memory session events.</summary>
public sealed class RedisIrisMessageMapper
{
    private const string ToolMessageMetadataKey = "redis.iris.tool_message";
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
        string actorId,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var mapping = this.MapRole(message.Role, actorId, agentId);
        if (mapping is null)
        {
            AgentMemoryDiagnostics.RecordSkippedMessage("unsupported_role");
            return null;
        }

        string? text;
        JsonElement? metadata = null;
        if (this._options.StoreToolMessages && IsToolMessage(message.Role, message.Contents))
        {
            metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                [ToolMessageMetadataKey] = new { version = 1, role = message.Role.Value, contents = message.Contents },
            });
            text = string.Join("\n", message.Contents.Select(static content => content switch
            {
                TextContent part => part.Text,
                FunctionCallContent call => $"Tool call {call.Name} ({call.CallId}): {JsonSerializer.Serialize(call.Arguments)}",
                FunctionResultContent result => $"Tool result ({result.CallId}): {JsonSerializer.Serialize(result.Result)}",
                _ => throw new InvalidOperationException("Unsupported tool message content."),
            }));
        }
        else if (message.Contents.Count > 0 && message.Contents.All(static content => content is TextContent))
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
            Metadata = metadata,
            CreatedAt = (message.CreatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
        };
    }

    /// <summary>Creates an Agent Framework message from one Redis Agent Memory event.</summary>
    public ChatMessage ToChatMessage(SessionEvent sessionEvent) => this.ToChatMessage(sessionEvent, this._options.StoreToolMessages);

    internal ChatMessage ToChatMessage(SessionEvent sessionEvent, bool restoreToolMessages)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        var role = sessionEvent.Role switch
        {
            SessionEventRole.USER => ChatRole.User,
            SessionEventRole.ASSISTANT => ChatRole.Assistant,
            SessionEventRole.SYSTEM => ChatRole.System,
            _ => throw new ArgumentOutOfRangeException(nameof(sessionEvent), "Unsupported Redis Agent Memory role."),
        };

        IList<AIContent> contents = sessionEvent.Content.Select(static part => (AIContent)new TextContent(part.Text)).ToList();
        if (restoreToolMessages && sessionEvent.Role == SessionEventRole.ASSISTANT &&
            TryReadToolMessage(sessionEvent.Metadata, out var toolRole, out var toolContents))
        {
            role = toolRole;
            contents = toolContents!;
        }

        return new ChatMessage(role, contents)
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

    private static bool IsToolMessage(ChatRole role, IList<AIContent> contents) =>
        role == ChatRole.Assistant && contents.Any(static part => part is FunctionCallContent) &&
            contents.All(static part => part is TextContent or FunctionCallContent) ||
        role == ChatRole.Tool && contents.Any(static part => part is FunctionResultContent) &&
            contents.All(static part => part is TextContent or FunctionResultContent);

    private static bool TryReadToolMessage(JsonElement? metadata, out ChatRole role, out IList<AIContent>? contents)
    {
        role = default;
        contents = null;
        if (metadata is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty(ToolMessageMetadataKey, out var envelope) ||
            envelope.ValueKind != JsonValueKind.Object ||
            !envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != 1 ||
            !envelope.TryGetProperty("role", out var storedRole) || storedRole.ValueKind != JsonValueKind.String ||
            storedRole.GetString() is not ("assistant" or "tool") ||
            !envelope.TryGetProperty("contents", out var storedContents))
        {
            return false;
        }

        try
        {
            role = new ChatRole(storedRole.GetString()!);
            contents = storedContents.Deserialize<List<AIContent>>();
            return contents is not null && IsToolMessage(role, contents);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            // Unknown or malformed envelopes remain readable text, never executable tool messages.
            return false;
        }
    }

    private (SessionEventRole Role, string ActorId)? MapRole(ChatRole role, string actorId, string agentId)
    {
        if (role == ChatRole.User)
        {
            return (SessionEventRole.USER, actorId);
        }

        if (role == ChatRole.Assistant)
        {
            return (SessionEventRole.ASSISTANT, agentId);
        }

        if (role == ChatRole.Tool && this._options.StoreToolMessages)
        {
            // RAM has no TOOL role. The original role lives in the versioned metadata envelope.
            return (SessionEventRole.ASSISTANT, agentId);
        }

        if (role == ChatRole.System && this._options.StoreSystemMessages)
        {
            return (SessionEventRole.SYSTEM, agentId);
        }

        return null;
    }
}
