# Redis Iris integration for Microsoft Agent Framework

This internal Redis Field Engineering project defines and tracks an integration between Redis Iris and Microsoft Agent Framework.

The current priority is a .NET connector for Redis Agent Memory, followed by Redis Context Retriever integration over local MCP. Python parity is planned afterward; Go is currently out of scope.

## Project documents

- [Integration design](INTEGRATION_DESIGN.md)
- [Product requirements and story backlog](tasks/prd-redis-iris-ms-agent-framework-integration.md)
- [Local integration environment](LOCAL_DEVELOPMENT.md)
- [Security model](SECURITY.md)
- [Observability](OBSERVABILITY.md)
- [Persistent .NET sample](samples/RedisIrisPersistentAgent/README.md)
- [ASP.NET Core DevUI sample](samples/RedisIrisDevUI/README.md)
- [C# Jupyter notebooks](notebooks/csharp/README.md)
- [Testing and release quality gate](TESTING.md)

## Current status

Milestone 1 implements the .NET Agent Memory client, Agent Framework chat-history and long-term-memory providers, guarded dependency-injection registration, authorization boundaries, and OpenTelemetry-compatible diagnostics.

## Supported versions

| Component | Supported baseline |
| --- | --- |
| .NET | 8.0 LTS |
| Microsoft Agent Framework | 1.22.x |
| Redis Agent Memory | Managed `/v1` data plane |

The connector intentionally pins Agent Framework 1.22.0 for reproducible builds. New minor versions must pass the complete quality gate before this matrix is updated.
