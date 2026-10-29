using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Xunit;

namespace Redis.Iris.ContextRetriever.Tests;

public sealed class ContextRetrieverClientContractTests
{
    private static readonly ContextRetrieverClientOptions s_options = new()
    {
        Endpoint = new Uri("https://context.example.test/mcp"),
    };

    [Fact]
    public async Task ConnectInitializesListsAndInvokesToolsWithAgentKey()
    {
        var handler = new McpContractHandler();
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);

        await using var client = await factory.ConnectAsync(s_options, "secret-agent-key", CancellationToken.None);

        var tool = Assert.Single(client.Tools);
        Assert.Equal("find_customer", tool.Name);
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "Ada" }), CancellationToken.None);
        Assert.Contains("Ada", result?.ToString());
        Assert.All(handler.AgentKeyHeaders, value => Assert.Equal("secret-agent-key", value));
        Assert.All(handler.AuthorizationHeaders, Assert.Null);
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"], handler.Methods);
    }

    [Fact]
    public async Task DelegatedConnectionSendsAgentKeyAndBearerJwtOnEveryMcpRequest()
    {
        var handler = new McpContractHandler();
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);

        await using var client = await factory.ConnectAsync(s_options, "tenant-agent-key", "signed-user-jwt", CancellationToken.None);
        await Assert.Single(client.Tools).InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "Ada" }),
            CancellationToken.None);

        Assert.All(handler.AgentKeyHeaders, value => Assert.Equal("tenant-agent-key", value));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal("Bearer signed-user-jwt", value));
    }

    [Fact]
    public async Task CancellationInterruptsInitialization()
    {
        var handler = new McpContractHandler { BlockInitialize = true };
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.ConnectAsync(s_options, "key", cancellation.Token));
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task CallerCancellationDuringDiscoveryDisposesTransport()
    {
        var handler = new McpContractHandler { BlockToolList = true };
        var factory = new ContextRetrieverClientFactory(null, () => handler);
        using var cancellation = new CancellationTokenSource();
        var connect = factory.ConnectAsync(s_options, "key", cancellation.Token);
        await handler.ToolListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.True(handler.Disposed);
    }

    [Fact]
    public async Task ProtocolErrorIsWrappedWithoutCredentialDisclosure()
    {
        var handler = new McpContractHandler { ReturnInvalidToolList = true };
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);

        var error = await Assert.ThrowsAsync<ContextRetrieverDiscoveryException>(
            () => factory.ConnectAsync(s_options, "never-print-this", CancellationToken.None));

        Assert.DoesNotContain("never-print-this", error.ToString());
    }

    [Fact]
    public async Task ADisposedSessionCanBeReconnectedWithAFreshProtocolSession()
    {
        var handlers = new List<McpContractHandler>();
        var factory = new ContextRetrieverClientFactory(handlerFactory: () =>
        {
            var handler = new McpContractHandler();
            handlers.Add(handler);
            return handler;
        }, loggerFactory: null);

        await using (await factory.ConnectAsync(s_options, "key", CancellationToken.None)) { }
        await using (await factory.ConnectAsync(s_options, "key", CancellationToken.None)) { }

        Assert.Equal(2, handlers.Count);
        Assert.All(handlers, handler => Assert.Contains("initialize", handler.Methods));
        Assert.All(handlers, handler => Assert.True(handler.Disposed));
    }

    [Fact]
    public async Task RejectsInsecureRemoteEndpoints()
    {
        var factory = new ContextRetrieverClientFactory();
        var options = new ContextRetrieverClientOptions { Endpoint = new Uri("http://context.example.test/mcp") };

        await Assert.ThrowsAsync<ArgumentException>(() => factory.ConnectAsync(options, "key"));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task DefaultTransportDoesNotForwardCredentialsAcrossRedirects(int status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var original = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        original.Start();
        target.Start();
        var originalPort = ((IPEndPoint)original.LocalEndpoint).Port;
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var first = ReplyWithRedirectAsync(original, targetPort, status, timeout.Token);
        var factory = new ContextRetrieverClientFactory();

        var error = await Assert.ThrowsAsync<ContextRetrieverConnectionException>(() => factory.ConnectAsync(
            new() { Endpoint = new($"http://127.0.0.1:{originalPort}/mcp"), ConnectionTimeout = TimeSpan.FromSeconds(2) },
            "redirect-agent-secret", "redirect-user-secret", timeout.Token));

        var headers = await first;
        Assert.Contains("X-API-Key: redirect-agent-secret", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Authorization: Bearer redirect-user-secret", headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(target.Pending());
        Assert.DoesNotContain("redirect-agent-secret", error.ToString());
        Assert.DoesNotContain("redirect-user-secret", error.ToString());
    }

    [Fact]
    public async Task TracesConnectInitializeDiscoveryAndExecutionWithoutSensitiveData()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ContextRetrieverDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);
        using var parentSource = new ActivitySource("test-parent");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-parent",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);
        using var parent = parentSource.StartActivity("agent-invocation")!;

        var factory = new ContextRetrieverClientFactory(handlerFactory: () => new McpContractHandler(), loggerFactory: null);
        await using var client = await factory.ConnectAsync(s_options, "trace-secret");
        await Assert.Single(client.Tools).InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "private-name" }));

        Assert.Equal(["redis.iris.context_retriever.initialize", "redis.iris.context_retriever.discover", "redis.iris.context_retriever.connect", "redis.iris.context_retriever.execute"], activities.Select(a => a.OperationName));
        Assert.All(activities, activity => Assert.Equal(parent.TraceId, activity.TraceId));
        var serialized = string.Join('|', activities.SelectMany(a => a.TagObjects).Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("trace-secret", serialized);
        Assert.DoesNotContain("private-name", serialized);
        Assert.DoesNotContain("context.example.test", serialized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SdkPayloadLoggingRequiresExplicitOptIn(bool enableSensitiveLogging)
    {
        using var capture = new CapturingLoggerFactory();
        var handler = new McpContractHandler();
        var factory = new ContextRetrieverClientFactory(capture, () => handler, enableSensitiveLogging);
        await using (var client = await factory.ConnectAsync(s_options, "private-key", "private-token"))
        {
            var result = await Assert.Single(client.Tools).InvokeAsync(new(new Dictionary<string, object?> { ["name"] = "private-query" }));
            Assert.Contains("Found private-query", result?.ToString());
        }
        Assert.Contains("tools/call", handler.Methods);
        var logs = string.Join("\n", capture.Messages);
        Assert.Equal(enableSensitiveLogging, logs.Contains("private-query", StringComparison.Ordinal));
        Assert.Equal(enableSensitiveLogging, logs.Contains("Found private-query", StringComparison.Ordinal));
        Assert.DoesNotContain("private-key", logs);
        Assert.DoesNotContain("private-token", logs);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }

    [Fact]
    public async Task MetricsCoverMcpHealthWithoutSensitiveDimensions()
    {
        var measurements = new System.Collections.Concurrent.ConcurrentQueue<(string Name, string Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ContextRetrieverDiagnostics.MeterName) meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, Tags(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Enqueue((instrument.Name, Tags(tags))));
        listener.Start();

        var factory = new ContextRetrieverClientFactory(handlerFactory: () => new McpContractHandler(), loggerFactory: null);
        await using (var client = await factory.ConnectAsync(s_options, "metric-secret"))
        {
            await Assert.Single(client.Tools).InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "metric-private" }));
        }

        Assert.Contains(measurements, item => item.Name == "redis.iris.context_retriever.connection.attempts");
        Assert.Contains(measurements, item => item.Name == "redis.iris.context_retriever.discovery.duration");
        Assert.Contains(measurements, item => item.Name == "redis.iris.context_retriever.tool.duration");
        Assert.Contains(measurements, item => item.Name == "redis.iris.context_retriever.connections.active");
        var serialized = string.Join('|', measurements.Select(item => item.Tags));
        Assert.DoesNotContain("metric-secret", serialized);
        Assert.DoesNotContain("metric-private", serialized);
        Assert.DoesNotContain("context.example.test", serialized);

        static string Tags(ReadOnlySpan<KeyValuePair<string, object?>> tags) => string.Join(',', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}"));
    }

    [Fact]
    public async Task DiscoveryAndExecutionHaveIndependentTimeouts()
    {
        var discoveryFactory = new ContextRetrieverClientFactory(handlerFactory: () => new McpContractHandler { BlockToolList = true }, loggerFactory: null);
        var discoveryOptions = new ContextRetrieverClientOptions
        {
            Endpoint = s_options.Endpoint,
            ConnectionTimeout = TimeSpan.FromSeconds(2),
            DiscoveryTimeout = TimeSpan.FromMilliseconds(50),
        };
        var discoveryError = await Assert.ThrowsAsync<ContextRetrieverTimeoutException>(() => discoveryFactory.ConnectAsync(discoveryOptions, "key"));
        Assert.Equal("tool discovery", discoveryError.Operation);

        var executionFactory = new ContextRetrieverClientFactory(handlerFactory: () => new McpContractHandler { BlockToolCall = true }, loggerFactory: null);
        var executionOptions = new ContextRetrieverClientOptions
        {
            Endpoint = s_options.Endpoint,
            ExecutionTimeout = TimeSpan.FromMilliseconds(50),
        };
        await using var client = await executionFactory.ConnectAsync(executionOptions, "key");
        var executionError = await Assert.ThrowsAsync<ContextRetrieverTimeoutException>(
            () => Assert.Single(client.Tools).InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "Ada" })).AsTask());
        Assert.Equal("tool execution", executionError.Operation);
    }

    [Fact]
    public async Task BoundsConcurrentToolExecutionPerProfile()
    {
        var handler = new McpContractHandler { ToolDelay = TimeSpan.FromMilliseconds(40) };
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);
        await using var client = await factory.ConnectAsync(new()
        {
            Endpoint = s_options.Endpoint,
            MaximumConcurrentToolExecutions = 1,
        }, "key");
        var tool = Assert.Single(client.Tools);

        await Task.WhenAll(
            tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "one" })).AsTask(),
            tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["name"] = "two" })).AsTask());

        Assert.Equal(1, handler.MaximumActiveToolCalls);
    }

    private sealed class McpContractHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public TaskCompletionSource ToolListStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public bool BlockInitialize { get; init; }
        public bool ReturnInvalidToolList { get; init; }
        public bool BlockToolList { get; init; }
        public bool BlockToolCall { get; init; }
        public TimeSpan ToolDelay { get; init; }
        public int MaximumActiveToolCalls { get; private set; }
        private int _activeToolCalls;
        public List<string> Methods { get; } = [];
        public List<string?> AgentKeyHeaders { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);

            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            AgentKeyHeaders.Add(request.Headers.TryGetValues("X-API-Key", out var values) ? values.Single() : null);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            if (method == "initialize" && BlockInitialize) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (method == "tools/list")
            {
                ToolListStarted.TrySetResult();
                if (BlockToolList) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (method == "tools/call")
            {
                var active = Interlocked.Increment(ref _activeToolCalls);
                MaximumActiveToolCalls = Math.Max(MaximumActiveToolCalls, active);
                try
                {
                    if (BlockToolCall) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    if (ToolDelay > TimeSpan.Zero) await Task.Delay(ToolDelay, cancellationToken);
                }
                finally { Interlocked.Decrement(ref _activeToolCalls); }
            }

            var id = root.GetProperty("id").GetRawText();
            var result = method switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"contract-test\",\"version\":\"1\"}}",
                "tools/list" when ReturnInvalidToolList => "{\"tools\":\"invalid\"}",
                "tools/list" => "{\"tools\":[{\"name\":\"find_customer\",\"description\":\"Find a customer\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}}]}",
                "tools/call" => $"{{\"content\":[{{\"type\":\"text\",\"text\":\"Found {root.GetProperty("params").GetProperty("arguments").GetProperty("name").GetString()}\"}}],\"isError\":false}}",
                _ => throw new InvalidOperationException(method),
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", "contract-session");
            return response;
        }
    }

    private static async Task<string> ReplyWithRedirectAsync(TcpListener listener, int targetPort, int status, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { Length: > 0 } line) headers.AppendLine(line);
        var response = $"HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:{targetPort}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        return headers.ToString();
    }
}
