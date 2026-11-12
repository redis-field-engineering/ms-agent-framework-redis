// Copyright (c) Redis, Inc.

using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Redis.Iris.AgentFramework;
using Redis.Iris.AgentMemory;

namespace RedisIrisDevUI;

internal static class MemoryEditorEndpoints
{
    public static void MapMemoryEditor(this RouteGroupBuilder endpoints, SampleOptions settings)
    {
        endpoints.MapGet("/memories", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(MemoryEditorPage.Render(antiforgery.GetAndStoreTokens(context).RequestToken!,
                settings.ApiMode == AgentMemoryApiMode.Iris), "text/html");
        });
        var api = endpoints.MapGroup("/api/ram/memory-editor");
        api.AddEndpointFilter(async (context, next) =>
        {
            try
            {
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                if (settings.ApiMode != AgentMemoryApiMode.Iris) return Results.Json(new { error = "Select Iris API mode to use the memory editor." }, statusCode: 501);
                if (context.HttpContext.Request.Method != "GET")
                    await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
                return await next(context);
            }
            catch (AntiforgeryValidationException) { return Error("Refresh the page before saving.", 400); }
            catch (AgentMemoryException error)
            {
                var status = (int)(error.StatusCode ?? System.Net.HttpStatusCode.BadGateway);
                return Error(status switch
                {
                    412 => "This memory changed. Reload it and reconcile your draft before saving.",
                    409 => "A record or request with this identity already exists. Reload before making a new change.",
                    404 => "Memory not found or unavailable.",
                    401 or 403 => "Memory access was denied. Check your sign-in.",
                    _ => "Iris did not complete this operation. Retry the same proposal if the outcome is uncertain.",
                }, status);
            }
            catch (KeyNotFoundException) { return Error("Proposal not found or expired.", 404); }
            catch (UnauthorizedAccessException) { return Error("Memory access was denied.", 403); }
            catch (ArgumentException) { return Error("Check the memory text, topics, and attributes.", 400); }
            catch (NotSupportedException) { return Error("This server or record does not support conditional memory editing.", 501); }
            catch (InvalidOperationException) { return Error("This memory is read-only or the proposal limit has been reached.", 409); }
            catch (HttpRequestException) { return Error("Connection interrupted. Retry the same proposal; the write may have committed.", 502); }
            catch (InvalidDataException) { return Error("Unexpected Iris response. The write may have committed; refresh before making a new change.", 502); }
        });
        api.MapGet("/memories", (string? query, string? cursor, RedisIrisLongTermMemoryWriter writer, CancellationToken token) =>
            writer.SearchAsync(query, cursor, cancellationToken: token));
        api.MapGet("/memories/{id}", (string id, RedisIrisLongTermMemoryWriter writer, CancellationToken token) => writer.GetAsync(id, cancellationToken: token));
        api.MapGet("/proposals", (MemoryProposalStore proposals, TravelUserAccessor users) => proposals.List(users.Current.IsolationKey));
        api.MapPost("/proposals", async (MemoryDraft draft, RedisIrisLongTermMemoryWriter writer, MemoryProposalStore proposals,
            TravelUserAccessor users, CancellationToken token) =>
        {
            var proposal = draft.MemoryId is null
                ? writer.PrepareCreate(draft.Text, draft.Topics, draft.MemoryType, draft.Attributes)
                : await writer.PrepareUpdateAsync(draft.MemoryId, draft.ExpectedRevision ?? "", draft.Text, draft.Topics, draft.Attributes, cancellationToken: token);
            proposals.Add(users.Current.IsolationKey, proposal, null);
            return Results.Ok(MemoryProposalStore.Describe(proposal));
        });
        api.MapPost("/proposals/{id}/save", async (string id, RedisIrisLongTermMemoryWriter writer, MemoryProposalStore proposals,
            TravelUserAccessor users, CancellationToken token) => Results.Ok(await proposals.SaveAsync(users.Current.IsolationKey, id, writer, token)));
    }

    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);
    internal sealed record MemoryDraft(string Text, string[]? Topics, string? MemoryId = null, string? ExpectedRevision = null,
        string? MemoryType = null, Dictionary<string, JsonElement>? Attributes = null);
}
