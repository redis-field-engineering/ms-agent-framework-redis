// Copyright (c) Redis, Inc.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Redis.Iris.AgentMemory;

/// <summary>OpenTelemetry-compatible diagnostics emitted by the Agent Memory client.</summary>
public static class AgentMemoryDiagnostics
{
    /// <summary>Gets the activity source name.</summary>
    public const string ActivitySourceName = "Redis.Iris.AgentMemory";

    /// <summary>Gets the connector activity source.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    /// <summary>Gets the connector meter name.</summary>
    public const string MeterName = "Redis.Iris.AgentMemory";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>("redis.iris.requests", "{request}");
    private static readonly Counter<long> ErrorCounter = Meter.CreateCounter<long>("redis.iris.errors", "{error}");
    private static readonly Counter<long> RetryCounter = Meter.CreateCounter<long>("redis.iris.retries", "{retry}");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("redis.iris.operation.duration", "ms");
    private static readonly Histogram<long> RecallResults = Meter.CreateHistogram<long>("redis.iris.recall.results", "{memory}");
    private static readonly Counter<long> SkippedMessages = Meter.CreateCounter<long>("redis.iris.messages.skipped", "{message}");

    internal static Activity? StartActivity(string operation, Uri endpoint)
    {
        var activity = ActivitySource.StartActivity($"redis.iris.{operation}", ActivityKind.Client);
        activity?.SetTag("redis.iris.operation", operation);
        activity?.SetTag("server.address", endpoint.Host);
        activity?.SetTag("redis.iris.retry_count", 0);
        return activity;
    }

    internal static long StartRequest(string operation)
    {
        RequestCounter.Add(1, new KeyValuePair<string, object?>("redis.iris.operation", operation));
        RetryCounter.Add(0, new KeyValuePair<string, object?>("redis.iris.operation", operation));
        return Stopwatch.GetTimestamp();
    }

    internal static void RecordMetricsSuccess(string operation, long startedAt, int resultCount)
    {
        Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("redis.iris.operation", operation),
            new KeyValuePair<string, object?>("redis.iris.outcome", "success"));
        if (operation == "ltm.search")
        {
            RecallResults.Record(resultCount, new KeyValuePair<string, object?>("redis.iris.operation", operation));
        }
    }

    internal static void RecordMetricsError(string operation, long startedAt)
    {
        Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("redis.iris.operation", operation),
            new KeyValuePair<string, object?>("redis.iris.outcome", "error"));
        ErrorCounter.Add(1, new KeyValuePair<string, object?>("redis.iris.operation", operation));
    }

    /// <summary>Records a skipped message using a bounded reason.</summary>
    public static void RecordSkippedMessage(string reason) =>
        SkippedMessages.Add(1, new KeyValuePair<string, object?>("redis.iris.reason", reason));

    internal static void SetSuccess(Activity? activity, int resultCount = 0)
    {
        activity?.SetTag("redis.iris.outcome", "success");
        activity?.SetTag("http.response.status_code", 200);
        activity?.SetTag("redis.iris.result_count", resultCount);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    internal static void SetError(Activity? activity, Exception exception)
    {
        activity?.SetTag("redis.iris.outcome", "error");
        activity?.SetTag("error.type", exception.GetType().FullName);
        if (exception is AgentMemoryException agentMemoryException && agentMemoryException.StatusCode is { } statusCode)
        {
            activity?.SetTag("http.response.status_code", (int)statusCode);
        }

        activity?.SetStatus(ActivityStatusCode.Error);
    }
}
