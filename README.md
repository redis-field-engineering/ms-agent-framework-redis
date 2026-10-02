# Redis Iris integration for Microsoft Agent Framework

This internal Redis Field Engineering project defines and tracks an integration between Redis Iris and Microsoft Agent Framework.

The current priority is a .NET connector for Redis Agent Memory, followed by Redis Context Retriever integration over local MCP. Python parity is planned afterward; Go is currently out of scope.

## Project documents

- [Integration design](INTEGRATION_DESIGN.md)
- [Product requirements and story backlog](tasks/prd-redis-iris-ms-agent-framework-integration.md)
- [Local integration environment](LOCAL_DEVELOPMENT.md)
- [Security model](SECURITY.md)
- [Observability](OBSERVABILITY.md)
- [Context Retriever integration](docs/context-retriever.md)
- [Context Retriever POCO modeling](docs/context-retriever-modeling.md)
- [Context Retriever model sample](samples/RedisIrisContextModel/README.md)
- [Persistent .NET sample](samples/RedisIrisPersistentAgent/README.md)
- [ASP.NET Core DevUI sample](samples/RedisIrisDevUI/README.md)
- [C# Jupyter notebooks](notebooks/csharp/README.md)
- [Testing and release quality gate](TESTING.md)

## Current status

Milestones 1–3 implement the .NET Agent Memory client and Agent Framework providers plus Redis Context Retriever over local MCP. Context Retriever supports a recommended per-invocation identity provider and an explicitly owned fixed-service-identity helper over one principal-isolated MCP implementation, with authorization hardening, OpenTelemetry-compatible diagnostics, and bounded operational controls.

## Supported versions

| Component | Supported baseline |
| --- | --- |
| .NET | 8.0 LTS |
| Microsoft Agent Framework | 1.23.x |
| Redis Agent Memory | Managed `/v1` data plane |
| Redis Context Retriever | Managed local MCP endpoint |

The connector intentionally pins Agent Framework 1.23.0 for reproducible builds. New minor versions must pass the complete quality gate before this matrix is updated.
