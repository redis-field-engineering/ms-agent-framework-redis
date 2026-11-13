using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Redis.Iris.AgentMemory.Management.Tests;

public sealed class AgentMemoryManagementClientTests
{
    private const string Store = "st_0123456789abcdef0123456789abcdef";
    private const string TypeJson = """{"name":"trip","description":"Trip requirements","revision":"r2","fields":[{"name":"city","description":"Destination","type":"STRING"}],"extractionStrategy":{"prompt":"Extract trips","enabled":false}}""";

    [Fact]
    public async Task DiClientDoesNotFollowRedirectsOrReplayCookies()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var captured = new List<string>();
        var server = Task.Run(async () =>
        {
            foreach (var response in new[]
            {
                $"HTTP/1.1 302 Found\r\nLocation: {address}/redirected\r\nSet-Cookie: admin=session\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(TypeJson)}\r\nConnection: close\r\n\r\n{TypeJson}",
            })
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var lines = new List<string>();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(deadline.Token))) lines.Add(line);
                captured.Add(string.Join("\n", lines));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), deadline.Token);
            }
        }, deadline.Token);
        var services = new ServiceCollection();
        services.AddRedisAgentMemoryManagement(new() { Endpoint = new(address) }, new FixedAgentMemoryManagementCredentialProvider("synthetic"))
            .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(5));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAgentMemoryManagementClient>();
        var redirect = await Assert.ThrowsAsync<AgentMemoryManagementException>(() => client.GetMemoryTypeAsync(Store, "trip", deadline.Token));
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("trip", (await client.GetMemoryTypeAsync(Store, "trip", deadline.Token)).Name);
        await server;
        Assert.Equal(2, captured.Count);
        Assert.All(captured, headers =>
        {
            Assert.StartsWith($"GET /iris/v1/agent-memory/stores/{Store}/memory-types/trip ", headers);
            Assert.DoesNotContain("Cookie:", headers, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CancellationDuringDispatchIsPropagatedWithoutRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Response();
        });
        using var http = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var pending = Client(http).CreateMemoryTypeAsync(Store, Definition(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransportErrorsAreSanitizedWithoutRetry()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("secret credentials in URL"));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AgentMemoryManagementException>(() => Client(http).CreateMemoryTypeAsync(Store, Definition()));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CreateUsesDedicatedResourceAndOmitsReadOnlyRevision()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/gateway/iris/v1/agent-memory/stores/{Store}/memory-types", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-admin", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("X-API-Key"));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("trip", root.GetProperty("name").GetString());
            Assert.Equal("STRING", root.GetProperty("fields")[0].GetProperty("type").GetString());
            Assert.False(root.GetProperty("extractionStrategy").GetProperty("enabled").GetBoolean());
            Assert.False(root.TryGetProperty("revision", out _));
            Assert.False(root.TryGetProperty("customMemoryTypes", out _));
            return Response(HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler);
        var result = await Client(http).CreateMemoryTypeAsync(Store, Definition() with { Revision = "ignored" });
        Assert.Equal("r2", result.Revision);
        Assert.Equal(CustomMemoryFieldType.STRING, Assert.Single(result.Fields!).Type);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ListAndReadUseEscapedPaginationAndFreshCredentials()
    {
        var credentials = new RotatingCredentials();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"token-{credentials.Calls}", request.Headers.Authorization!.Parameter);
            if (request.RequestUri!.Query.Length > 0)
            {
                Assert.Equal("?limit=2&cursor=a%2Bb%26c", request.RequestUri.Query);
                return Task.FromResult(Response(body: "{\"items\":[" + TypeJson + "],\"nextCursor\":\"next\"}"));
            }
            Assert.EndsWith("/memory-types/trip", request.RequestUri.AbsolutePath);
            return Task.FromResult(Response());
        });
        using var http = new HttpClient(handler);
        var client = new AgentMemoryManagementClient(http, new() { Endpoint = new("https://example.test") }, credentials);
        var page = await client.ListMemoryTypesAsync(Store, 2, "a+b&c");
        Assert.Equal("next", page.NextCursor);
        Assert.Single(page.Items);
        Assert.Equal("trip", (await client.GetMemoryTypeAsync(Store, "trip")).Name);
        Assert.Equal(2, credentials.Calls);
    }

    [Fact]
    public async Task StrategyUpdateSendsOnlySuppliedMembersAndExpectedRevision()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement;
            Assert.Equal(2, root.EnumerateObject().Count());
            Assert.Equal("r1", root.GetProperty("expectedRevision").GetString());
            var strategy = root.GetProperty("extractionStrategy");
            Assert.False(strategy.GetProperty("enabled").GetBoolean());
            Assert.False(strategy.TryGetProperty("prompt", out _));
            return Response();
        });
        using var http = new HttpClient(handler);
        Assert.Equal("r2", (await Client(http).UpdateExtractionStrategyAsync(Store, "trip", new() { Enabled = false }, "r1")).Revision);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(500)]
    public async Task FailuresKeepStatusWithoutLeakingBodyOrRetrying(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)status, "secret prompt and token")));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AgentMemoryManagementException>(() => Client(http).CreateMemoryTypeAsync(Store, Definition()));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("memory_type.create", error.Operation);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ValidationAndCancellationDoNotSend()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler); var client = Client(http);
        foreach (var name in new[] { "../trip", "1trip", "semantic", "session_summary", new string('x', 65) })
            await Assert.ThrowsAsync<ArgumentException>(() => client.CreateMemoryTypeAsync(Store, Definition() with { Name = name }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetMemoryTypeAsync("bad", "trip"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListMemoryTypesAsync(Store, 201));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListMemoryTypesAsync(Store, cursor: ""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateMemoryTypeAsync(Store, Definition() with { Fields = [new("city", "Destination", (CustomMemoryFieldType)99)] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateMemoryTypeAsync(Store, Definition() with { Fields = [new("city", "A", CustomMemoryFieldType.STRING), new("city", "B", CustomMemoryFieldType.STRING)] }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateExtractionStrategyAsync(Store, "trip", new()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateExtractionStrategyAsync(Store, "trip", new() { Prompt = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateExtractionStrategyAsync(Store, "trip", new() { Enabled = true }, ""));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMemoryTypeAsync(Store, "trip", new CancellationToken(true)));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"name\":\"different\",\"description\":\"wrong type\"}")]
    public async Task MalformedResponsesAreSanitized(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(body: body)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<AgentMemoryManagementException>(() => Client(http).GetMemoryTypeAsync(Store, "trip"));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task MissingListCannotBeMistakenForAnEmptyRegistry()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(body: "{}")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<AgentMemoryManagementException>(() => Client(http).ListMemoryTypesAsync(Store));
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test?token=x")]
    [InlineData("https://example.test/iris/data/v1/agent-memory")]
    [InlineData("https://example.test/memory-stores")]
    public void InvalidEndpointsFailDuringRegistration(string endpoint)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddRedisAgentMemoryManagement(new() { Endpoint = new(endpoint) }, new FixedAgentMemoryManagementCredentialProvider("synthetic")));
    }

    [Fact]
    public async Task TypesWithoutFieldsOrStrategyRemainValid()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.False(body.RootElement.TryGetProperty("fields", out _));
            Assert.False(body.RootElement.TryGetProperty("extractionStrategy", out _));
            return Response(body: "{\"name\":\"trip\",\"description\":\"Trip requirements\"}");
        });
        using var http = new HttpClient(handler);
        await Client(http).CreateMemoryTypeAsync(Store, new() { Name = "trip", Description = "Trip requirements" });
    }

    private static CustomMemoryTypeDefinition Definition() => new()
    {
        Name = "trip",
        Description = "Trip requirements",
        Fields = [new("city", "Destination", CustomMemoryFieldType.STRING)],
        ExtractionStrategy = new("Extract trips", false),
    };
    private static AgentMemoryManagementClient Client(HttpClient http) => new(http, new() { Endpoint = new("https://example.test/gateway/") }, new FixedAgentMemoryManagementCredentialProvider("synthetic-admin"));
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK, string body = TypeJson) => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request, cancellationToken); }
    }
    private sealed class RotatingCredentials : IAgentMemoryManagementCredentialProvider
    {
        public int Calls { get; private set; }
        public ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult($"token-{++Calls}");
    }
}
