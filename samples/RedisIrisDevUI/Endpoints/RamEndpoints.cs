// Copyright (c) Redis, Inc.

using Redis.Iris.AgentMemory;

namespace RedisIrisDevUI;

internal static class RamEndpoints
{
    public static void MapRamEndpoints(this RouteGroupBuilder endpoints, int shortTermWindow)
    {
        endpoints.MapGet("/api/ram", async (
            IRedisAgentMemoryClient client,
            TravelUserAccessor users,
            CancellationToken cancellationToken) =>
        {
            var user = users.Current;
            var sessions = await ListOwnerSessionsAsync(client, user.RamOwnerId, cancellationToken);
            var memories = await ListOwnerMemoriesAsync(client, user.RamOwnerId, cancellationToken);
            return Results.Ok(new
            {
                tenantId = user.TenantId,
                ownerId = user.RamOwnerId,
                shortTermWindow,
                sessions,
                longTermMemories = memories,
                note = "Session IDs are generated from trusted application state, not model input.",
            });
        });

        endpoints.MapGet("/api/ram/sessions/{sessionId}", async (
            string sessionId,
            IRedisAgentMemoryClient client,
            TravelUserAccessor users,
            CancellationToken cancellationToken) =>
        {
            var user = users.Current;
            try
            {
                var session = await client.GetSessionMemoryAsync(sessionId, includeSummarizedEvents: true, cancellationToken);
                return !string.Equals(session.OwnerId, user.RamOwnerId, StringComparison.Ordinal)
                    ? Results.Forbid()
                    : Results.Ok(session);
            }
            catch (AgentMemoryException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Results.NotFound();
            }
        });

        endpoints.MapDelete("/api/ram", async (
            string? confirm,
            IRedisAgentMemoryClient client,
            TravelUserAccessor users,
            CancellationToken cancellationToken) =>
        {
            var user = users.Current;
            var ownerId = user.RamOwnerId;
            if (!string.Equals(confirm, ownerId, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = $"Set confirm={ownerId} to clear this owner's RAM data." });
            }

            var sessionIds = await ListOwnerSessionsAsync(client, ownerId, cancellationToken);
            foreach (var sessionId in sessionIds)
            {
                await client.DeleteSessionMemoryAsync(sessionId, cancellationToken);
            }

            var memories = await ListOwnerMemoriesAsync(client, ownerId, cancellationToken);
            foreach (var batch in memories.Select(static memory => memory.Id).Chunk(100))
            {
                var result = await client.BulkDeleteLongTermMemoriesAsync(batch, cancellationToken);
                if (result.Errors is { Count: > 0 })
                {
                    return Results.Problem($"RAM failed to delete {result.Errors.Count} long-term memory record(s).");
                }
            }

            return Results.Ok(new { ownerId, sessionsDeleted = sessionIds.Count, longTermMemoriesDeleted = memories.Count });
        });
    }

    private static async Task<List<string>> ListOwnerSessionsAsync(
        IRedisAgentMemoryClient client,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var items = new List<string>();
        string? pageToken = null;
        do
        {
            var page = await client.ListSessionsAsync(ownerId, limit: 1000, pageToken, cancellationToken);
            items.AddRange(page.Items);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return items;
    }

    private static async Task<List<LongTermMemoryRecord>> ListOwnerMemoriesAsync(
        IRedisAgentMemoryClient client,
        string ownerId,
        CancellationToken cancellationToken)
    {
        var items = new List<LongTermMemoryRecord>();
        string? pageToken = null;
        do
        {
            var page = await client.SearchLongTermMemoryAsync(new()
            {
                Filter = new() { OwnerId = new() { Eq = ownerId } },
                Limit = 100,
                PageToken = pageToken,
            }, cancellationToken);
            items.AddRange(page.Items);
            pageToken = page.NextPageToken;
        }
        while (pageToken is not null);

        return items;
    }
}
