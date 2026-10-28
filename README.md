# Redis Iris integration for Microsoft Agent Framework

Redis Field Engineering’s preview .NET connectors provide durable Agent Framework history, owner-scoped memory recall, and authorized Redis Context Retriever tools. The packages are maintained by Redis Field Engineering; they are not Microsoft products.

Start with the [standalone .NET quickstart](docs/quickstart.md). Public publication is gated on package validation and positive live-service evidence; a green unit-test build alone does not establish release readiness.

## Project documents

- [Integration design](INTEGRATION_DESIGN.md)
- [Product requirements and story backlog](tasks/prd-redis-iris-ms-agent-framework-integration.md)
- [Local integration environment](LOCAL_DEVELOPMENT.md)
- [Run samples against the Iris Kubernetes cluster](docs/kubernetes-samples.md)
- [Security model](SECURITY.md)
- [Preview guarantees and limitations](docs/operational-contract.md)
- [Observability](OBSERVABILITY.md)
- [Context Retriever integration](docs/context-retriever.md)
- [Context Retriever POCO modeling](docs/context-retriever-modeling.md)
- [Context Retriever model sample](samples/RedisIrisContextModel/README.md)
- [Persistent .NET sample](samples/RedisIrisPersistentAgent/README.md)
- [ASP.NET Core DevUI sample](samples/RedisIrisDevUI/README.md)
- [C# Jupyter notebooks](notebooks/csharp/README.md)
- [Testing and release quality gate](TESTING.md)
- [Preview release procedure](docs/releasing.md)

## Current status

Milestones 1–3 implement the .NET Agent Memory client and Agent Framework providers plus Redis Context Retriever over local MCP. Context Retriever supports a recommended per-invocation identity provider and an explicitly owned fixed-service-identity helper over one principal-isolated MCP implementation, with authorization hardening, OpenTelemetry-compatible diagnostics, and bounded operational controls.

## Supported versions

| Component | Supported baseline |
| --- | --- |
| Build SDK | .NET 10 (selected by `global.json`; required for `.slnx`) |
| Application runtimes | .NET 10 LTS recommended; .NET 8 compatibility tested until its November 10, 2026 end of support |
| Microsoft Agent Framework | 1.23.x |
| Redis Agent Memory | Managed or self-managed `/v1` data plane |
| Redis Context Retriever | Managed MCP or self-managed 0.4.2 over loopback |

The connector intentionally pins Agent Framework 1.23.0 for reproducible builds. New minor versions must pass the complete quality gate before this matrix is updated.

The libraries target `net8.0` and can be consumed by .NET 10 applications. Tests run on both runtimes; install both runtimes to run the complete suite locally. The build SDK and library target framework are independent.

## License and package version

This repository is licensed under [MIT](LICENSE). All four packages use the same preview version, `0.1.0-preview.1`, declared in `src/Directory.Build.props`. Preview APIs may change before a stable release. A version in source does not mean the package has been published to NuGet.
