# Standalone .NET quickstart

Use .NET 10 and an existing Redis Agent Memory store or Context Retriever surface. This library is maintained by Redis Field Engineering; the `Redis.Iris.AgentFramework` package name denotes Agent Framework integration, not Microsoft ownership or support. These are preview APIs. See [the operational contract](operational-contract.md) before using customer data.

Service provisioning is separate from these packages. Use the public [Redis Iris documentation](https://redis.io/docs/latest/develop/ai/context-engine/) for deployment options and the [RAM REST quickstart](https://redis.io/docs/latest/develop/ai/context-engine/agent-memory/rest-api-quickstart/) for creating a development memory service. The internal Kubernetes example is optional; no access to Redis Field Engineering infrastructure is required.

## Install

After the preview is published:

```bash
dotnet new console -n IrisDemo -f net10.0
cd IrisDemo
dotnet add package Redis.Iris.AgentFramework --version 0.1.0-preview.1
```

Until publication, build the five packages with `./eng/pack.sh` from a source checkout and add the resulting `artifacts/packages` directory as a NuGet source. Keep NuGet.org enabled for external dependencies. The adapter brings in the Agent Memory, Context Retriever, and modeling packages transitively. Surface administration requires explicitly adding the optional `Redis.Iris.ContextRetriever.Management` package. Applications using only the Agent Memory REST client can install `Redis.Iris.AgentMemory` instead.

If you used the earlier source preview named `Microsoft.Agents.AI.Redis.Iris`, change the package/project reference and `using` directives to `Redis.Iris.AgentFramework`. The assembly is now `Redis.Iris.AgentFramework.dll`; rebuild your application and restart notebook kernels before loading it. Provider class names and behavior are unchanged. No compatibility assembly is supplied for the old name.

Supply these environment settings through your shell or secret manager; the snippets do not read a repository `.env` file:

| Setting | Source |
| --- | --- |
| `REDIS_IRIS_ENDPOINT` | Agent Memory data-plane HTTPS URL |
| `REDIS_IRIS_STORE_ID` | Existing memory store ID |
| `REDIS_IRIS_API_KEY` | Store-scoped agent credential |
| `REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT` | Existing surface's MCP HTTPS URL |
| `REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY` | Agent key scoped to that surface and intended records |

Only the settings for the connector you run are needed. Loopback HTTP is supported for local development.

## Agent Memory round trip

Replace `Program.cs` with the following. It writes one synthetic event, reads it, and deletes that unique session. Use a dedicated development store that permits key-only service identities. Ownership-enforcing deployments require a delegated user token instead, as described below. Session deletion does not delete memories a background worker may already have extracted.

```csharp
using Redis.Iris.AgentMemory;

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set {name}.");

using var http = new HttpClient(new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
}) { Timeout = TimeSpan.FromSeconds(30) };
var client = new RedisAgentMemoryClient(http,
    new() { Endpoint = new Uri(Required("REDIS_IRIS_ENDPOINT")), StoreId = Required("REDIS_IRIS_STORE_ID") },
    new FixedAgentMemoryCredentialProvider(Required("REDIS_IRIS_API_KEY")));
var sessionId = "quickstart-" + Guid.NewGuid().ToString("N");
var ownerId = "quickstart-owner-" + Guid.NewGuid().ToString("N");
try
{
    await client.AddSessionEventAsync(new()
    {
        SessionId = sessionId,
        ActorId = ownerId,
        Role = SessionEventRole.USER,
        Content = [new("Synthetic example: I prefer window seats.")],
        CreatedAt = DateTimeOffset.UtcNow,
    });
    var session = await client.GetSessionMemoryAsync(sessionId);
    Console.WriteLine($"Read {session.Events.Count} event(s) for the synthetic session.");
}
finally
{
    await client.DeleteSessionMemoryAsync(sessionId);
}
```

Run `dotnet run`. For personal memory, pass an `IAgentMemoryDelegatedTokenProvider` as the fourth client constructor argument. It must obtain a current signed user access token from your application's authenticated context; never take identity claims or credentials from a model prompt. `ActorId` and `OwnerId` must match the token's subject on an ownership-enforcing deployment. A configured provider returning no token fails closed.

## Attach Agent Framework providers

An application with an existing `IChatClient` can use this factory. `resolveIdentity` must resolve the authenticated caller and the requested session from trusted host state; `authorize` must verify the caller's access to that session on every call. Persist the generated session ID and keep its owner, tenant, namespace, and topic bindings stable when resuming it.

```csharp
using Microsoft.Agents.AI;
using Redis.Iris.AgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Redis.Iris.AgentMemory;

public static class IrisAgentFactory
{
    public static AIAgent Create(IChatClient chatClient, Uri endpoint, string storeId,
        IAgentMemoryCredentialProvider credential,
        Func<AgentSession?, RedisIrisIdentityContext> resolveIdentity,
        Func<RedisIrisIdentityContext, bool> authorize,
        out ServiceProvider services,
        IAgentMemoryDelegatedTokenProvider? delegatedToken = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddRedisIrisAgentMemory(options =>
        {
            options.Endpoint = endpoint;
            options.StoreId = storeId;
            options.CredentialProvider = credential;
            options.DelegatedTokenProvider = delegatedToken;
            options.IdentityResolver = resolveIdentity;
            options.AuthorizationPolicy = authorize;
            options.RecallFailurePolicy = RedisIrisFailurePolicy.FailClosed;
        });
        services = registrations.BuildServiceProvider();
        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            ChatHistoryProvider = services.GetRequiredService<RedisIrisChatHistoryProvider>(),
            AIContextProviders = [services.GetRequiredService<RedisIrisMemoryProvider>()],
        });
    }
}
```

Dispose the returned service provider when the owning agent/application scope ends. The example explicitly fails closed on recall errors; the library default is fail-open recall. Host identity and scope validation always propagate; credential/service errors follow the configured failure policy. Treat summaries and recalled memories as untrusted content. For a complete model-backed application, see the [persistent sample](../samples/RedisIrisPersistentAgent/README.md).

## Discover Context Retriever tools

This complete `Program.cs` uses a fixed service identity. Every caller of an agent receiving these tools receives that identity's full server-authorized scope. Use a narrowly scoped key and keep the lease alive while its tools are attached.

```csharp
using Redis.Iris.AgentFramework;
using Redis.Iris.ContextRetriever;

static string Required(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Set {name}.");

var profile = new ContextRetrieverAccessProfile(
    new Uri(Required("REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT")),
    "quickstart-service",
    new FixedContextRetrieverCredentialProvider(new(
        Required("REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY"), "key-version-1")));
await using var tools = await RedisContextRetrieverTools.ConnectAsync(profile, new ContextRetrieverClientFactory());
foreach (var tool in tools.Tools) Console.WriteLine(tool.Name);
```

For multiple users, use `RedisContextRetrieverProvider` with an `IContextRetrieverAccessResolver` and an application-owned `ContextRetrieverClientCache`. Resolve a separate stable access-profile ID from trusted authorization state for each principal/scope, provide current credential versions, and asynchronously dispose the cache at host shutdown. Bind authorization arguments with `BoundToolArguments`; do not expose tenant or owner selection to the model. See [the Context Retriever guide](context-retriever.md) and [security model](../SECURITY.md) for delegated tokens, rotation, and invocation boundaries.
