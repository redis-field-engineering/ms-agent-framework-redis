using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Redis.Iris.AgentMemory.Tests;

public sealed class LongTermMemoryWriteTests
{
    private const string Record = """{"memoryId":"memory_1","text":"aisle seat","ownerId":"user@example.com","sessionId":"session_1","revision":"r2","documentManaged":false,"topics":["travel"],"createdAt":"2026-11-12T12:00:00Z","updatedAt":"2026-11-12T12:01:00Z"}""";
    private const string Ns = "ns_0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task CreateMapsFieldsAndPreservesPartialFailuresAndCredentials()
    {
        using var handler = new Handler("""{"created":["memory_1"],"errors":[{"memoryId":"memory_2","status":409,"type":"/errors/conflict","title":"Exists","detail":"Already exists"}]}""", HttpStatusCode.Created);
        var client = Client(handler);
        var result = await client.BulkCreateLongTermMemoriesAsync(new()
        {
            IdempotencyKey = "create-1",
            Memories = [Create() with { NamespaceRef = new(Ns), Attributes = new Dictionary<string, JsonElement> { ["seat"] = JsonSerializer.SerializeToElement("aisle") } }, Create() with { Id = "memory_2" }],
        });
        Assert.Equal(["memory_1"], result.Created);
        var error = Assert.Single(result.Errors!);
        Assert.Equal(409, error.Status);
        Assert.Equal("/errors/conflict", error.Type);
        Assert.Equal("memory_2", error.Id);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/gateway/iris/data/v1/agent-memory/stores/store/memories", handler.Path);
        Assert.Equal("Bearer application-key", handler.Authorization);
        Assert.Equal("user-token", handler.Delegation);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("create-1", body.RootElement.GetProperty("idempotencyKey").GetString());
        var memory = body.RootElement.GetProperty("memories")[0];
        Assert.Equal("memory_1", memory.GetProperty("memoryId").GetString());
        Assert.Equal(Ns, memory.GetProperty("namespaceId").GetString());
        Assert.Equal("aisle", memory.GetProperty("attributes").GetProperty("seat").GetString());
        Assert.False(memory.TryGetProperty("id", out _));
        Assert.False(memory.TryGetProperty("namespaceRef", out _));
    }

    [Fact]
    public async Task GetAndPatchRetainRevisionAndProtectOmittedFields()
    {
        using var handler = new Handler(Record);
        var client = Client(handler);
        var record = await client.GetLongTermMemoryAsync("memory_1");
        Assert.Equal("r2", record.Revision);
        Assert.False(record.DocumentManaged);
        Assert.Equal(HttpMethod.Get, handler.Method);
        var result = await client.UpdateLongTermMemoryAsync("memory_1", new()
        {
            Text = "aisle seat",
            Topics = [],
            SessionId = "session_1",
            ExpectedRevision = "r1",
            IdempotencyKey = "update-1",
        });
        Assert.Equal("r2", result.Revision);
        Assert.Equal(HttpMethod.Patch, handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("r1", body.RootElement.GetProperty("expectedRevision").GetString());
        Assert.Equal("update-1", body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("session_1", body.RootElement.GetProperty("sessionId").GetString());
        Assert.Empty(body.RootElement.GetProperty("topics").EnumerateArray());
        foreach (var absent in new[] { "ownerId", "namespaceId", "memoryType", "attributes" }) Assert.False(body.RootElement.TryGetProperty(absent, out _));
    }

    [Fact]
    public async Task UpsertUsesItsActualResponseShapeAndSendsAttributes()
    {
        using var handler = new Handler("""{"memoryId":"memory_1","created":false}""");
        var result = await Client(handler).UpsertLongTermMemoryAsync("memory_1", new()
        {
            Text = "aisle seat",
            OwnerId = "user@example.com",
            ExpectedRevision = "r1",
            IdempotencyKey = "replace-1",
            Attributes = new Dictionary<string, JsonElement>(),
        });
        Assert.False(result.Created);
        Assert.Equal("memory_1", result.Id);
        Assert.Equal(HttpMethod.Put, handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("attributes").ValueKind);
        Assert.False(body.RootElement.TryGetProperty("topics", out _));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task MutationErrorsPreserveStatusWithoutRetryOrSensitiveDetails(int status)
    {
        using var handler = new Handler("""{"type":"/errors/revision-conflict","detail":"private memory text"}""", (HttpStatusCode)status);
        var error = await Assert.ThrowsAsync<AgentMemoryException>(() => Client(handler).UpdateLongTermMemoryAsync("memory_1", new() { Text = "new", ExpectedRevision = "old" }));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("/errors/revision-conflict", error.ErrorCode);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task LegacyRejectsEveryNewOperationBeforeDispatch()
    {
        using var handler = new Handler(Record);
        var client = Client(handler, AgentMemoryApiMode.Legacy);
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetLongTermMemoryAsync("memory_1"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.BulkCreateLongTermMemoriesAsync(new() { Memories = [Create()] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.UpdateLongTermMemoryAsync("memory_1", new() { Text = "new" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.UpsertLongTermMemoryAsync("memory_1", new() { Text = "new", OwnerId = "owner" }));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ValidationDoesNotTruncateOrSendInvalidRequests()
    {
        using var handler = new Handler(Record);
        var client = Client(handler);
        foreach (var count in new[] { 0, 101 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.BulkCreateLongTermMemoriesAsync(new() { Memories = Enumerable.Range(0, count).Select(i => Create() with { Id = $"m-{i}" }).ToArray() }));
        foreach (var memory in new[]
        {
            Create() with { Id = "bad/id" }, Create() with { Id = "id\n" }, Create() with { Text = "" },
            Create() with { Text = new string('x', 50001) }, Create() with { OwnerId = ".." },
            Create() with { NamespaceRef = new("label") }, Create() with { Topics = [""] },
            Create() with { MemoryType = "1type" }, Create() with { SessionId = "session/path" },
        }) await Assert.ThrowsAsync<ArgumentException>(() => client.BulkCreateLongTermMemoriesAsync(new() { Memories = [memory] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.BulkCreateLongTermMemoriesAsync(new() { Memories = [Create(), Create()] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateLongTermMemoryAsync("memory_1", new()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateLongTermMemoryAsync("memory_1", new() { Text = "new", IdempotencyKey = "bad\nkey" }));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task RetryingWithSameRequestKeepsReplayIdentityAndPayload()
    {
        using var handler = new Handler("""{"created":["memory_1"]}""", HttpStatusCode.Created);
        var client = Client(handler);
        var request = new BulkCreateLongTermMemoriesRequest { Memories = [Create()], IdempotencyKey = "stable-key" };
        await client.BulkCreateLongTermMemoriesAsync(request);
        var first = handler.Body;
        await client.BulkCreateLongTermMemoriesAsync(request);
        Assert.Equal(first, handler.Body);
    }

    [Fact]
    public async Task MissingMutationResultFieldsAreNotReportedAsSuccess()
    {
        using var handler = new Handler("{}");
        var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLongTermMemoryAsync("memory_1"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.BulkCreateLongTermMemoriesAsync(new() { Memories = [Create()] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.UpsertLongTermMemoryAsync("memory_1", new() { Text = "new", OwnerId = "owner" }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationTelemetryUsesActualStatusAndSanitizedPartialFailure(bool partial)
    {
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(listener);
        using var handler = new Handler(partial
            ? """{"created":[],"errors":[{"memoryId":"memory_1","status":409,"type":"/errors/conflict","title":"Exists","detail":"private memory text"}]}"""
            : """{"created":["memory_1"]}""", HttpStatusCode.Created);
        await Client(handler).BulkCreateLongTermMemoriesAsync(new() { Memories = [Create()] });
        Assert.NotNull(stopped);
        Assert.Equal(201, stopped.GetTagItem("http.response.status_code"));
        Assert.Equal(partial ? "partial_failure" : "success", stopped.GetTagItem("redis.iris.outcome"));
        var tags = string.Join(" ", stopped.TagObjects.Select(pair => $"{pair.Key}={pair.Value}"));
        foreach (var secret in new[] { "memory_1", "user@example.com", "private memory text", "application-key", "user-token" }) Assert.DoesNotContain(secret, tags);
    }

    [Fact]
    public async Task CancelledMutationNeverReachesTransport()
    {
        using var handler = new Handler(Record);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).UpdateLongTermMemoryAsync("memory_1", new() { Text = "new" }, cancellation.Token));
        Assert.Equal(0, handler.Count);
    }

    private static CreateLongTermMemoryRequest Create() => new() { Id = "memory_1", Text = "aisle seat", OwnerId = "user@example.com", MemoryType = "travel_preference", Topics = ["travel"] };
    private static RedisAgentMemoryClient Client(Handler handler, AgentMemoryApiMode mode = AgentMemoryApiMode.Iris) => new(new HttpClient(handler),
        new() { Endpoint = new("https://memory.example/gateway"), StoreId = "store", ApiMode = mode },
        new FixedAgentMemoryCredentialProvider("application-key"), new FixedAgentMemoryDelegatedTokenProvider(new("user-token", DateTimeOffset.UtcNow.AddHours(1))));

    private sealed class Handler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? Delegation { get; private set; }
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++; Method = request.Method; Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Delegation = request.Headers.GetValues("X-Iris-User-Token").Single();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
