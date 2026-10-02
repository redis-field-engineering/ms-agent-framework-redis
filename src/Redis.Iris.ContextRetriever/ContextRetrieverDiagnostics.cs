using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Redis.Iris.ContextRetriever;

/// <summary>OpenTelemetry-compatible diagnostics for the Context Retriever connector.</summary>
public static class ContextRetrieverDiagnostics
{
    /// <summary>Activity source name applications should subscribe to.</summary>
    public const string ActivitySourceName = "Redis.Iris.ContextRetriever";
    /// <summary>Connector activity source.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
    /// <summary>Meter name applications should subscribe to.</summary>
    public const string MeterName = "Redis.Iris.ContextRetriever";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Connections = Meter.CreateCounter<long>("redis.iris.context_retriever.connection.attempts");
    private static readonly Counter<long> ConnectionFailures = Meter.CreateCounter<long>("redis.iris.context_retriever.connection.failures");
    private static readonly UpDownCounter<long> ActiveConnections = Meter.CreateUpDownCounter<long>("redis.iris.context_retriever.connections.active");
    private static readonly Histogram<double> DiscoveryDuration = Meter.CreateHistogram<double>("redis.iris.context_retriever.discovery.duration", "ms");
    private static readonly Counter<long> DiscoveryFailures = Meter.CreateCounter<long>("redis.iris.context_retriever.discovery.failures");
    private static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>("redis.iris.context_retriever.tool.duration", "ms");
    private static readonly Counter<long> ToolFailures = Meter.CreateCounter<long>("redis.iris.context_retriever.tool.failures");
    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>("redis.iris.context_retriever.client_cache.requests");
    private static readonly Counter<long> Reconnects = Meter.CreateCounter<long>("redis.iris.context_retriever.reconnects");

    internal static Activity? Start(string operation) => ActivitySource.StartActivity($"redis.iris.context_retriever.{operation}", ActivityKind.Client);
    internal static void ConnectionAttempt() => Connections.Add(1);
    internal static void ConnectionFailed() => ConnectionFailures.Add(1);
    internal static void ActiveConnection(int delta) => ActiveConnections.Add(delta);
    internal static void Discovery(double elapsedMs, bool failed)
    {
        DiscoveryDuration.Record(elapsedMs);
        if (failed) DiscoveryFailures.Add(1);
    }
    internal static void Tool(double elapsedMs, bool failed)
    {
        ToolDuration.Record(elapsedMs);
        if (failed) ToolFailures.Add(1);
    }
    internal static void Cache(bool hit) => CacheRequests.Add(1, new KeyValuePair<string, object?>("cache.outcome", hit ? "hit" : "miss"));
    internal static void Reconnect() => Reconnects.Add(1);

    internal static void Failed(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity?.SetTag("error.type", exception.GetType().FullName);
    }
}
