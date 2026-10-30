// Notebook-only synthetic transport and provider harness. Never shipped in a NuGet package.
// It deliberately implements only the read/filter subset used by notebooks 06–08.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;
using Microsoft.Extensions.AI;
using Redis.Iris.AgentMemory;

public static class MemoryLab
{
    public const string SeatingQuestion = "What seat should I choose?";
    public const string HotelQuestion = "What hotel should I choose?";
    public static readonly AIAgent Agent = new InspectionAgent();
    public static void Check(bool condition, string explanation)
    {
        if (!condition) throw new InvalidOperationException(explanation);
    }
    public static ChatMessage Question(string text) => new(ChatRole.User, text);
    public static void Show(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages) Console.WriteLine($"[{message.Role}] {message.Text}");
    }
    public static RedisIrisProviderState State(string owner = "avery") => new($"session-{owner}", owner, "memory-lab", owner);
    public static RedisAgentMemoryClient Client(HttpClient http, IAgentMemoryDelegatedTokenProvider? token = null) =>
        new(http, new() { Endpoint = new Uri("https://notebook.invalid/"), StoreId = "synthetic-store" },
            new FixedAgentMemoryCredentialProvider("synthetic-notebook-key"), token);

    // Providers require an agent/session context. These hooks inspect context without making a model call.
    private sealed class InspectionSession : AgentSession { }
    private sealed class InspectionAgent : AIAgent
    {
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new InspectionSession());
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException("This harness never calls a model.");
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new NotSupportedException("This harness never calls a model.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}

public sealed class MemoryLabTransport : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public HttpStatusCode? FailureStatus { get; set; }
    public bool MissingHistory { get; set; }
    public bool BlockUntilCancelled { get; set; }
    public int Sends { get; private set; }
    public LongTermMemorySearchRequest? LastSearch { get; private set; }
    public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Scores are fixed teaching inputs, NOT Redis/embedding output or quality measurements.
    public static readonly (LongTermMemoryRecord Memory, double SeatScore, double HotelScore)[] Records =
    {
        (new("avery-seat", "Avery prefers a window seat and morning departures.", At, At, "preference", "session-avery", "avery", Topics: new[] { "travel", "flights" }), .95, .35),
        (new("avery-meal", "Avery requests vegetarian meals on flights.", At, At, "preference", "session-avery", "avery", Topics: new[] { "travel", "flights" }), .73, .30),
        (new("avery-hotel", "Avery prefers quiet hotels near a train station.", At, At, "preference", "session-avery", "avery", Topics: new[] { "travel", "hotels" }), .40, .94),
        (new("avery-old-trip", "Avery took a trip to Boston last year; this is not a current booking.", At, At, "fact", "session-avery", "avery", Topics: new[] { "travel" }), .62, .66),
        (new("riley-seat", "Riley prefers an aisle seat and evening departures.", At, At, "preference", "session-riley", "riley", Topics: new[] { "travel", "flights" }), .96, .35),
        (new("riley-hotel", "Riley prefers a hotel with a pool.", At, At, "preference", "session-riley", "riley", Topics: new[] { "travel", "hotels" }), .35, .96),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "notebook.invalid") throw new InvalidOperationException("Fixture accepts only notebook.invalid.");
        Sends++;
        Entered.TrySetResult(true);
        if (BlockUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailureStatus is { } status) return new(status) { Content = JsonContent.Create(new { error = "Synthetic outage" }) };
        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Get && path.Contains("/session-memory/"))
        {
            var id = Uri.UnescapeDataString(path.Split('/').Last());
            if (MissingHistory || id is not ("session-avery" or "session-riley")) return new(HttpStatusCode.NotFound);
            var owner = id == "session-avery" ? "avery" : "riley";
            var texts = new[] { $"I am {owner} and I am planning a trip.", "Where would you like to go?", "Boston, next month.", "What time of day?", "Morning, please.", "I will use that preference." };
            var events = texts.Select((text, index) => new SessionEvent($"event-{index}", owner, id,
                index % 2 == 0 ? SessionEventRole.USER : SessionEventRole.ASSISTANT,
                new[] { new SessionEventContent(text) }, At.AddMinutes(index), At.AddMinutes(index))).ToArray();
            var summary = new SessionSummary($"{owner} discussed a rail connection, a quiet hotel, and vegetarian meals before the recent conversation.",
                "earlier-event", At, At, JsonSerializer.SerializeToElement(new { }), 4);
            return Ok(new SessionMemory(id, owner, events, Summary: summary));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/long-term-memory/search", StringComparison.Ordinal))
        {
            var query = await request.Content!.ReadFromJsonAsync<LongTermMemorySearchRequest>(Json, cancellationToken)
                ?? throw new InvalidOperationException("Missing fixture query.");
            LastSearch = query;
            var filter = query.Filter;
            if (query.FilterOp != "all" || query.PageToken is not null || filter?.Namespace is not null ||
                filter?.NamespaceRef is not null || filter?.Unnamespaced is not null || filter?.CreatedAt is not null)
                throw new NotSupportedException("The fixture supports conjunctions of owner, session, topic and memory-type filters only, without pagination.");
            var results = Records
                .Where(row => Matches(filter?.OwnerId, new[] { row.Memory.OwnerId! }) &&
                    Matches(filter?.SessionId, new[] { row.Memory.SessionId! }) &&
                    Matches(filter?.Topics, row.Memory.Topics!) && Matches(filter?.MemoryType, new[] { row.Memory.MemoryType! }))
                .Select(row => (row.Memory, Score: query.Text == MemoryLab.SeatingQuestion ? row.SeatScore : query.Text == MemoryLab.HotelQuestion ? row.HotelScore : .10))
                .Where(row => row.Score >= (query.SimilarityThreshold ?? 0))
                .OrderByDescending(row => row.Score).ThenBy(row => row.Memory.Id, StringComparer.Ordinal)
                .Take(query.Limit).Select(row => row.Memory).ToArray();
            return Ok(new LongTermMemorySearchResult(results));
        }
        throw new NotSupportedException("This notebook fixture does not implement that request.");
    }
    private static bool Matches(StringFilter? filter, IReadOnlyList<string> values) => filter is null ||
        (filter.Eq is null || values.Contains(filter.Eq)) && (filter.Ne is null || !values.Contains(filter.Ne)) &&
        (filter.In is null || filter.In.Any(values.Contains)) && (filter.All is null || filter.All.All(values.Contains));
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: Json) };
}

public sealed class MemoryLabTelemetry : IDisposable
{
    public sealed record Span(string Name, ActivityStatusCode Status, IReadOnlyDictionary<string, object?> Tags);
    public sealed record Measurement(string Name, double Value, IReadOnlyDictionary<string, object?> Tags);
    public ConcurrentQueue<Span> Spans { get; } = new();
    public ConcurrentQueue<Measurement> Measurements { get; } = new();
    private readonly ActivityListener activities;
    private readonly MeterListener metrics;
    public MemoryLabTelemetry()
    {
        activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentMemoryDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => Spans.Enqueue(new(activity.OperationName, activity.Status,
                activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value))),
        };
        ActivitySource.AddActivityListener(activities);
        metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentMemoryDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
            },
        };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument.Name, value, tags));
        metrics.Start();
    }
    private void Record(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        Measurements.Enqueue(new(name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
    public void Dispose() { metrics.Dispose(); activities.Dispose(); }
}
