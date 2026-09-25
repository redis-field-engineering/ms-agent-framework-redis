# Testing and release quality gate

Run each suite independently:

```bash
dotnet test tests/Microsoft.Agents.AI.Redis.Iris.Tests
dotnet test tests/Redis.Iris.AgentMemory.Tests
dotnet test tests/Redis.Iris.AgentMemory.LiveTests
```

The first project covers provider unit and composition behavior. The second covers the HTTP data-plane contract, serialization, tracing, and metrics with an in-memory handler/listener. The live suite uses a managed Agent Memory store and reports skipped tests when `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_STORE_ID`, or `REDIS_IRIS_API_KEY` is absent.

The managed suite verifies multi-role event ordering, individual-event retrieval and deletion, session deletion, Agent Framework history persistence and reconstruction after provider recreation, owner-scoped long-term-memory search, owner isolation, and rejection of invalid bearer credentials. Every mutable test uses unique synthetic identifiers and cleans up its session. It deliberately does not assert immediate promotion: managed promotion is eventually consistent, and promoted records require a separate explicit LTM cleanup capability before a repeatable promotion test can safely create them.

Before merging:

```bash
dotnet restore ms-agent-framework-redis.slnx
dotnet format ms-agent-framework-redis.slnx --verify-no-changes --no-restore
dotnet build ms-agent-framework-redis.slnx -c Release --no-restore
dotnet test ms-agent-framework-redis.slnx -c Release --no-build
dotnet pack src/Redis.Iris.AgentMemory -c Release --no-build -o artifacts
dotnet pack src/Microsoft.Agents.AI.Redis.Iris -c Release --no-build -o artifacts
./eng/validate-packages.sh artifacts
```

Nullable analysis and warnings-as-errors are repository-wide. Package validation checks that release archives contain no test credentials, captured customer data, build outputs, or test assemblies. Public API changes must be reviewed against the packages produced from the previous release before the supported-version matrix is changed.
