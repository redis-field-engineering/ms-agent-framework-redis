using System.Diagnostics;

namespace Redis.Iris.ContextRetriever;

/// <summary>OpenTelemetry-compatible diagnostics for the Context Retriever connector.</summary>
public static class ContextRetrieverDiagnostics
{
    /// <summary>Activity source name applications should subscribe to.</summary>
    public const string ActivitySourceName = "Redis.Iris.ContextRetriever";
    /// <summary>Connector activity source.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    internal static Activity? Start(string operation) => ActivitySource.StartActivity($"redis.iris.context_retriever.{operation}", ActivityKind.Client);

    internal static void Failed(Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        activity?.SetTag("error.type", exception.GetType().FullName);
    }
}
