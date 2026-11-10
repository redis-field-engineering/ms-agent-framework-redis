// Copyright (c) Redis, Inc.

using Redis.Iris.ContextRetriever;
using Redis.Iris.ContextRetriever.Modeling;

namespace RedisIrisDevUI;

internal static class ContextEndpoints
{
    public static void MapContextEndpoints(this RouteGroupBuilder endpoints)
    {
        endpoints.MapGet("/api/context/model", (ContextDataModel contextModel) =>
            Results.Text(ContextDataModelJson.Serialize(contextModel), "application/json"));

        endpoints.MapGet("/api/context/examples", (TravelUserAccessor users) =>
            Results.Json(TravelContext.ExamplesFor(users.Current), ContextExports.JsonOptions));

        endpoints.MapGet("/api/context/status", (ContextRetrieverClientCache cache) =>
        {
            var health = cache.GetHealth();
            return Results.Ok(new
            {
                configured = true,
                health.ActiveClients,
                health.IsDisposed,
                note = health.ActiveClients == 0
                    ? "The MCP connection is initialized lazily on the first agent invocation."
                    : "The DevUI has an active authorized Context Retriever MCP session.",
            });
        });
    }
}
