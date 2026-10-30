// Copyright (c) Redis, Inc.

using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentFramework.Tests;

public sealed class RedisIrisMessageMapperTests
{
    [Theory]
    [InlineData("user", SessionEventRole.USER, "owner-1")]
    [InlineData("assistant", SessionEventRole.ASSISTANT, "agent-1")]
    public void ToSessionEventRequestMapsSupportedRoles(string role, SessionEventRole expectedRole, string expectedActor)
    {
        // Arrange
        var mapper = new RedisIrisMessageMapper();
        var message = new ChatMessage(new ChatRole(role), "hello")
        {
            CreatedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.FromHours(-4)),
        };

        // Act
        var result = mapper.ToSessionEventRequest(message, "session-1", "owner-1", "agent-1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedRole, result.Role);
        Assert.Equal(expectedActor, result.ActorId);
        Assert.Equal(TimeSpan.Zero, result.CreatedAt.Offset);
    }

    [Fact]
    public void ToSessionEventRequestSkipsSystemByDefault()
    {
        // Arrange
        var mapper = new RedisIrisMessageMapper();

        // Act
        var result = mapper.ToSessionEventRequest(new ChatMessage(ChatRole.System, "instructions"), "session-1", "owner-1", "agent-1");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToSessionEventRequestStoresOptedInSystemAsAgentActor()
    {
        // Arrange
        var mapper = new RedisIrisMessageMapper(new() { StoreSystemMessages = true });

        // Act
        var result = mapper.ToSessionEventRequest(new ChatMessage(ChatRole.System, "instructions"), "session-1", "owner-1", "agent-1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(SessionEventRole.SYSTEM, result.Role);
        Assert.Equal("agent-1", result.ActorId);
    }

    [Fact]
    public void ToSessionEventRequestSkipsUnsupportedContentByDefault()
    {
        // Arrange
        var mapper = new RedisIrisMessageMapper();
        var message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]);

        // Act
        var result = mapper.ToSessionEventRequest(message, "session-1", "owner-1", "agent-1");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ToSessionEventRequestUsesProjectionForUnsupportedContent()
    {
        // Arrange
        var mapper = new RedisIrisMessageMapper(new() { UnsupportedContentProjection = _ => "Called lookup tool." });
        var message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]);

        // Act
        var result = mapper.ToSessionEventRequest(message, "session-1", "owner-1", "agent-1");

        // Assert
        Assert.Equal("Called lookup tool.", result!.Content.Single().Text);
    }

    [Fact]
    public void ToChatMessagePreservesRoleAndProvenance()
    {
        // Arrange
        var mapper = new RedisIrisMessageMapper();
        var stored = new SessionEvent(
            "event-1",
            "user-1",
            "session-1",
            SessionEventRole.USER,
            [new("hello")],
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 25, 12, 0, 1, TimeSpan.Zero));

        // Act
        var result = mapper.ToChatMessage(stored);

        // Assert
        Assert.Equal(ChatRole.User, result.Role);
        Assert.Equal("hello", result.Text);
        Assert.Equal("event-1", result.MessageId);
        Assert.Equal("user-1", result.AuthorName);
        Assert.Equal("session-1", result.AdditionalProperties!["redis.iris.session_id"]);
    }
}
