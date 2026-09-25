# Testing and release quality gate

Run each suite independently:

```bash
dotnet test tests/Microsoft.Agents.AI.Redis.Iris.Tests
dotnet test tests/Redis.Iris.AgentMemory.Tests
dotnet test tests/Redis.Iris.AgentMemory.LiveTests
```

The first project covers provider unit and composition behavior. The second covers the HTTP data-plane contract, serialization, tracing, and metrics with an in-memory handler/listener. The live suite uses a managed Agent Memory store and reports a skipped test when `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_STORE_ID`, or `REDIS_IRIS_API_KEY` is absent.

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
