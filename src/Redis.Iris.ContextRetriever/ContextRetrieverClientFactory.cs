using System.Diagnostics;
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
        CancellationToken cancellationToken = default) =>
        await ConnectAsync(options, agentKey, delegatedBearerToken: null, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IContextRetrieverClient> ConnectAsync(
        ContextRetrieverClientOptions options,
        string agentKey,
        string? delegatedBearerToken,
        CancellationToken cancellationToken = default)
    {
        using var connectActivity = ContextRetrieverDiagnostics.Start("connect");
        ContextRetrieverDiagnostics.ConnectionAttempt();
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(agentKey);
        if (delegatedBearerToken is not null) ArgumentException.ThrowIfNullOrWhiteSpace(delegatedBearerToken);

        var endpoint = options.Endpoint;
        var headerValue = string.IsNullOrWhiteSpace(options.AgentKeyScheme)
            ? agentKey
            : $"{options.AgentKeyScheme} {agentKey}";
        var headers = new Dictionary<string, string>
        {
            [options.AgentKeyHeaderName] = headerValue,
        };
        if (delegatedBearerToken is not null)
        {
            headers["Authorization"] = $"Bearer {delegatedBearerToken}";
        }
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "redis-iris-context-retriever",
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = options.ConnectionTimeout,
            AdditionalHeaders = headers,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 3,
        };

        McpClient? client = null;
        HttpClientTransport? transport = null;
        var ownershipTransferred = false;
        try
        {
            transport = CreateTransport(transportOptions);
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
                var discoveryStarted = Stopwatch.GetTimestamp();
                using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                discoveryTimeout.CancelAfter(options.DiscoveryTimeout);
                try
                {
                    tools = await client.ListToolsAsync(cancellationToken: discoveryTimeout.Token).ConfigureAwait(false);
                    ContextRetrieverDiagnostics.Discovery(Stopwatch.GetElapsedTime(discoveryStarted).TotalMilliseconds, false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && discoveryTimeout.IsCancellationRequested)
                {
                    ContextRetrieverDiagnostics.Discovery(Stopwatch.GetElapsedTime(discoveryStarted).TotalMilliseconds, true);
                    ContextRetrieverDiagnostics.Failed(discoveryActivity, ex);
                    throw new ContextRetrieverTimeoutException("tool discovery", ex);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ContextRetrieverDiagnostics.Discovery(Stopwatch.GetElapsedTime(discoveryStarted).TotalMilliseconds, true);
                    ContextRetrieverDiagnostics.Failed(discoveryActivity, ex);
                    throw new ContextRetrieverDiscoveryException("Context Retriever tool discovery failed.", ex);
                }
            }

            ContextRetrieverDiagnostics.ActiveConnection(1);
            var executionGate = new SemaphoreSlim(options.MaximumConcurrentToolExecutions, options.MaximumConcurrentToolExecutions);
            var connected = new ContextRetrieverClient(client, transport, tools.Select(tool => (AIFunction)new TracingAIFunction(tool, executionGate, options.ExecutionTimeout)).ToArray(), executionGate);
            ownershipTransferred = true;
            return connected;
        }
        catch (ContextRetrieverException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ContextRetrieverDiagnostics.Failed(connectActivity, ex);
            ContextRetrieverDiagnostics.ConnectionFailed();
            throw new ContextRetrieverConnectionException("Context Retriever MCP connection failed.", ex);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                // Cancellation must clean up just as failures do. Preserve the original error
                // if a broken transport also fails during best-effort teardown.
                try
                {
                    try { if (client is not null) await client.DisposeAsync().ConfigureAwait(false); }
                    finally { if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false); }
                }
                catch { }
            }
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

    private sealed class ContextRetrieverClient(McpClient client, HttpClientTransport transport, IReadOnlyList<AIFunction> tools, SemaphoreSlim executionGate) : IContextRetrieverClient
    {
        private int _disposed;
        public IReadOnlyList<AIFunction> Tools { get; } = tools;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { await client.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await transport.DisposeAsync().ConfigureAwait(false); }
                finally { executionGate.Dispose(); ContextRetrieverDiagnostics.ActiveConnection(-1); }
            }

        }
    }
}
