# Testing and release quality gate

Run each suite independently:

```bash
dotnet test tests/Redis.Iris.AgentFramework.Tests
dotnet test tests/Redis.Iris.AgentMemory.Tests
dotnet test tests/Redis.Iris.AgentMemory.LiveTests
dotnet test tests/Redis.Iris.ContextRetriever.LiveTests
```

The first project covers provider unit and composition behavior. The second covers the HTTP data-plane contract, serialization, tracing, and metrics with an in-memory handler/listener. The Agent Memory live suite uses a managed store and reports skipped tests when `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_STORE_ID`, or `REDIS_IRIS_API_KEY` is absent.

The Context Retriever live suite uses `REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT` plus the scoped credential bundle generated at `artifacts/context-retriever/redis-iris-travel-users.json`. Override the bundle location with `REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE`. It requires distinct travelers and credentials, verifies two-user server-side ACL isolation and get-by-ID denial in both directions, and checks trusted traveler-ID injection and model-visible schema hardening. The provider filter check requires a populated own-user result and an empty foreign-user result; a non-null MCP error is not success. The suite is read-only and skips cleanly until the endpoint and credential bundle are available.

The suite accepts either a per-traveler agent-key array or the shared-key bundle
with delegated JWTs. For the self-managed cluster, follow
[Kubernetes sample setup](docs/kubernetes-samples.md), then `source secrets/iris-k8s.env`
before running the two live projects. That profile supplies both service endpoints
and the Kubernetes-specific user bundle without loading the Cloud configuration.

The managed suite verifies multi-role event ordering, individual-event retrieval and deletion, session deletion, Agent Framework history persistence and reconstruction after provider recreation, owner-scoped long-term-memory search, owner isolation, and rejection of invalid bearer credentials. Every mutable test uses unique synthetic identifiers and cleans up its session. The owner-isolation test seeds a positive memory for each owner and requires provider recall of the authorized record. The opt-in promotion test appends 24 synthetic events and requires populated promotion results plus provider recall. It waits up to eight minutes by default (`REDIS_IRIS_PROMOTION_TIMEOUT_SECONDS`, 30–900 seconds). Use only a dedicated test store with extraction enabled and a threshold below 24 events.

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
actors, missing/invalid user tokens, and invalid agent keys. A cross-user session delete may return 404 or succeed as an idempotent no-op; the test requires the rightful owner to read the complete, unchanged session afterward in either case. A separate test seeds one LTM record under each signed user, requires positive own-user reads, and proves foreign-owner searches return no records. It deletes only fixture IDs created by that run.

The release quality gate excludes live tests:

```bash
dotnet restore ms-agent-framework-redis.slnx
dotnet format ms-agent-framework-redis.slnx --verify-no-changes --no-restore
dotnet build ms-agent-framework-redis.slnx -c Release --no-restore
dotnet test ms-agent-framework-redis.slnx -c Release --no-build --filter "Category!=Live"
./eng/pack.sh # destination artifacts/packages must be empty
./eng/package-smoke.sh
```

Nullable analysis and warnings-as-errors are repository-wide. Package validation checks that release archives contain no test credentials, captured customer data, build outputs, or test assemblies. Public API changes must be reviewed against the packages produced from the previous release before the supported-version matrix is changed.

## Optional live integration validation

Ordinary CI skips unconfigured live tests. Release validation explicitly excludes live tests and requires no service credentials. Run these optional checks independently of publication using the corresponding service profiles:

```bash
# Key-only dedicated test store with asynchronous extraction enabled:
REDIS_IRIS_RUN_PROMOTION_TESTS=1 python3 eng/live-release-gate.py managed
# Ownership-enforcing store with the fresh two-user signed-token bundle:
python3 eng/live-release-gate.py delegated
# Existing synthetic two-traveler surface and scoped credential bundle:
python3 eng/live-release-gate.py context
```

The runner retains its historical filename, `eng/live-release-gate.py`, but is not invoked by the release workflow. It rejects missing settings, skipped tests, empty runs, and failures so an explicitly requested live check cannot silently pass. Each suite runs on .NET 10. Do not point the key-only tests at an ownership-enforcing store; configure the delegated gate separately. Context tests can use per-user scoped keys or delegated tokens, and the validation results should identify which mode was tested.

LTM fixtures use random IDs and synthetic owners/topics, track IDs before bulk creation, inspect per-item responses, and delete known fixture records in `finally`/async disposal. The promotion test deletes its session and searches for records under its unique owner even after a failed assertion. Background jobs can finish after cleanup; use short retention on the dedicated test store and inspect/drain it before disposal. This test cleanup is not a complete erasure API or an erasure guarantee.

Validation on October 28, 2026: the managed Context Retriever gate passed all three tests with the existing scoped-key fixture. The self-managed RAM gate passed all five tests, including promotion and provider recall, within the eight-minute limit (about two to four minutes observed).

Validation on October 29, 2026: fresh Cognito authorization-code access tokens for two distinct demo users passed all three delegated RAM tests and all three Context Retriever tests in delegated-JWT mode against the self-managed deployment, using .NET 10. Neither gate skipped tests. Cross-user RAM reads returned 404; cross-user session deletes returned 204 without changing the owner's session, verified in both directions. The RAM gate now checks that preservation explicitly. These results apply to that deployment and test run; live validation remains separate from publication.

The package gate requires all five release packages at one version, validates internal dependency versions, and restores a standalone .NET 10 consumer from a fresh package cache. Source mapping restricts all connector dependencies to the newly built feed. NuGet.org supplies only external dependencies. The smoke program first loads the four runtime/modeling packages and verifies that management is absent, then explicitly references the optional management package and loads its public client.

## Notebook labs

After building Release, run `python3 eng/validate-notebooks.py` in a Python environment with `nbformat`, `nbclient`, and `jupyter_client`, plus a registered `.net-csharp` kernel. The validator checks all notebook schemas and executes the offline assertions in notebooks 06–08 in fresh kernels. Optional live cells are compiled inside uninvoked functions; this command does not access services or establish live authorization evidence. See [the notebook guide](notebooks/csharp/README.md) for prerequisites and the separate opt-in live isolation checks.
