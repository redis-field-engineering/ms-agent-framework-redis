using System.Text.Json;
using Redis.Iris.AgentFramework;

namespace Redis.Iris.AgentMemory.LiveTests;

// Uses the dedicated dotnet_attribute_test type created by the opt-in management live test.
[Trait("Category", "Live")]
public sealed class IrisAttributeSearchLiveTests
{
    [AttributeSearchFact]
    public async Task TypedAttributesFilterBeforePaginationAndRetainOwnerScope()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var client = new RedisAgentMemoryClient(http, new()
        {
            Endpoint = new(Environment.GetEnvironmentVariable("REDIS_IRIS_ENDPOINT")!),
            StoreId = Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID")!,
            ApiMode = AgentMemoryApiMode.Iris,
        }, new FixedAgentMemoryCredentialProvider(Environment.GetEnvironmentVariable("REDIS_IRIS_API_KEY")!));
        var owner = "attr-" + Guid.NewGuid().ToString("N");
        var ids = Enumerable.Range(0, 4).Select(_ => "attr-" + Guid.NewGuid().ToString("N")).ToArray();
        var options = new RedisIrisMemorySearchOptions
        {
            MemoryType = new() { Eq = "dotnet_attribute_test" },
            Limit = 1,
            Attributes = new Dictionary<string, AttributeFilterClause>
            {
                ["status"] = AttributeFilterClause.StringEquals("open"),
                ["priority"] = AttributeFilterClause.NumberEquals(0),
                ["approved"] = AttributeFilterClause.BooleanEquals(false),
                ["tags"] = AttributeFilterClause.ContainsAll("synthetic", "travel"),
            },
        };
        try
        {
            var created = await client.BulkCreateLongTermMemoriesAsync(new()
            {
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Memories = ids.Select((id, index) => new CreateLongTermMemoryRequest
                {
                    Id = id,
                    OwnerId = index == 3 ? owner + "-other" : owner,
                    MemoryType = "dotnet_attribute_test",
                    Text = "Synthetic traveler plans a trip to Tokyo.",
                    Attributes = new Dictionary<string, JsonElement>
                    {
                        ["status"] = JsonSerializer.SerializeToElement(index == 2 ? "closed" : "open"),
                        ["priority"] = JsonSerializer.SerializeToElement(0),
                        ["approved"] = JsonSerializer.SerializeToElement(false),
                        ["tags"] = JsonSerializer.SerializeToElement(new[] { "synthetic", "travel" }),
                    },
                }).ToArray(),
            }, ct);
            Assert.Equal(4, created.Created.Count);
            Assert.True(created.Errors is null or { Count: 0 });
            var search = new RedisIrisLongTermMemorySearch(client);
            var found = new HashSet<string>();
            string? cursor = null;
            var cursors = new HashSet<string>();
            do
            {
                var page = await search.SearchAsync(owner, (string?)null, options with { PageToken = cursor }, ct);
                foreach (var item in page.Items)
                {
                    Assert.Equal(owner, item.OwnerId);
                    Assert.Equal("open", item.Attributes!["status"].GetString());
                    Assert.True(found.Add(item.Id));
                }
                cursor = page.NextPageToken;
                if (cursor is not null) Assert.True(cursors.Add(cursor), "Cursor must progress.");
            } while (cursor is not null);
            Assert.Equal(ids.Take(2).Order(), found.Order());
            var semantic = await search.SearchAsync(owner, "Tokyo trip", options with { Limit = 10 }, ct);
            Assert.NotEmpty(semantic.Items);
            Assert.All(semantic.Items, item => Assert.Contains(item.Id, ids.Take(2)));
            var missing = await search.SearchAsync(owner, (string?)null, options with
            { Attributes = new Dictionary<string, AttributeFilterClause> { ["status"] = AttributeFilterClause.StringEquals("absent") } }, ct);
            Assert.Empty(missing.Items);
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var cleanup = await client.BulkDeleteLongTermMemoriesAsync(ids, cleanupDeadline.Token);
            Assert.True(cleanup.Errors is null || cleanup.Errors.All(error => error.Status == 404), "Synthetic cleanup failed.");
        }
    }
}

internal sealed class AttributeSearchFactAttribute : FactAttribute
{
    public AttributeSearchFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDIS_IRIS_RUN_ATTRIBUTE_TESTS") != "1" ||
            new[] { "REDIS_IRIS_ENDPOINT", "REDIS_IRIS_STORE_ID", "REDIS_IRIS_API_KEY" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set REDIS_IRIS_RUN_ATTRIBUTE_TESTS=1 with a dedicated Iris store containing dotnet_attribute_test (see custom memory docs).";
    }
}
