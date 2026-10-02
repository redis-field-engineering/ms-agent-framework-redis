// Copyright (c) Redis, Inc.

using System.Diagnostics;
using System.Net;
using System.Text;

namespace Redis.Iris.AgentMemory.Tests;

public sealed class AgentMemoryTracingTests
{
    [Fact]
    public async Task TryLoadMissingSessionRecordsExpectedMiss()
    {
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped = activity,
        };
        ActivitySource.AddActivityListener(listener);
        var client = new RedisAgentMemoryClient(new HttpClient(new StatusHandler(HttpStatusCode.NotFound)), new()
        {
            Endpoint = new("https://memory.example.test"),
            StoreId = "store",
        }, new FixedAgentMemoryCredentialProvider("token"));

        var result = await client.TryGetSessionMemoryAsync("new-session");

        Assert.Null(result);
        Assert.NotNull(stopped);
        Assert.Equal(ActivityStatusCode.Unset, stopped.Status);
        Assert.Equal("not_found", stopped.GetTagItem("redis.iris.outcome"));
        Assert.Equal(404, stopped.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task SearchSpanIsParentedAndContainsOnlyApprovedValues()
    {
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == AgentMemoryDiagnostics.ActivitySourceName)
                {
                    stopped = activity;
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var parentSource = new ActivitySource("test", "1.0");
        using var parentListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(parentListener);
        using var parent = parentSource.StartActivity("agent.invoke")!;
        var handler = new StubHandler("{\"items\":[]}");
        var client = new RedisAgentMemoryClient(new HttpClient(handler), new()
        {
            Endpoint = new("https://memory.example.test"),
            StoreId = "secret-store",
        }, new FixedAgentMemoryCredentialProvider("secret-token"));

        await client.SearchLongTermMemoryAsync(new() { Text = "sensitive query", Filter = new() { OwnerId = new() { Eq = "private-owner" } } });

        Assert.NotNull(stopped);
        Assert.Equal(parent.TraceId, stopped.TraceId);
        Assert.Equal(parent.SpanId, stopped.ParentSpanId);
        Assert.Equal("ltm.search", stopped.GetTagItem("redis.iris.operation"));
        Assert.Equal("memory.example.test", stopped.GetTagItem("server.address"));
        var tags = string.Join(" ", stopped.TagObjects.Select(static pair => $"{pair.Key}={pair.Value}"));
        Assert.DoesNotContain("sensitive query", tags);
        Assert.DoesNotContain("private-owner", tags);
        Assert.DoesNotContain("secret-store", tags);
        Assert.DoesNotContain("secret-token", tags);
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
