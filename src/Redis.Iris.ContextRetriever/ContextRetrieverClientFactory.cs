using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Redis.Iris.ContextRetriever;

/// <summary>Creates Streamable HTTP sessions using the official MCP C# SDK.</summary>
public sealed class ContextRetrieverClientFactory : IContextRetrieverClientFactory
{
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <summary>Creates a factory with optional MCP SDK logging.</summary>
    public ContextRetrieverClientFactory(ILoggerFactory? loggerFactory = null)
        : this(loggerFactory, null) { }

    internal ContextRetrieverClientFactory(ILoggerFactory? loggerFactory, Func<HttpMessageHandler>? handlerFactory)
    {
        _loggerFactory = loggerFactory;
        _handlerFactory = handlerFactory;
    }

    /// <inheritdoc />
    public async Task<IContextRetrieverClient> ConnectAsync(
        ContextRetrieverClientOptions options,
        string agentKey,
        CancellationToken cancellationToken = default)
    {
        using var connectActivity = ContextRetrieverDiagnostics.Start("connect");
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);

        var endpoint = options.Endpoint;
        var headerValue = string.IsNullOrWhiteSpace(options.AgentKeyScheme)
            ? agentKey
            : $"{options.AgentKeyScheme} {agentKey}";
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "redis-iris-context-retriever",
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = options.ConnectionTimeout,
            AdditionalHeaders = new Dictionary<string, string>
            {
                [options.AgentKeyHeaderName] = headerValue,
            },
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 3,
        };

        McpClient? client = null;
        try
        {
            var transport = CreateTransport(transportOptions);
            using (var initializeActivity = ContextRetrieverDiagnostics.Start("initialize"))
            {
                try
                {
                    client = await McpClient.CreateAsync(
                        transport,
                        new McpClientOptions { ProtocolVersion = "2025-11-25", InitializationTimeout = options.ConnectionTimeout },
                        _loggerFactory,
                        cancellationToken).ConfigureAwait(false);
                    initializeActivity?.SetTag("redis.iris.authorization.outcome", "success");
                }
                catch (Exception ex)
                {
                    initializeActivity?.SetTag("redis.iris.authorization.outcome", "failure");
                    ContextRetrieverDiagnostics.Failed(initializeActivity, ex);
                    throw;
                }
            }

            IList<McpClientTool> tools;
            using (var discoveryActivity = ContextRetrieverDiagnostics.Start("discover"))
            {
                try
                {
                    tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ContextRetrieverDiagnostics.Failed(discoveryActivity, ex);
                    throw new ContextRetrieverDiscoveryException("Context Retriever tool discovery failed.", ex);
                }
            }

            return new ContextRetrieverClient(client, tools.Select(static tool => (AIFunction)new TracingAIFunction(tool)).ToArray());
        }
        catch (ContextRetrieverException)
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ContextRetrieverDiagnostics.Failed(connectActivity, ex);
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            throw new ContextRetrieverConnectionException("Context Retriever MCP connection failed.", ex);
        }
    }

    private HttpClientTransport CreateTransport(HttpClientTransportOptions options)
    {
        if (_handlerFactory is null)
        {
            var secureHandler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
            };
            var secureHttpClient = new HttpClient(secureHandler, disposeHandler: true);
            return new HttpClientTransport(options, secureHttpClient, _loggerFactory, ownsHttpClient: true);
        }

        var handler = _handlerFactory();
        var httpClient = new HttpClient(handler, disposeHandler: true);
        return new HttpClientTransport(options, httpClient, _loggerFactory, ownsHttpClient: true);
    }

    private sealed class ContextRetrieverClient(McpClient client, IReadOnlyList<AIFunction> tools) : IContextRetrieverClient
    {
        public IReadOnlyList<AIFunction> Tools { get; } = tools;

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
