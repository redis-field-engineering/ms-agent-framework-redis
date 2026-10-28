# Testing and release quality gate

Run each suite independently:

```bash
dotnet test tests/Microsoft.Agents.AI.Redis.Iris.Tests
dotnet test tests/Redis.Iris.AgentMemory.Tests
dotnet test tests/Redis.Iris.AgentMemory.LiveTests
dotnet test tests/Redis.Iris.ContextRetriever.LiveTests
```

The first project covers provider unit and composition behavior. The second covers the HTTP data-plane contract, serialization, tracing, and metrics with an in-memory handler/listener. The Agent Memory live suite uses a managed store and reports skipped tests when `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_STORE_ID`, or `REDIS_IRIS_API_KEY` is absent.

The Context Retriever live suite uses `REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT` plus the scoped credential bundle generated at `artifacts/context-retriever/redis-iris-travel-users.json`. Override the bundle location with `REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE`. It verifies two-user server-side ACL isolation, cross-traveler get-by-ID denial, trusted traveler-ID injection, model-visible schema hardening, and rejection of authorization-field substitution. The suite is read-only and skips cleanly until the endpoint and credential bundle are available.

The suite accepts either a per-traveler agent-key array or the shared-key bundle
with delegated JWTs. For the self-managed cluster, follow
[Kubernetes sample setup](docs/kubernetes-samples.md), then `source secrets/iris-k8s.env`
before running the two live projects. That profile supplies both service endpoints
and the Kubernetes-specific user bundle without loading the Cloud configuration.

The managed suite verifies multi-role event ordering, individual-event retrieval and deletion, session deletion, Agent Framework history persistence and reconstruction after provider recreation, owner-scoped long-term-memory search, owner isolation, and rejection of invalid bearer credentials. Every mutable test uses unique synthetic identifiers and cleans up its session. It deliberately does not assert immediate promotion: managed promotion is eventually consistent, and promoted records require a separate explicit LTM cleanup capability before a repeatable promotion test can safely create them.

Before merging:

The optional RAM delegated suite requires an ownership-enforcing RAM deployment
and `REDIS_IRIS_RAM_DELEGATED_USERS_FILE` pointing to an ignored, mode-0600 JSON
array of exactly two objects with `subject`, `accessToken`, and `expiresAt`
(ISO-8601 UTC). Tokens must be fresh signed Cognito access tokens; `subject`
must equal their validated `sub`. Load that deployment's RAM endpoint/store/key
and run only this suite, since the original key-only live tests use arbitrary
owners:

```bash
dotnet test tests/Redis.Iris.AgentMemory.LiveTests --filter FullyQualifiedName~DelegatedAgentMemoryLiveTests
```

It checks personal session creation/read/list/delete isolation, forged initial
actors, missing/invalid user tokens, and invalid agent keys. It also checks a
foreign-owner LTM search returns no results, but does not seed promoted memories
or prove asynchronous promotion. It cleans up only unique sessions created by
that run; promoted test data, if any, requires separate explicit cleanup.

The release quality gate is:

```bash
dotnet restore ms-agent-framework-redis.slnx
dotnet format ms-agent-framework-redis.slnx --verify-no-changes --no-restore
dotnet build ms-agent-framework-redis.slnx -c Release --no-restore
dotnet test ms-agent-framework-redis.slnx -c Release --no-build
./eng/pack.sh # destination artifacts/packages must be empty
./eng/package-smoke.sh
```

Nullable analysis and warnings-as-errors are repository-wide. Package validation checks that release archives contain no test credentials, captured customer data, build outputs, or test assemblies. Public API changes must be reviewed against the packages produced from the previous release before the supported-version matrix is changed.

The package gate requires all four release packages at one version, validates internal dependency versions, and restores a standalone .NET 10 consumer from a fresh package cache. Source mapping restricts all connector dependencies to the newly built feed. NuGet.org supplies only external dependencies. The smoke program loads public types from all four packages.
