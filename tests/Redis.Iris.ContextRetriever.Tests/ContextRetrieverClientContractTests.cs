using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
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
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal("Bearer secret-agent-key", value));
        Assert.Equal(["initialize", "notifications/initialized", "tools/list", "tools/call"], handler.Methods);
    }

    [Fact]
    public async Task CancellationInterruptsInitialization()
    {
        var handler = new McpContractHandler { BlockInitialize = true };
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.ConnectAsync(s_options, "key", cancellation.Token));
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
    }

    [Fact]
    public async Task RejectsInsecureRemoteEndpoints()
    {
        var factory = new ContextRetrieverClientFactory();
        var options = new ContextRetrieverClientOptions { Endpoint = new Uri("http://context.example.test/mcp") };

        await Assert.ThrowsAsync<ArgumentException>(() => factory.ConnectAsync(options, "key"));
    }

    [Fact]
    public async Task RedirectDoesNotForwardAgentKey()
    {
        var handler = new RedirectHandler();
        var factory = new ContextRetrieverClientFactory(handlerFactory: () => handler, loggerFactory: null);

        var error = await Assert.ThrowsAsync<ContextRetrieverConnectionException>(() => factory.ConnectAsync(s_options, "redirect-secret"));

        Assert.Equal(1, handler.RequestCount);
        Assert.DoesNotContain("redirect-secret", error.ToString());
    }

    private sealed class McpContractHandler : HttpMessageHandler
    {
        public bool BlockInitialize { get; init; }
        public bool ReturnInvalidToolList { get; init; }
        public List<string> Methods { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);

            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()!;
            Methods.Add(method);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted);
            if (method == "initialize" && BlockInitialize) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

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

    private sealed class RedirectHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Headers = { Location = new Uri("https://attacker.example.test/mcp") },
            });
        }
    }
}
