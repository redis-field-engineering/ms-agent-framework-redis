using System.Net;
using System.Text.Json;
using Redis.Iris.AgentMemory;

namespace Redis.Iris.AgentMemory.LiveTests;

// Opt-in independently of the legacy live suite. Use only a dedicated current-Iris test store.
public sealed class IrisDirectMemoryLiveTests
{
    [IrisMutationFact]
    public async Task CreateReadCorrectReplaceAndReplayWithRevisionProtection()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        var client = new RedisAgentMemoryClient(http, new()
        {
            Endpoint = new(Environment.GetEnvironmentVariable("REDIS_IRIS_ENDPOINT")!),
            StoreId = Environment.GetEnvironmentVariable("REDIS_IRIS_STORE_ID")!,
            ApiMode = AgentMemoryApiMode.Iris,
        }, new FixedAgentMemoryCredentialProvider(Environment.GetEnvironmentVariable("REDIS_IRIS_API_KEY")!));
        var id = "direct-" + Guid.NewGuid().ToString("N");
        var missing = "missing-" + Guid.NewGuid().ToString("N");
        var owner = "test-" + Guid.NewGuid().ToString("N");
        var session = "test-" + Guid.NewGuid().ToString("N");
        try
        {
            var create = new BulkCreateLongTermMemoriesRequest
            {
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Memories = [new() { Id = id, Text = "The synthetic traveler prefers a window seat.", OwnerId = owner, SessionId = session, Topics = ["synthetic-test"] }],
            };
            Assert.Contains(id, (await client.BulkCreateLongTermMemoriesAsync(create)).Created);
            Assert.Contains(id, (await client.BulkCreateLongTermMemoriesAsync(create)).Created);
            var duplicate = await client.BulkCreateLongTermMemoriesAsync(create with { IdempotencyKey = Guid.NewGuid().ToString("N") });
            Assert.Empty(duplicate.Created); Assert.NotEmpty(duplicate.Errors!);
            var before = await client.GetLongTermMemoryAsync(id);
            Assert.False(string.IsNullOrEmpty(before.Revision));
            var patch = new UpdateLongTermMemoryRequest
            {
                Text = "The synthetic traveler now prefers an aisle seat.",
                SessionId = before.SessionId,
                ExpectedRevision = before.Revision,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
            };
            var updated = await client.UpdateLongTermMemoryAsync(id, patch);
            Assert.NotEqual(before.Revision, updated.Revision);
            Assert.Equal(session, updated.SessionId);
            Assert.Equal(updated.Revision, (await client.UpdateLongTermMemoryAsync(id, patch)).Revision);
            var conflict = await Assert.ThrowsAsync<AgentMemoryException>(() => client.UpdateLongTermMemoryAsync(id,
                patch with { IdempotencyKey = Guid.NewGuid().ToString("N"), Text = "stale" }));
            Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
            var replacement = new UpsertLongTermMemoryRequest
            {
                Text = updated.Text,
                OwnerId = owner,
                MemoryType = updated.MemoryType,
                SessionId = session,
                Topics = updated.Topics,
                ExpectedRevision = updated.Revision,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                Attributes = new Dictionary<string, JsonElement>(),
            };
            Assert.False((await client.UpsertLongTermMemoryAsync(id, replacement)).Created);
            var replaced = await client.GetLongTermMemoryAsync(id);
            Assert.Equal(before.CreatedAt, replaced.CreatedAt);
            Assert.Equal(session, replaced.SessionId);
            Assert.False((await client.UpsertLongTermMemoryAsync(id, replacement)).Created);
            var missingError = await Assert.ThrowsAsync<AgentMemoryException>(() => client.UpsertLongTermMemoryAsync(missing,
                replacement with { IdempotencyKey = Guid.NewGuid().ToString("N") }));
            Assert.Contains(missingError.StatusCode, new HttpStatusCode?[] { HttpStatusCode.NotFound, HttpStatusCode.PreconditionFailed });
            var search = await client.SearchLongTermMemoryAsync(new() { Filter = new() { OwnerId = new() { Eq = owner } } });
            Assert.Contains(search.Items, memory => memory.Id == id && memory.Text.Contains("aisle", StringComparison.Ordinal));
        }
        finally
        {
            // IDs are tracked before writes, including an unexpected create by conditional PUT.
            var cleanup = await client.BulkDeleteLongTermMemoriesAsync([id, missing]);
            Assert.True(cleanup.Errors is null || cleanup.Errors.All(error => error.Status == 404), "Synthetic memory cleanup failed.");
        }
    }
}

internal sealed class IrisMutationFactAttribute : FactAttribute
{
    public IrisMutationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REDIS_IRIS_RUN_DIRECT_MEMORY_TESTS") != "1" ||
            new[] { "REDIS_IRIS_ENDPOINT", "REDIS_IRIS_STORE_ID", "REDIS_IRIS_API_KEY" }.Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "Set REDIS_IRIS_RUN_DIRECT_MEMORY_TESTS=1 and current-Iris connection values for a dedicated test store.";
    }
}
