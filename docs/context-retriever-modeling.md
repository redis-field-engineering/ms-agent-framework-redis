# Context Retriever POCO modeling

Application POCOs can be the canonical domain types while carrying declarative Context Retriever mapping metadata. Model compilation reads types and properties; it never instantiates entities or reads application data. Only properties marked with `ContextField` or added through fluent configuration are exposed.

```csharp
[ContextEntity(KeyTemplate = "customer:{id}", Description = "Customer account")]
public sealed class Customer
{
    [ContextField(Name = "id", IsKeyComponent = true)]
    public required string Id { get; init; }

    [ContextField(Index = ContextIndex.Text)]
    public required string Name { get; init; }
}

var model = ContextModelCompiler.Compile("Commerce", "Customer model", typeof(Customer));
await ContextDataModelJson.WriteAsync(model, "context-model.json");
```

`JsonPropertyName` is honored; otherwise CLR property names use camel case. Set `ContextField.Name` for a stable external field name that must not change during CLR refactoring.

## Fluent configuration

Domain assemblies can remain free of Context Retriever attributes:

```csharp
services.AddRedisContextModel("Commerce", "Customer model", model => model
    .Entity<Customer>(entity => entity
        .KeyTemplate("customer:{id}")
        .Field(x => x.Id, field => field.Name("id").KeyComponent())
        .Field(x => x.Name, field => field.Text())));
```

Fluent configuration can also override annotated names, descriptions, fields, and relationships. Compilation is explicit and cached; the application does not scan every loaded assembly on requests.

## Review and deploy

`ContextDataModelComparer.Compare` identifies additive and destructive changes for CI review. Install the optional `Redis.Iris.ContextRetriever.Management` package to create, read, or update surfaces directly through the administrative REST API. The runtime integration and modeling packages do not depend on management; no CLI or child process is involved.

```csharp
using Redis.Iris.ContextRetriever.Management;

using var http = new HttpClient(new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
});
var management = new ContextRetrieverManagementClient(http,
    new() { Endpoint = new Uri(managementEndpoint) },
    new FixedContextRetrieverManagementCredentialProvider(adminApiKey));

var surface = await management.CreateSurfaceAsync(model, new()
{
    Name = "commerce",
    RedisAddress = "redis.example:6380",
    RedisUsername = "app",
    RedisPassword = redisPassword,
    RedisTls = true,
}, cancellationToken);

var current = await management.GetSurfaceAsync(surface.SurfaceId, cancellationToken);
// Review ContextDataModelComparer.Compare(current.DataModel!, revisedModel) first.
await management.UpdateSurfaceAsync(surface.SurfaceId,
    new() { DataModel = revisedModel }, cancellationToken);
```

Use the service base URL, such as `http://localhost:8080` or the managed gateway's `https://cloud.redis.io/context-surfaces`, without `/mcp` or `/api/v1`. Gateway path prefixes are preserved. Non-loopback endpoints require HTTPS. Authentication uses an explicitly supplied admin credential in `X-API-Key`; the client does not read CLI login state, `.env` files, or runtime agent credentials.

For dependency injection, call `services.AddRedisContextRetrieverManagement(options, credentialProvider)`. It returns an `IHttpClientBuilder` for timeout configuration and registers `IContextRetrieverManagementClient`. The default handler disables redirects and cookies, and HTTP factory logging is disabled. A custom `IContextRetrieverManagementCredentialProvider` can resolve a current credential on every operation. Direct construction requires the caller to own the `HttpClient`, disable redirects and cookies, and avoid logging credential headers or request bodies.

Update fields omitted or set to null are left unchanged. An empty description or metadata dictionary clears that field; `DataModel` replaces the entire model. Creation configures a Redis data source but does not import records. Neither operation migrates Redis data. Surface administration belongs in deployment tooling, never in an agent or `RedisContextRetrieverProvider`.

Each operation sends once without automatic retries. `ContextRetrieverManagementException` exposes the operation and HTTP status when available, without raw response bodies or underlying exception messages. Cancellation or a failed response cannot undo a request already accepted by the service; reconcile the surface before retrying a write.

This replaces the preview `ContextCtlSurfaceDeployer` API. Add the management package explicitly, move `ContextSurfaceDeployment` imports to its namespace, and call `CreateSurfaceAsync` with an explicit endpoint and credential provider. Repository provisioning scripts still use `ctxctl` independently; they are not part of the NuGet libraries.

## Typed application reads

`ContextRetrieverTypedClient` uses only tools discovered in an already authorized MCP session. `GetByIdAsync<T>` resolves the generated get tool from the registered model, while `InvokeAsync<T>` supports other generated tools. Typed access does not bypass agent-key access tags or provide arbitrary Redis queries.
