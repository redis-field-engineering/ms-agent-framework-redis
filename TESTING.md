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

The managed suite verifies multi-role event ordering, individual-event retrieval and deletion, session deletion, Agent Framework history persistence and reconstruction after provider recreation, owner-scoped long-term-memory search, owner isolation, and rejection of invalid bearer credentials. Every mutable test uses unique synthetic identifiers and cleans up its session. The owner-isolation test seeds a positive memory for each owner and requires provider recall of the authorized record. The opt-in promotion test appends 24 synthetic events and requires populated promotion results plus provider recall. It waits up to eight minutes by default (`REDIS_IRIS_PROMOTION_TIMEOUT_SECONDS`, 30–900 seconds). Use only a dedicated test store with extraction enabled and a threshold below 24 events.

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
actors, missing/invalid user tokens, and invalid agent keys. A separate test seeds one LTM record under each signed user, requires positive own-user reads, and proves foreign-owner searches return no records. It deletes only fixture IDs created by that run.

The release quality gate is:

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

## Required live release evidence

Ordinary CI skips unconfigured live tests. Release validation must run these gates using the corresponding service profiles:

```bash
# Key-only dedicated test store with asynchronous extraction enabled:
REDIS_IRIS_RUN_PROMOTION_TESTS=1 python3 eng/live-release-gate.py managed
# Ownership-enforcing store with the fresh two-user signed-token bundle:
python3 eng/live-release-gate.py delegated
# Existing synthetic two-traveler surface and scoped credential bundle:
python3 eng/live-release-gate.py context
```

The runner rejects missing settings, skipped tests, empty runs, and failures. Each gate runs on .NET 10. Do not point the key-only tests at an ownership-enforcing store; configure the delegated gate separately. Context tests can use per-user scoped keys or delegated tokens, and the release evidence must identify which mode was tested.

LTM fixtures use random IDs and synthetic owners/topics, track IDs before bulk creation, inspect per-item responses, and delete known fixture records in `finally`/async disposal. The promotion test deletes its session and searches for records under its unique owner even after a failed assertion. Background jobs can finish after cleanup; use short retention on the dedicated test store and inspect/drain it before disposal. This test cleanup is not a complete erasure API or an erasure guarantee.

Validation on October 28, 2026: the managed Context Retriever gate passed all three tests with the existing scoped-key fixture. The self-managed RAM store passed the four session/seeded-isolation checks; the initial three-minute promotion attempt timed out. Promotion and delegated-JWT evidence must be completed before publishing.
