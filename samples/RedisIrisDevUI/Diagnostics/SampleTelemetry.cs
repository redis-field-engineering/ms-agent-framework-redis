// Copyright (c) Redis, Inc.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Redis.Iris.AgentMemory;
using Redis.Iris.ContextRetriever;

namespace RedisIrisDevUI;

internal static class SampleTelemetry
{
    public static void AddSampleTelemetry(this WebApplicationBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService(
                    serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? "redis-iris-devui"))
                .WithTracing(tracing => tracing
                    // ASP.NET Core creates an ambient request Activity even when HTTP instrumentation is not exported.
                    // Sample the explicitly registered connector source instead of inheriting that unsampled parent.
                    .SetSampler(new AlwaysOnSampler())
                    .AddSource(TravelAgentRegistration.TelemetrySourceName)
                    .AddSource(AgentMemoryDiagnostics.ActivitySourceName)
                    .AddSource(ContextRetrieverDiagnostics.ActivitySourceName)
                    .AddOtlpExporter())
                .WithMetrics(metrics => metrics
                    .AddMeter(AgentMemoryDiagnostics.MeterName)
                    .AddMeter(ContextRetrieverDiagnostics.MeterName)
                    .AddOtlpExporter());
        }
    }

    public static IDisposable StartConsoleTelemetry()
    {
        var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.ActivitySourceName ||
                source.Name == ContextRetrieverDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Console.WriteLine(
                $"TRACE      : operation={activity.OperationName} status={activity.Status} duration_ms={activity.Duration.TotalMilliseconds:F1}"),
        };
        ActivitySource.AddActivityListener(activityListener);
        var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AgentMemoryDiagnostics.MeterName ||
                instrument.Meter.Name == ContextRetrieverDiagnostics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, _, _) => Console.WriteLine($"METRIC     : {instrument.Name}={value}"));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, _, _) => Console.WriteLine($"METRIC     : {instrument.Name}={value:F2}"));
        meterListener.Start();
        return new CompositeDisposable(activityListener, meterListener);
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }
}
