// Copyright (c) Redis, Inc.

using System.Net;
using System.Text;
using System.Text.Json;

namespace Redis.Iris.AgentMemory.Tests;

// Fixtures follow the public Iris OpenAPI contract, including fields that differ from the legacy API.
public sealed class IrisApiContractTests
{
    private const string StorePath = "https://memory.example/gateway/iris/data/v1/agent-memory/stores/store-1";
    private const string EventJson = """
        {"event":{"eventId":"event-1","actorId":"owner","sessionId":"session-1","role":"USER","content":[{"text":"hello"}],"createdAt":"2026-09-25T12:00:00Z","systemTimestamp":"2026-09-25T12:00:01Z"}}
        """;

    [Theory]
    [InlineData(null, "/sessions")]
    [InlineData("session-1", "/sessions/session-1/events")]
    [InlineData("session /?#", "/sessions/session%20%2F%3F%23/events")]
    public async Task AppendUsesSessionPathAndMapsNamespace(string? sessionId, string suffix)
    {
        using var handler = new RecordingHandler(EventJson, HttpStatusCode.Created);
        using var http = new HttpClient(handler);
        var result = await CreateClient(http).AddSessionEventAsync(new()
        {
            ActorId = "owner",
            SessionId = sessionId,
            Role = SessionEventRole.USER,
            Content = [new("hello")],
            CreatedAt = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            NamespaceRef = new("ns-1"),
            IdempotencyKey = "event-key",
            Metadata = JsonSerializer.SerializeToElement(new { source = "test" }),
        });

        Assert.Equal("event-1", result.EventId);
        Assert.Equal("session-1", result.SessionId);
        AssertRequest(handler, HttpMethod.Post, suffix);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("ns-1", root.GetProperty("namespaceId").GetString());
        Assert.Equal("event-key", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal("owner", root.GetProperty("actorId").GetString());
        Assert.Equal("USER", root.GetProperty("role").GetString());
        Assert.Equal("hello", root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("test", root.GetProperty("metadata").GetProperty("source").GetString());
        Assert.False(root.TryGetProperty("sessionId", out _));
        Assert.False(root.TryGetProperty("namespaceRef", out _));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "?includeSummarisedEvents=true")]
    public async Task SessionReadMapsNamespaceAndPreservesSummary(bool includeSummarized, string query)
    {
        using var handler = new RecordingHandler("""
            {"sessionId":"session-1","ownerId":"owner","namespaceId":"ns-1","events":[],"summary":{"text":"Earlier context","summarizedUpToEventId":"event-1","createdAt":"2026-09-25T12:00:00Z","updatedAt":"2026-09-25T12:01:00Z","metadata":{},"summarizedEvents":4}}
            """);
        using var http = new HttpClient(handler);
        var session = await CreateClient(http).GetSessionMemoryAsync("session-1", includeSummarized);

        AssertRequest(handler, HttpMethod.Get, "/sessions/session-1" + query);
        Assert.Equal("owner", session.OwnerId);
        Assert.Equal("ns-1", session.NamespaceRef!.Id);
        Assert.Equal("Earlier context", session.Summary!.Text);
        Assert.Equal(4, session.Summary.SummarizedEvents);
    }

    [Fact]
    public async Task EventReadAndDeletesUseNestedRoutes()
    {
        using var handler = new RecordingHandler(EventJson);
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var result = await client.GetSessionEventAsync("session-1", "event-1");
        Assert.Equal("event-1", result.EventId);
        AssertRequest(handler, HttpMethod.Get, "/sessions/session-1/events/event-1");

        await client.DeleteSessionEventAsync("session-1", "event /?#");
        AssertRequest(handler, HttpMethod.Delete, "/sessions/session-1/events/event%20%2F%3F%23");
        await client.DeleteSessionMemoryAsync("session-1");
        AssertRequest(handler, HttpMethod.Delete, "/sessions/session-1");
    }

    [Fact]
    public async Task SessionListMapsRowsAndCursorWithoutInventingTotal()
    {
        using var handler = new RecordingHandler("""
            {"items":[{"sessionId":"session-1","ownerId":"owner"},{"sessionId":"session-2","ownerId":"owner"}],"nextCursor":"second page"}
            """);
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var result = await client.ListSessionsAsync("owner+1", limit: 200, pageToken: "first +/=");

        AssertRequest(handler, HttpMethod.Get, "/sessions?ownerId=owner%2B1&limit=200&cursor=first%20%2B%2F%3D");
        Assert.Equal(["session-1", "session-2"], result.Items);
        Assert.Equal("second page", result.NextPageToken);
        Assert.Equal(-1, result.Total);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListSessionsAsync("owner", 201));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SearchMapsQueryFiltersRecordsAndPagination()
    {
        using var handler = new RecordingHandler("""
            {"items":[{"memoryId":"memory-1","text":"Window seats","createdAt":"2026-09-25T12:00:00Z","updatedAt":"2026-09-25T12:01:00Z","ownerId":"owner","sessionId":"session-1","namespaceId":"ns-1","memoryType":"semantic","topics":["travel"],"attributes":{"airline":"example"}}],"nextCursor":"next"}
            """);
        using var http = new HttpClient(handler);
        var result = await CreateClient(http).SearchLongTermMemoryAsync(new()
        {
            Text = "seating",
            SimilarityThreshold = 0.75,
            Limit = 5,
            PageToken = "previous",
            FilterOp = "any",
            Filter = new()
            {
                OwnerId = new() { Eq = "owner" },
                SessionId = new() { In = ["session-1"] },
                NamespaceRef = new() { Eq = "ns-1" },
                Topics = new() { All = ["travel"] },
                MemoryType = new() { Eq = "semantic" },
                CreatedAt = new() { Gte = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) },
            },
        });

        AssertRequest(handler, HttpMethod.Post, "/memories/search");
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("seating", root.GetProperty("query").GetString());
        Assert.Equal(0.75, root.GetProperty("minScore").GetDouble());
        Assert.Equal("previous", root.GetProperty("cursor").GetString());
        Assert.Equal(5, root.GetProperty("limit").GetInt32());
        var filter = root.GetProperty("filter");
        Assert.Equal("ANY", filter.GetProperty("op").GetString());
        Assert.Equal("owner", filter.GetProperty("ownerId").GetProperty("eq").GetString());
        Assert.Equal("ns-1", filter.GetProperty("namespaceId").GetProperty("eq").GetString());
        Assert.Equal("travel", filter.GetProperty("topics").GetProperty("all")[0].GetString());
        Assert.Equal("session-1", filter.GetProperty("sessionId").GetProperty("in")[0].GetString());
        Assert.Equal("semantic", filter.GetProperty("memoryType").GetProperty("eq").GetString());
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), filter.GetProperty("createdAt").GetProperty("gte").GetDateTimeOffset());
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("filterOp", out _));
        Assert.False(root.TryGetProperty("pageToken", out _));
        Assert.False(filter.TryGetProperty("namespaceRef", out _));
        var memory = Assert.Single(result.Items);
        Assert.Equal("memory-1", memory.Id);
        Assert.Equal("ns-1", memory.NamespaceRef!.Id);
        Assert.Equal("Window seats", memory.Text);
        Assert.Equal("owner", memory.OwnerId);
        Assert.Equal("session-1", memory.SessionId);
        Assert.Equal("semantic", memory.MemoryType);
        Assert.Equal(["travel"], memory.Topics);
        Assert.Equal("example", memory.Attributes!["airline"].GetString());
        Assert.Equal("next", result.NextPageToken);
    }

    [Fact]
    public async Task UnsupportedNamespaceFiltersFailBeforeSending()
    {
        using var handler = new RecordingHandler("{}");
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        foreach (var filter in new LongTermMemoryFilter[]
        {
            new() { Namespace = new() { Eq = "legacy-label" } },
            new() { NamespaceRef = new() { Ne = "ns-1" } },
            new() { NamespaceRef = new() { All = ["ns-1"] } },
        })
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => client.SearchLongTermMemoryAsync(new() { Filter = filter }));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => client.SearchLongTermMemoryAsync(new() { FilterOp = "invalid" }));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task BulkDeleteUsesPostAndMapsPartialFailures()
    {
        using var handler = new RecordingHandler("""
            {"deleted":["memory-1"],"errors":[{"memoryId":"memory-2","type":"not-found","title":"Not found","status":404,"detail":"Memory not found"},{"memoryId":"memory-3","type":"forbidden","title":"Forbidden","status":403}]}
            """);
        using var http = new HttpClient(handler);
        var result = await CreateClient(http).BulkDeleteLongTermMemoriesAsync(["memory-1", "memory-2", "memory-3"]);
        AssertRequest(handler, HttpMethod.Post, "/memories/bulk-delete");
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(3, body.RootElement.GetProperty("memoryIds").GetArrayLength());
        Assert.Equal(["memory-1"], result.Deleted);
        Assert.Equal("memory-2", result.Errors![0].Id);
        Assert.Equal("Memory not found", result.Errors[0].Error);
        Assert.Equal("Forbidden", result.Errors[1].Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ProblemDetailsPreserveStatusWithoutLeakingDetailsOrRetrying(HttpStatusCode status)
    {
        using var handler = new RecordingHandler("""
            {"type":"https://errors.example/not-found","status":404,"title":"private title","detail":"private detail","instance":"private instance"}
            """, status);
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var error = await Assert.ThrowsAsync<AgentMemoryException>(() => client.GetSessionMemoryAsync("missing"));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("https://errors.example/not-found", error.ErrorCode);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(1, handler.RequestCount);
        if (status == HttpStatusCode.NotFound)
            Assert.Null(await client.TryGetSessionMemoryAsync("missing"));
        else
            await Assert.ThrowsAsync<AgentMemoryException>(() => client.TryGetSessionMemoryAsync("missing"));
    }

    [Fact]
    public void InvalidApiModeIsRejected()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisAgentMemoryClient(http,
            new() { Endpoint = new("https://memory.example"), StoreId = "store", ApiMode = (AgentMemoryApiMode)99 },
            new FixedAgentMemoryCredentialProvider("key")));
    }

    private static RedisAgentMemoryClient CreateClient(HttpClient http) => new(http,
        new() { Endpoint = new("https://memory.example/gateway"), StoreId = "store-1", ApiMode = AgentMemoryApiMode.Iris },
        new FixedAgentMemoryCredentialProvider("test-key"),
        new FixedAgentMemoryDelegatedTokenProvider(new("user-token", DateTimeOffset.UtcNow.AddHours(1))));

    private static void AssertRequest(RecordingHandler handler, HttpMethod method, string suffix)
    {
        Assert.Equal(method, handler.Method);
        Assert.Equal(StorePath + suffix, handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer test-key", handler.Authorization);
        Assert.Equal("user-token", handler.DelegatedToken);
    }

    private sealed class RecordingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal Uri? Uri { get; private set; }
        internal HttpMethod? Method { get; private set; }
        internal string? Body { get; private set; }
        internal string? Authorization { get; private set; }
        internal string? DelegatedToken { get; private set; }
        internal int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Uri = request.RequestUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            DelegatedToken = request.Headers.GetValues("X-Iris-User-Token").Single();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
