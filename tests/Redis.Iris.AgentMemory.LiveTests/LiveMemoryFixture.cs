using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Redis.Iris.AgentMemory.LiveTests;

// Synthetic records only. The REST fixture is independent of the connector search implementation.
internal sealed class LiveMemoryFixture : IAsyncDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly LiveConfiguration _configuration = LiveConfiguration.Load();
    private readonly HashSet<string> _ids = [];
    private readonly string? _token;
    public RedisAgentMemoryClient Client { get; }

    public LiveMemoryFixture(string? token = null, DateTimeOffset? expiresAt = null)
    {
        _token = token;
        Client = new(_http, new() { Endpoint = _configuration.Endpoint, StoreId = _configuration.StoreId },
            new FixedAgentMemoryCredentialProvider(_configuration.ApiKey), token is null ? null :
                new FixedAgentMemoryDelegatedTokenProvider(new(token, expiresAt!.Value)));
    }

    public async Task<string> SeedAsync(string owner, string text, string topic)
    {
        var id = "live-memory-" + Guid.NewGuid().ToString("N");
        _ids.Add(id); // Track even an ambiguous or partially successful response.
        var uri = new Uri(new Uri(_configuration.Endpoint.AbsoluteUri.TrimEnd('/') + "/"),
            $"v1/stores/{Uri.EscapeDataString(_configuration.StoreId)}/long-term-memory");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiKey);
        if (_token is not null) request.Headers.Add("X-Iris-User-Token", _token);
        request.Content = JsonContent.Create(new { memories = new[] { new { id, ownerId = owner, text, memoryType = "semantic", topics = new[] { topic } } } });
        using var response = await _http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Synthetic LTM creation returned HTTP {(int)response.StatusCode}.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Contains(id, json.RootElement.GetProperty("created").EnumerateArray().Select(item => item.GetString()));
        if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array) Assert.Equal(0, errors.GetArrayLength());
        return id;
    }

    public static async Task<LongTermMemorySearchResult> WaitForAsync(IRedisAgentMemoryClient client,
        LongTermMemorySearchRequest request, Func<LongTermMemorySearchResult, bool> predicate, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var result = await client.SearchLongTermMemoryAsync(request, deadline.Token);
                if (predicate(result)) return result;
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Expected populated live memory results were not observed before the deadline.");
        }
    }

    public void Track(IEnumerable<string> ids) => _ids.UnionWith(ids);

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var batch in _ids.Chunk(100))
            {
                var result = await Client.BulkDeleteLongTermMemoriesAsync(batch);
                // A failed seed may never have created an ID. Missing records need no cleanup.
                Assert.True(result.Errors is null || result.Errors.All(error => error.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)),
                    "Synthetic memory cleanup reported errors; inspect the dedicated test store.");
            }
        }
        finally { _http.Dispose(); }
    }
}
