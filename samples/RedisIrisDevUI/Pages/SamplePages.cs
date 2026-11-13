// Copyright (c) Redis, Inc.

namespace RedisIrisDevUI;

internal static class SamplePages
{
    public static string Dashboard(
        TravelUser user,
        int shortTermWindow,
        bool multiUser,
        bool accessTokenInspectorEnabled) => $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Redis Iris + Microsoft Agent Framework</title></head>
        <body style="font-family:system-ui;max-width:850px;margin:3rem auto;padding:0 1rem;line-height:1.5">
          <h1>Redis Iris + Microsoft Agent Framework</h1>
          {{(multiUser ? "<form method=\"post\" action=\"/logout\"><button type=\"submit\">Sign out</button></form>" : "")}}
          <p>This local sample hosts a travel assistant in Microsoft's official DevUI, persists its memory in Redis Agent Memory, and defines structured travel context for Redis Context Retriever.</p>
          <dl>
            <dt><strong>Tenant</strong></dt><dd><code>{{System.Net.WebUtility.HtmlEncode(user.TenantId)}}</code></dd>
            <dt><strong>Traveler</strong></dt><dd><code>{{System.Net.WebUtility.HtmlEncode(user.TravelerId)}}</code></dd>
            <dt><strong>RAM owner scope</strong></dt><dd><code>{{System.Net.WebUtility.HtmlEncode(user.RamOwnerId)}}</code></dd>
            <dt><strong>Short-term window</strong></dt><dd>{{shortTermWindow}} recent messages</dd>
          </dl>
          <p><a href="/devui">Open the Agent Framework DevUI</a> · <a href="/memories">Create and edit memories</a></p>
          <p>When running with Docker Compose: <a href="http://localhost:16686" target="_blank">Jaeger traces</a> · <a href="http://localhost:3000/d/redis-iris-agent-memory" target="_blank">Grafana metrics</a> · <a href="http://localhost:9090" target="_blank">Prometheus</a></p>
          <h2>Demonstration</h2>
          <ol>
            <li>Tell the agent your name and travel preferences.</li>
            <li>Continue the same conversation to see the bounded short-term window.</li>
            <li>After RAM promotion runs, start a new DevUI conversation and ask what it remembers.</li>
          </ol>
          <h2>RAM inspection</h2>
          <p><a href="/api/ram">List this owner's sessions and long-term memories as JSON</a>.</p>
          <p>Inspect one session, including its summary and events, at <code>/api/ram/sessions/{sessionId}</code>.</p>
          <h2>Structured travel context</h2>
          <p>The sample defines annotated POCOs for travelers, flight reservations, hotel reservations, and travel receipts.</p>
          <ul>
            <li><a href="/api/context/model">View the generated Context Retriever DataModel</a></li>
            <li><a href="/api/context/examples">View representative structured records</a></li>
            <li><a href="/api/context/status">View the runtime MCP connection status</a></li>
          </ul>
          <p>The agent receives the surface's authorized MCP tools on every invocation. The model and example endpoints do not provision a surface or write records.</p>
          {{(accessTokenInspectorEnabled ? "<h2>JWT inspection</h2><p><a href=\"/debug/jwt\">View the exact delegated access token and decoded claims</a> or <a href=\"/debug/jwt/fresh\">sign in again and download a fresh token</a>. Treat the returned bearer token as a secret.</p>" : "")}}
          <h2>Cleanup</h2>
          <p>Delete both memory tiers with an explicit owner confirmation:</p>
          <pre>curl -X DELETE 'http://localhost:5080/api/ram?confirm={{Uri.EscapeDataString(user.RamOwnerId)}}'</pre>
          <p><strong>Development only:</strong> this sample intentionally refuses to run outside the Development environment.</p>
        </body>
        </html>
        """;

    public static string Login(string? error = null) => $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Redis Iris DevUI sign in</title></head>
        <body style="font-family:system-ui;max-width:520px;margin:5rem auto;padding:0 1rem;line-height:1.5">
          <h1>Redis Iris DevUI</h1>
          <p>Enter a configured development-user token. The token selects a server-owned tenant, traveler, RAM owner, session partition, and delegated Context Retriever identity.</p>
          {{(error is null ? "" : $"<p style=\"color:#b91c1c\">{System.Net.WebUtility.HtmlEncode(error)}</p>")}}
          <form method="post" action="/login">
            <label>Sign-in token<br><input name="token" type="password" autocomplete="current-password" required style="width:100%;padding:.6rem;margin:.4rem 0 1rem"></label>
            <button type="submit">Sign in</button>
          </form>
          <p><strong>Development only:</strong> production applications should replace this local token directory with their OIDC or JWT authentication scheme.</p>
        </body>
        </html>
        """;
}
