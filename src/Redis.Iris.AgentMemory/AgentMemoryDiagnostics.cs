// Copyright (c) Redis, Inc.

using System.Diagnostics;

namespace Redis.Iris.AgentMemory;

/// <summary>OpenTelemetry-compatible diagnostics emitted by the Agent Memory client.</summary>
public static class AgentMemoryDiagnostics
{
    /// <summary>Gets the activity source name.</summary>
    public const string ActivitySourceName = "Redis.Iris.AgentMemory";

    /// <summary>Gets the connector activity source.</summary>
    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);

    internal static Activity? StartActivity(string operation, Uri endpoint)
    {
        var activity = ActivitySource.StartActivity($"redis.iris.{operation}", ActivityKind.Client);
        activity?.SetTag("redis.iris.operation", operation);
        activity?.SetTag("server.address", endpoint.Host);
        activity?.SetTag("redis.iris.retry_count", 0);
        return activity;
    }

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
