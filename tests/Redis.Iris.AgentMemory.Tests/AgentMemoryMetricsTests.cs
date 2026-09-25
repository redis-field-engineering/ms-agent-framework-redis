// Copyright (c) Redis, Inc.

using System.Diagnostics.Metrics;
using System.Net;
using System.Text;

namespace Redis.Iris.AgentMemory.Tests;

public sealed class AgentMemoryMetricsTests
{
    [Fact]
    public async Task SearchEmitsLowCardinalityMeasurements()
    {
        var measurements = new List<(string Instrument, object Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AgentMemoryDiagnostics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        var client = new RedisAgentMemoryClient(new HttpClient(new StubHandler()), new()
        {
            Endpoint = new("https://private-host.example.test"),
            StoreId = "private-store",
        }, new FixedAgentMemoryCredentialProvider("private-token"));

        await client.SearchLongTermMemoryAsync(new() { Text = "private-query", Filter = new() { OwnerId = new() { Eq = "private-owner" } } });

        Assert.Contains(measurements, static item => item.Instrument == "redis.iris.requests");
        Assert.Contains(measurements, static item => item.Instrument == "redis.iris.operation.duration");
        Assert.Contains(measurements, static item => item.Instrument == "redis.iris.recall.results");
        var renderedTags = string.Join(" ", measurements.SelectMany(static item => item.Tags).Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain("private-host", renderedTags);
        Assert.DoesNotContain("private-store", renderedTags);
        Assert.DoesNotContain("private-owner", renderedTags);
        Assert.DoesNotContain("private-query", renderedTags);
        Assert.All(measurements.SelectMany(static item => item.Tags), static tag =>
            Assert.Contains(tag.Key, new[] { "redis.iris.operation", "redis.iris.outcome", "redis.iris.reason" }));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json"),
            });
    }
}
