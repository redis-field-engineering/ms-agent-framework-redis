// Copyright (c) Redis, Inc.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    [Fact]
    public void ToolCallsRoundTripWithTextAndNestedArguments()
    {
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var message = new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Looking that up."),
            new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>
            {
                ["query"] = "café", ["filter"] = new { ids = new[] { 1, 2 }, enabled = true }, ["optional"] = null,
            }),
            new FunctionCallContent("call-2", "clock", null),
        ]);

        var request = mapper.ToSessionEventRequest(message, "session-1", "owner-1", "agent-1")!;
        var result = mapper.ToChatMessage(Stored(request));

        Assert.Equal(SessionEventRole.ASSISTANT, request.Role);
        Assert.Contains("Tool call lookup (call-1)", request.Content.Single().Text);
        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Equal(3, result.Contents.Count);
        Assert.Equal("Looking that up.", Assert.IsType<TextContent>(result.Contents[0]).Text);
        var call = Assert.IsType<FunctionCallContent>(result.Contents[1]);
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("lookup", call.Name);
        Assert.Equal("café", Assert.IsType<JsonElement>(call.Arguments!["query"]).GetString());
        Assert.Equal(2, Assert.IsType<JsonElement>(call.Arguments["filter"]).GetProperty("ids")[1].GetInt32());
        Assert.Null(call.Arguments["optional"]);
        Assert.Null(Assert.IsType<FunctionCallContent>(result.Contents[2]).Arguments);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"found\"")]
    [InlineData("{\"items\":[1,true,\"value\"]}")]
    public void ToolResultsRoundTripAsToolRole(string json)
    {
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var message = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("call-1", JsonSerializer.Deserialize<JsonElement>(json))]);

        var request = mapper.ToSessionEventRequest(message, "session-1", "owner-1", "agent-1")!;
        var result = mapper.ToChatMessage(Stored(request));

        Assert.Equal(SessionEventRole.ASSISTANT, request.Role);
        Assert.Equal("agent-1", request.ActorId);
        Assert.Equal(ChatRole.Tool, result.Role);
        var content = Assert.IsType<FunctionResultContent>(Assert.Single(result.Contents));
        Assert.Equal("call-1", content.CallId);
        Assert.Equal(json, JsonSerializer.Serialize(content.Result));
    }

    [Fact]
    public void ToolResultsAreStillSkippedByDefault()
    {
        var mapper = new RedisIrisMessageMapper(new() { UnsupportedContentProjection = _ => "projected" });
        Assert.Null(mapper.ToSessionEventRequest(new(ChatRole.Tool, [new FunctionResultContent("call", "value")]),
            "session", "owner", "agent"));
    }

    [Fact]
    public void ToolMetadataIsReadableTextWhenOptionIsDisabled()
    {
        var writer = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var request = writer.ToSessionEventRequest(new(ChatRole.Tool, [new FunctionResultContent("call", "value")]),
            "session", "owner", "agent")!;
        var result = new RedisIrisMessageMapper().ToChatMessage(Stored(request));
        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Equal(request.Content.Single().Text, result.Text);
        Assert.IsType<TextContent>(Assert.Single(result.Contents));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"redis.iris.tool_message\":{\"version\":2,\"role\":\"tool\",\"contents\":[]}}")]
    [InlineData("{\"redis.iris.tool_message\":{\"version\":1,\"role\":\"system\",\"contents\":[]}}")]
    [InlineData("{\"redis.iris.tool_message\":{\"version\":1,\"role\":\"tool\",\"contents\":42}}")]
    [InlineData("{\"redis.iris.tool_message\":{\"version\":1,\"role\":\"tool\",\"contents\":[{\"$type\":\"future\"}]}}")]
    public void InvalidOrUnknownMetadataFallsBackToText(string json)
    {
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var stored = new SessionEvent("event", "agent", "session", SessionEventRole.ASSISTANT,
            [new("fallback")], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, JsonSerializer.Deserialize<JsonElement>(json));
        var result = mapper.ToChatMessage(stored);
        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Equal("fallback", result.Text);
    }

    [Fact]
    public void UserEventCannotRestoreToolMetadata()
    {
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var request = mapper.ToSessionEventRequest(new(ChatRole.Tool, [new FunctionResultContent("call", "value")]),
            "session", "owner", "agent")!;
        var result = mapper.ToChatMessage(Stored(request) with { Role = SessionEventRole.USER });
        Assert.Equal(ChatRole.User, result.Role);
        Assert.IsType<TextContent>(Assert.Single(result.Contents));
    }

    [Theory]
    [InlineData(AgentMemoryApiMode.Legacy)]
    [InlineData(AgentMemoryApiMode.Iris)]
    public async Task ToolMetadataSurvivesHttpContract(AgentMemoryApiMode mode)
    {
        using var handler = new EchoEventHandler();
        using var http = new HttpClient(handler);
        var client = new RedisAgentMemoryClient(http,
            new() { Endpoint = new("https://memory.example.test"), StoreId = "store", ApiMode = mode },
            new FixedAgentMemoryCredentialProvider("test-key"));
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var request = mapper.ToSessionEventRequest(new(ChatRole.Tool, [new FunctionResultContent("call", new { answer = 42 })]),
            "session", "owner", "agent")!;

        var stored = await client.AddSessionEventAsync(request);
        var message = mapper.ToChatMessage(stored);

        Assert.Equal(ChatRole.Tool, message.Role);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents));
        Assert.Equal(42, Assert.IsType<JsonElement>(result.Result).GetProperty("answer").GetInt32());
        Assert.Equal("call", result.CallId);
        Assert.Equal(1, handler.RequestCount);

        var oversized = mapper.ToSessionEventRequest(new(ChatRole.Tool, [new FunctionResultContent("call", new string('x', 16 * 1024))]),
            "session", "owner", "agent")!;
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddSessionEventAsync(oversized));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void ToolOptionDoesNotSilentlyDropUnsupportedParts()
    {
        var mapper = new RedisIrisMessageMapper(new() { StoreToolMessages = true });
        var message = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call", "lookup", null), new DataContent(new byte[] { 1 }, "image/png")]);
        Assert.Null(mapper.ToSessionEventRequest(message, "session", "owner", "agent"));
        var projectingMapper = new RedisIrisMessageMapper(new()
        {
            StoreToolMessages = true,
            UnsupportedContentProjection = _ => "Projected message.",
        });
        var request = projectingMapper.ToSessionEventRequest(message, "session", "owner", "agent")!;
        Assert.Null(request.Metadata);
        Assert.Equal("Projected message.", request.Content.Single().Text);
    }

    private sealed class EchoEventHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.RequestCount++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
            Assert.Equal("ASSISTANT", body.GetProperty("role").GetString());
            Assert.Equal("agent", body.GetProperty("actorId").GetString());
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    @event = new
                    {
                        eventId = "event",
                        actorId = "agent",
                        sessionId = "session",
                        role = "ASSISTANT",
                        content = body.GetProperty("content"),
                        metadata = body.GetProperty("metadata"),
                        createdAt = body.GetProperty("createdAt"),
                        systemTimestamp = DateTimeOffset.UtcNow,
                    },
                }),
            };
        }
    }

    private static SessionEvent Stored(AddSessionEventRequest request) =>
        JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize(new SessionEvent(
            "event", request.ActorId, request.SessionId!, request.Role, request.Content,
            request.CreatedAt, DateTimeOffset.UtcNow, request.Metadata)))!;
}
