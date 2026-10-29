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

`ContextDataModelComparer.Compare` identifies additive and destructive changes for CI review. `ContextCtlSurfaceDeployer` writes a temporary artifact and calls the confirmed CLI contract:

```text
ctxctl --output json surface create --datamodel context-model.json ...
```

Run `ctxctl auth login` or create/store the default admin key before deployment. The wrapper deliberately does not accept or forward an admin key. Surface administration belongs in deployment tooling, never in an agent or `RedisContextRetrieverProvider`. The current CLI accepts the Redis password as a process argument; use deployment-host process isolation and avoid command logging.

## Typed application reads

`ContextRetrieverTypedClient` uses only tools discovered in an already authorized MCP session. `GetByIdAsync<T>` resolves the generated get tool from the registered model, while `InvokeAsync<T>` supports other generated tools. Typed access does not bypass agent-key access tags or provide arbitrary Redis queries.

Cancellation of `CreateAsync` terminates the local `ctxctl` process tree and waits
for process/output cleanup before removing the temporary manifest. It cannot
undo a deployment request already accepted by the service. Inspect the service
for an existing surface before retrying an interrupted create.
