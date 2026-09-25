// Copyright (c) Redis, Inc.

using System.Net;
using System.Text;
using System.Text.Json;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentMemory.Tests;

public sealed class RedisAgentMemoryClientTests
{
    [Fact]
    public async Task AddSessionEventAsyncSendsProductionContractAsync()
    {
        // Arrange
        var handler = new RecordingHandler(request => JsonResponse(HttpStatusCode.OK, """
            {"event":{"eventId":"event-1","actorId":"user-1","sessionId":"session-1","role":"USER","content":[{"text":"hello"}],"createdAt":"2026-09-25T12:00:00Z","systemTimestamp":"2026-09-25T12:00:01Z"}}
            """));
        var client = CreateClient(handler);

        // Act
        var result = await client.AddSessionEventAsync(new AddSessionEventRequest
        {
            ActorId = "user-1",
            SessionId = "session-1",
            Role = SessionEventRole.USER,
            Content = [new("hello")],
            CreatedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
        });

        // Assert
        Assert.Equal("event-1", result.EventId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://memory.example/v1/stores/store-1/session-memory/events", handler.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer test-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("USER", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("hello", body.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetSessionMemoryAsyncReadsSummaryAsync()
    {
        // Arrange
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, """
            {"sessionId":"session-1","ownerId":"user-1","events":[],"summary":{"text":"Earlier context","summarizedUpToEventId":"event-1","createdAt":"2026-09-25T12:00:00Z","updatedAt":"2026-09-25T12:01:00Z","metadata":{},"summarizedEvents":4}}
            """));
        var client = CreateClient(handler);

        // Act
        var result = await client.GetSessionMemoryAsync("session-1", includeSummarizedEvents: true);

        // Assert
        Assert.Equal("user-1", result.OwnerId);
        Assert.Equal("Earlier context", result.Summary!.Text);
        Assert.EndsWith("?includeSummarisedEvents=true", handler.Request!.RequestUri!.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteSessionEventAsyncUsesNestedEventPathAsync()
    {
        // Arrange
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        // Act
        await client.DeleteSessionEventAsync("session-1", "event-1");

        // Assert
        Assert.Equal(HttpMethod.Delete, handler.Request!.Method);
        Assert.EndsWith("/session-memory/session-1/events/event-1", handler.Request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorDoesNotExposeBodyOrCredentialAsync()
    {
        // Arrange
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.Unauthorized, """
            {"error":{"type":"AUTHENTICATION_ERROR","message":"secret server detail"}}
            """));
        var client = CreateClient(handler);

        // Act
        var error = await Assert.ThrowsAsync<AgentMemoryException>(() => client.GetSessionMemoryAsync("session-1"));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal("AUTHENTICATION_ERROR", error.ErrorCode);
        Assert.DoesNotContain("test-key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret server detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddSessionEventAsyncRejectsNonUtcTimestampAsync()
    {
        // Arrange
        var client = CreateClient(new RecordingHandler(_ => throw new InvalidOperationException("HTTP should not be called.")));
        var request = new AddSessionEventRequest
        {
            ActorId = "user-1",
            SessionId = "session-1",
            Role = SessionEventRole.USER,
            Content = [new("hello")],
            CreatedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.FromHours(-4)),
        };

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddSessionEventAsync(request));
    }

    private static RedisAgentMemoryClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new RedisAgentMemoryClientOptions { Endpoint = new Uri("https://memory.example"), StoreId = "store-1" },
        new FixedAgentMemoryCredentialProvider("test-key"));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Request = request;
            this.Authorization = request.Headers.Authorization?.ToString();
            this.Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }
}
