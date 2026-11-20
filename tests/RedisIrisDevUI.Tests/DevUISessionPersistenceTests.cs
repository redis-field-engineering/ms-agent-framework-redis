// Copyright (c) Redis, Inc.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RedisIrisDevUI.Tests;

public sealed class DevUISessionPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponsesRestoreSnapshotsWithoutReplayingHistoryAndIsolateCallers(bool stream)
    {
        await using var host = await TestHost.StartAsync();
        var conversation = await host.CreateConversationAsync();
        var first = await host.RunAsync(conversation, stream);
        Assert.Contains("turn=1;inputs=1", first);
        Assert.Equal(1, host.Store.Saves);
        var firstSession = host.Agent.Sessions.Single();
        var identity = firstSession.StateBag.GetValue<string>("ram-session");

        var second = await host.RunAsync(conversation, stream);
        Assert.Contains("turn=2;inputs=1", second);
        Assert.Contains(identity!, second);
        Assert.NotSame(firstSession, host.Agent.Sessions.Last());
        Assert.Equal(2, host.Store.Saves);
        Assert.Contains(stream ? "response.completed" : "\"completed\"", second);

        // The UI's response and transcript resources still work with the standard endpoints.
        using var responseDocument = JsonDocument.Parse(stream
            ? second.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : second);
        var responseId = (stream ? responseDocument.RootElement.GetProperty("response") : responseDocument.RootElement)
            .GetProperty("id").GetString();
        var retrieved = await host.Http.GetFromJsonAsync<JsonElement>($"/v1/responses/{responseId}");
        Assert.Equal("completed", retrieved.GetProperty("status").GetString());
        var items = await host.Http.GetStringAsync($"/v1/conversations/{conversation}/items");
        Assert.Contains("turn=1;inputs=1", items);
        Assert.Contains("turn=2;inputs=1", items);
        host.Http.DefaultRequestHeaders.Remove("X-Test-User");
        host.Http.DefaultRequestHeaders.Add("X-Test-User", "bob");
        var bob = await host.RunAsync(conversation, stream);
        Assert.Contains("turn=1;inputs=1", bob);
        Assert.DoesNotContain(identity!, bob);
        host.Http.DefaultRequestHeaders.Remove("X-Test-User");
        host.Http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        Assert.Contains("turn=3;inputs=1", await host.RunAsync(conversation, stream));
        Assert.Contains("turn=1;inputs=1", await host.RunAsync(await host.CreateConversationAsync(), stream));
        Assert.Equal(5, host.Store.Reads);
        Assert.Equal(5, host.Store.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreAndAgentFailuresDoNotReportCompletionOrCommitPartialState(bool stream)
    {
        await using var host = await TestHost.StartAsync();
        var conversation = await host.CreateConversationAsync();
        await host.RunAsync(conversation, stream);
        host.Store.FailRead = true;
        AssertFailed(await host.RunAsync(conversation, stream, expectFailure: true), stream);
        Assert.Single(host.Agent.Sessions);
        host.Store.FailRead = false;
        AssertFailed(await host.RunAsync(conversation, stream, "fail", expectFailure: true), stream);
        Assert.Equal(1, host.Store.Saves);
        host.Store.FailSave = true;
        AssertFailed(await host.RunAsync(conversation, stream, expectFailure: true), stream);
        Assert.Equal(1, host.Store.Saves);
        host.Store.FailSave = false;
        Assert.Contains("turn=2;inputs=1", await host.RunAsync(conversation, stream));
        Assert.Equal(2, host.Store.Saves);
    }

    [Fact]
    public async Task DisconnectedStreamCancelsAgentWithoutSavingPartialSession()
    {
        await using var host = await TestHost.StartAsync();
        var conversation = await host.CreateConversationAsync();
        await host.RunAsync(conversation, false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = host.Http.PostAsJsonAsync("/v1/responses", Body(conversation, true, "wait"), cancellation.Token);
        await host.Agent.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await host.Agent.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, host.Store.Saves);
        Assert.Contains("turn=2;inputs=1", await host.RunAsync(conversation, false));
    }

    [Theory]
    [InlineData("{\"input\":\"hello\"}")]
    [InlineData("{\"input\":\"hello\",\"previous_response_id\":\"resp_old\"}")]
    [InlineData("{\"input\":\"hello\",\"conversation\":\"conv_1\",\"background\":true}")]
    [InlineData("{\"input\":\"hello\",\"conversation\":\"conv_1\",\"previous_response_id\":\"resp_old\"}")]
    [InlineData("{")]
    public async Task InvalidOrUnsupportedRequestsDoNotRunAnAgent(string body)
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Http.PostAsync("/v1/responses", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Agent.Sessions);
        Assert.Equal(0, host.Store.Reads);
    }

    private static object Body(string conversation, bool stream, string input) => new
    {
        metadata = new { entity_id = "test-agent" },
        input,
        conversation,
        stream,
    };

    private static void AssertFailed(string body, bool stream)
    {
        Assert.Contains(stream ? "response.failed" : "execution_error", body);
        Assert.DoesNotContain(stream ? "response.completed" : "\"status\":\"completed\"", body);
    }

    private sealed class TestHost(WebApplication app, HttpClient http, StatefulAgent agent, RecordingStore store) : IAsyncDisposable
    {
        public HttpClient Http { get; } = http;
        public StatefulAgent Agent { get; } = agent;
        public RecordingStore Store { get; } = store;

        public static async Task<TestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<AgentIsolationKeyProvider, TestIsolation>();
            var agent = new StatefulAgent();
            var store = new RecordingStore();
            builder.AddAIAgent("test-agent", (services, name) => new DevUISessionAgent(agent,
                services.GetRequiredKeyedService<AgentSessionStore>(name), services.GetRequiredService<IHttpContextAccessor>()))
                .WithSessionStore(store);
            builder.Services.AddOpenAIResponses();
            builder.Services.AddOpenAIConversations();
            var app = builder.Build();
            app.UseDevUISessionPersistence();
            app.MapOpenAIResponses();
            app.MapOpenAIConversations();
            await app.StartAsync();
            var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            http.DefaultRequestHeaders.Add("X-Test-User", "alice");
            return new(app, http, agent, store);
        }

        public async Task<string> CreateConversationAsync()
        {
            using var response = await Http.PostAsJsonAsync("/v1/conversations", new { metadata = new { agent_id = "test-agent" } });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        }

        public async Task<string> RunAsync(string conversation, bool stream, string input = "hello", bool expectFailure = false)
        {
            using var response = await Http.PostAsJsonAsync("/v1/responses", Body(conversation, stream, input));
            Assert.Equal(expectFailure && !stream ? HttpStatusCode.InternalServerError : HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class TestIsolation(IHttpContextAccessor contexts) : AgentIsolationKeyProvider
    {
        public override ValueTask<string?> GetIsolationKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(contexts.HttpContext!.Request.Headers["X-Test-User"].ToString());
    }

    private sealed class RecordingStore() : DelegatingAgentSessionStore(new InMemoryAgentSessionStore())
    {
        public int Reads;
        public int Saves;
        public bool FailRead;
        public bool FailSave;
        public override async ValueTask<AgentSession> GetSessionAsync(AIAgent agent, string sessionStoreId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            if (FailRead) throw new InvalidOperationException("Simulated read failure");
            return await base.GetSessionAsync(agent, sessionStoreId, cancellationToken);
        }
        public override async ValueTask SaveSessionAsync(AIAgent agent, string sessionStoreId, AgentSession session, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new InvalidOperationException("Simulated save failure");
            await base.SaveSessionAsync(agent, sessionStoreId, session, cancellationToken);
            Interlocked.Increment(ref Saves);
        }
    }

    private sealed record TurnState(int Count);

    private sealed class TestSession(AgentSessionStateBag state) : AgentSession(state);

    private sealed class StatefulAgent : AIAgent
    {
        public override string Name => "test-agent";
        protected override string IdCore => Name;
        public ConcurrentQueue<AgentSession> Sessions { get; } = new();
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new TestSession(new()));
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => new(JsonSerializer.SerializeToElement(session.StateBag));
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => new(new TestSession(serializedState.Deserialize<AgentSessionStateBag>()!));
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.NotNull(session);
            Sessions.Enqueue(session);
            var identity = session.StateBag.GetValue<string>("ram-session") ?? Guid.NewGuid().ToString();
            var count = (session.StateBag.GetValue<TurnState>("turn")?.Count ?? 0) + 1;
            session.StateBag.SetValue("ram-session", identity);
            session.StateBag.SetValue("turn", new TurnState(count));
            var input = messages.ToArray();
            if (input.Last().Text == "fail") throw new InvalidOperationException("Simulated agent failure");
            if (input.Last().Text == "wait")
            {
                Waiting.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                finally { Cancelled.TrySetResult(); }
            }
            yield return new AgentResponseUpdate(ChatRole.Assistant, $"{identity};turn={count};inputs={input.Length}");
        }
    }
}
