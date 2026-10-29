using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.ContextRetriever.Modeling;
using Xunit;

namespace Redis.Iris.ContextRetriever.Management.Tests;

public sealed class ContextRetrieverManagementClientTests
{
    private const string AdminKey = "synthetic-admin-key";
    private const string RedisPassword = "synthetic-redis-password";
    private static readonly ContextDataModel Model = new("Travel", "Synthetic", [new("Traveler", "", "traveler:{id}", [new("id", "str", "", true, [])], [])]);
    private static readonly ContextSurfaceDeployment Deployment = new()
    {
        Name = "travel",
        Description = "Synthetic travel",
        RedisAddress = "redis.example:6380",
        RedisUsername = "app",
        RedisPassword = RedisPassword,
        RedisDatabase = 2,
        Metadata = new Dictionary<string, string> { ["integration"] = "notebook" },
    };

    [Theory]
    [InlineData("https://cloud.example/context-surfaces")]
    [InlineData("https://cloud.example/context-surfaces/")]
    public async Task CreatesUsingTheAdminRestContractAndPreservesGatewayPrefix(string endpoint)
    {
        using var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://cloud.example/context-surfaces/api/v1/context-surfaces", request.RequestUri!.AbsoluteUri);
            Assert.Equal(AdminKey, Assert.Single(request.Headers.GetValues("X-API-Key")));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellation));
            var body = document.RootElement;
            Assert.Equal("travel", body.GetProperty("name").GetString());
            Assert.Equal("Synthetic travel", body.GetProperty("description").GetString());
            Assert.Equal("notebook", body.GetProperty("metadata").GetProperty("integration").GetString());
            Assert.Equal(ContextDataModelJson.Serialize(Model),
                ContextDataModelJson.Serialize(ContextDataModelJson.Deserialize(body.GetProperty("data_model").GetRawText())));
            Assert.Equal("redis", body.GetProperty("data_source").GetProperty("type").GetString());
            var redis = body.GetProperty("data_source").GetProperty("connection_config");
            Assert.Equal("redis.example:6380", redis.GetProperty("addr").GetString());
            Assert.Equal("app", redis.GetProperty("username").GetString());
            Assert.Equal(RedisPassword, redis.GetProperty("password").GetString());
            Assert.Equal(2, redis.GetProperty("db").GetInt32());
            Assert.True(redis.GetProperty("tls_enabled").GetBoolean());
            Assert.DoesNotContain(AdminKey, body.GetRawText());
            return Response(HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://wrong.example/ignored/") };
        var result = await Client(http, endpoint).CreateSurfaceAsync(Model, Deployment);
        Assert.Equal("surface-1", result.SurfaceId);
        Assert.Equal("travel", result.Name);
        Assert.Equal("active", result.Status);
        Assert.Equal(1, handler.Calls);
        Assert.False(http.DefaultRequestHeaders.Contains("X-API-Key"));
        Assert.DoesNotContain(RedisPassword, Deployment.ToString());
        Assert.DoesNotContain(RedisPassword, JsonSerializer.Serialize(Deployment));
    }

    [Fact]
    public async Task UpdatesOnlySuppliedPropertiesAndReturnsTheTypedModel()
    {
        using var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("http://localhost:8080/api/v1/context-surfaces/surface%3Fone%23two", request.RequestUri!.AbsoluteUri);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            var body = document.RootElement;
            Assert.False(body.TryGetProperty("name", out _));
            Assert.False(body.TryGetProperty("data_source", out _));
            Assert.False(body.TryGetProperty("metadata", out _));
            Assert.Equal("", body.GetProperty("description").GetString());
            Assert.Equal(Model.Title, body.GetProperty("data_model").GetProperty("title").GetString());
            return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = "surface-1", name = "travel", data_model = Model, unknown = "ignored" }));
        });
        using var http = new HttpClient(handler);
        var result = await Client(http).UpdateSurfaceAsync("surface?one#two", new() { DataModel = Model, Description = "" });
        Assert.Equal(Model.Title, result.DataModel!.Title);
        Assert.Single(result.DataModel.Entities);
    }

    [Fact]
    public async Task GetsSurfaceAndResolvesCurrentCredentialOnEveryRequest()
    {
        var credentials = new RotatingCredentials();
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("http://localhost:8080/api/v1/context-surfaces/surface-1", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Equal($"admin-{credentials.Calls}", Assert.Single(request.Headers.GetValues("X-API-Key")));
            return Task.FromResult(Response());
        });
        using var http = new HttpClient(handler);
        var client = new ContextRetrieverManagementClient(http, new() { Endpoint = new("http://localhost:8080") }, credentials);
        await client.GetSurfaceAsync("surface-1");
        await client.GetSurfaceAsync("surface-1");
        Assert.Equal(2, credentials.Calls);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ReportsHttpStatusWithoutLeakingBodiesOrRetryingWrites(int status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response((HttpStatusCode)status, AdminKey + RedisPassword)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ContextRetrieverManagementException>(() => Client(http).CreateSurfaceAsync(Model, Deployment));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("surface.create", error.Operation);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(AdminKey, error.ToString());
        Assert.DoesNotContain(RedisPassword, error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not-json-synthetic-redis-password")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"\",\"name\":\"travel\"}")]
    [InlineData("{\"id\":\"x\",\"name\":null}")]
    public async Task RejectsInvalidSuccessResponsesWithoutRawPayloads(string body)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ContextRetrieverManagementException>(() => Client(http).GetSurfaceAsync("surface-1"));
        Assert.Equal("surface.get", error.Operation);
        Assert.Null(error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(RedisPassword, error.ToString());
    }

    [Fact]
    public async Task SanitizesTransportFailuresWithoutRetrying()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException(AdminKey + RedisPassword));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ContextRetrieverManagementException>(() => Client(http).CreateSurfaceAsync(Model, Deployment));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(AdminKey, error.ToString());
        Assert.DoesNotContain(RedisPassword, error.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PropagatesCancellationDuringDispatchAndDoesNotRetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, cancellation) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellation);
            return Response();
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = Client(http).CreateSurfaceAsync(Model, Deployment, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PreCancelledOperationsNeverResolveCredentialsOrSend()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        var credential = new RotatingCredentials();
        var client = new ContextRetrieverManagementClient(http, new() { Endpoint = new("http://localhost:8080") }, credential);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSurfaceAsync("surface-1", new CancellationToken(true)));
        Assert.Equal(0, credential.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("has space")]
    [InlineData("a\\b")]
    public async Task RejectsInvalidIdsBeforeSending(string id)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Client(http).GetSurfaceAsync(id));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("https://host.example/mcp")]
    [InlineData("https://host.example/mcp/")]
    [InlineData("https://user:password@host.example")]
    [InlineData("https://host.example?token=secret")]
    [InlineData("https://host.example#fragment")]
    [InlineData("file:///tmp/unused")]
    [InlineData("relative/path")]
    public void RejectsUnsafeOrRuntimeEndpoints(string endpoint)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new ContextRetrieverManagementClient(http,
            new() { Endpoint = new(endpoint, UriKind.RelativeOrAbsolute) }, new FixedContextRetrieverManagementCredentialProvider(AdminKey)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("key\r\nX-Injected: value")]
    public async Task RejectsInvalidRotatingCredentialsBeforeSending(string credential)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        var client = new ContextRetrieverManagementClient(http, new() { Endpoint = new("http://localhost:8080") }, new BadCredentials(credential));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetSurfaceAsync("surface-1"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RejectsInvalidDeploymentAndEmptyUpdates()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response()));
        using var http = new HttpClient(handler);
        var client = Client(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateSurfaceAsync(Model, new() { Name = " ", RedisAddress = "redis:6379" }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.CreateSurfaceAsync(Model, new() { Name = "travel", RedisAddress = "redis:6379", RedisDatabase = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateSurfaceAsync("surface-1", new()));
        Assert.Equal(0, handler.Calls);
    }

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
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 34\r\nConnection: close\r\n\r\n{\"id\":\"surface-1\",\"name\":\"travel\"}",
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
        services.AddRedisContextRetrieverManagement(new() { Endpoint = new(address) }, new FixedContextRetrieverManagementCredentialProvider(AdminKey))
            .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(5));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IContextRetrieverManagementClient>();
        var redirect = await Assert.ThrowsAsync<ContextRetrieverManagementException>(() => client.GetSurfaceAsync("surface-1", deadline.Token));
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("surface-1", (await client.GetSurfaceAsync("surface-1", deadline.Token)).SurfaceId);
        await server;
        Assert.Equal(2, captured.Count);
        Assert.All(captured, headers =>
        {
            Assert.StartsWith("GET /api/v1/context-surfaces/surface-1 ", headers);
            Assert.DoesNotContain("Cookie:", headers, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ModelingAssemblyNoLongerContainsDeploymentOrProcessTypes()
    {
        Assert.DoesNotContain(typeof(ContextDataModel).Assembly.GetExportedTypes(),
            type => type.Name.Contains("ContextCtl", StringComparison.Ordinal) || type.Name == "ContextSurfaceDeployment");
    }

    private static ContextRetrieverManagementClient Client(HttpClient http, string endpoint = "http://localhost:8080") =>
        new(http, new() { Endpoint = new(endpoint) }, new FixedContextRetrieverManagementCredentialProvider(AdminKey));
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"id\":\"surface-1\",\"name\":\"travel\",\"status\":\"active\"}") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
    private sealed class RotatingCredentials : IContextRetrieverManagementCredentialProvider
    {
        public int Calls { get; private set; }
        public ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult($"admin-{++Calls}");
    }
    private sealed class BadCredentials(string credential) : IContextRetrieverManagementCredentialProvider
    {
        public ValueTask<string> GetCredentialAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(credential);
    }
}
