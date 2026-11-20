// Copyright (c) Redis, Inc.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.Extensions.AI;

namespace RedisIrisDevUI;

internal static class DevUISessionPersistence
{
    private static readonly object RequestKey = new();

    // The pinned hosting executor supplies no AgentSession and prepends its own UI
    // transcript. Capture the current turn before it does so; RAM owns agent history.
    public static void UseDevUISessionPersistence(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsPost(context.Request.Method) ||
                !string.Equals(context.Request.Path.Value?.TrimEnd('/'), "/v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            context.Request.EnableBuffering();
            try
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var body = document.RootElement;
                var request = OpenAIResponses.ToAgentRunRequest(body);
                if (string.IsNullOrWhiteSpace(request.ConversationId) || request.PreviousResponseId is not null)
                {
                    throw new ArgumentException("DevUI requires a conversation ID. Create a conversation using /v1/conversations; previous_response_id is not supported by this sample.");
                }
                if (body.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True)
                {
                    throw new ArgumentException("Background runs are not supported by this sample; session persistence uses the active HTTP caller.");
                }
                context.Items[RequestKey] = request;
            }
            catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = new { code = "invalid_request", message = error.Message } }, context.RequestAborted);
                return;
            }
            finally
            {
                context.Request.Body.Position = 0;
            }

            await next(context);
        });
    }

    internal static OpenAIResponsesRunRequest GetRequest(HttpContext context) =>
        context.Items.TryGetValue(RequestKey, out var request) && request is OpenAIResponsesRunRequest parsed
            ? parsed
            : throw new InvalidOperationException("Configure UseDevUISessionPersistence before the Responses endpoints.");
}

internal sealed class DevUISessionAgent(
    AIAgent innerAgent,
    AgentSessionStore store,
    IHttpContextAccessor httpContextAccessor) : DelegatingAIAgent(innerAgent)
{
    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (session is not null)
        {
            return await InnerAgent.RunAsync(messages, session, options, cancellationToken);
        }

        var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("A DevUI request or explicit session is required.");
        var request = DevUISessionPersistence.GetRequest(context);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        session = await store.GetSessionAsync(InnerAgent, request.ConversationId!, cancellation.Token);
        var response = await InnerAgent.RunAsync(request.Messages, session, options, cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        await store.SaveSessionAsync(InnerAgent, request.ConversationId!, session, cancellation.Token);
        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (session is not null)
        {
            await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken)) yield return update;
            yield break;
        }

        var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("A DevUI request or explicit session is required.");
        var request = DevUISessionPersistence.GetRequest(context);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.RequestAborted);
        session = await store.GetSessionAsync(InnerAgent, request.ConversationId!, cancellation.Token);
        await foreach (var update in InnerAgent.RunStreamingAsync(request.Messages, session, options, cancellation.Token)) yield return update;

        // Only a successfully exhausted run is checkpointed. This completes before
        // the hosting converter emits response.completed, including non-streaming HTTP.
        cancellation.Token.ThrowIfCancellationRequested();
        await store.SaveSessionAsync(InnerAgent, request.ConversationId!, session, cancellation.Token);
    }
}
