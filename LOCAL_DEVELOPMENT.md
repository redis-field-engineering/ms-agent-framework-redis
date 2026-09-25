# Local integration environment

## Decision

Redis Agent Memory and Redis Context Retriever run as managed Redis Cloud services. Everything owned by this repository runs locally in Docker Compose.

Self-managed Iris deployments are not part of the default developer environment. They may be added later as optional compatibility tests, but local Kubernetes is not required for normal development or CI.

## Topology

```text
Docker Compose
  ├── sample-agent / integration-test-runner
  ├── Agent Memory protocol fake (offline profile)
  ├── Context Retriever MCP fake (offline profile)
  ├── OpenTelemetry Collector
  ├── Prometheus
  ├── Tempo
  └── Grafana
          │
          ├── HTTPS ──► Managed Redis Agent Memory
          └── MCP   ──► Managed Redis Context Retriever
                            └── Synthetic Redis Cloud data
```

The local application connects to the managed services through credentials supplied at runtime. The observability stack receives connector telemetry locally; Iris services remain remote.

## Compose profiles

### `managed`

The primary development and end-to-end profile.

- Runs the application/test runner and local observability stack.
- Connects to dedicated managed Agent Memory and Context Retriever development services.
- Exercises real authentication, authorization, session storage, promotion, semantic recall, MCP discovery, access tags, and tool execution.
- Requires cloud credentials and network access.

### `offline`

The deterministic PR and inner-loop profile.

- Replaces the two managed endpoints with local protocol fakes.
- Exercises serialization, mapping, lifecycle, timeout, retry, credential rotation, authorization failures, cross-tenant isolation, and telemetry.
- Does not claim compatibility with real promotion, semantic search, or Context Retriever authorization.
- Requires no Redis Cloud credentials.

### `observability`

Runs only the OpenTelemetry Collector, Prometheus, Tempo, and Grafana so a developer can run the application directly with `dotnet run` while retaining the same telemetry environment.

## Managed service isolation

The development environment must use synthetic data only.

- Use dedicated non-production Agent Memory and Context Retriever services.
- Use separate credentials for developers and CI where practical.
- Generate a unique test-run prefix for owner, session, namespace, and seeded entity identifiers.
- Context Retriever data must be safely reset or uniquely scoped between runs.
- Agent Memory cleanup must delete both session memory and explicitly created long-term memories; session deletion alone does not remove promoted LTM.
- Tests must never target customer stores or production agent keys.

Example run prefix:

```text
maf-iris-{developer-or-ci}-{run-id}
```

The connector's identifier codec will convert this logical identifier to service-valid IDs where required.

## Configuration

Secrets are supplied through an ignored `.env.local`, CI secret store, or the developer's secret manager. The repository will provide `.env.example` containing names only.

Required managed Agent Memory settings:

```text
AGENT_MEMORY_BASE_URL
AGENT_MEMORY_STORE_ID
AGENT_MEMORY_API_KEY
```

Required managed Context Retriever settings will include:

```text
CONTEXT_RETRIEVER_MCP_URL
CONTEXT_RETRIEVER_AGENT_KEY
CONTEXT_RETRIEVER_ADMIN_URL
CONTEXT_RETRIEVER_ADMIN_KEY
```

Admin credentials are used only by explicit seed/reset tooling. The agent application receives only an agent key with the minimum access tags needed by its test profile.

Local telemetry defaults:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_SERVICE_NAME=ms-agent-framework-redis-dev
```

No credential, prompt, recalled memory, tool argument/result, owner ID, or session ID is exported by default.

## Developer commands

The repository should expose a small command surface, implemented by scripts or a task runner:

```bash
./scripts/dev-env.sh up managed
./scripts/dev-env.sh up offline
./scripts/dev-env.sh up observability
./scripts/dev-env.sh seed
./scripts/dev-env.sh test
./scripts/dev-env.sh reset
./scripts/dev-env.sh down
```

Expected behavior:

- `up` validates required tools and configuration before starting containers.
- `seed` creates only synthetic Context Retriever entities/surfaces and test access profiles.
- `test` selects managed or offline tests from the active profile.
- `reset` removes only resources carrying the current test-run prefix.
- `down` stops local containers but never deletes managed data implicitly.

## Test allocation

| Test type | Offline profile | Managed profile |
|---|---:|---:|
| DTO serialization and validation | Yes | No |
| Agent Framework lifecycle and message mapping | Yes | Smoke |
| Authentication failure mapping | Yes | Yes |
| Credential rotation | Yes | Yes |
| Owner and access-profile isolation | Yes | Yes |
| Timeout, throttling, malformed response, retry | Yes | Limited |
| Session round trip | Simulated | Yes |
| Background promotion | No | Yes, bounded polling |
| Semantic LTM search | Simulated | Yes |
| Context Retriever tool discovery/execution | Simulated | Yes |
| Context Retriever access tags | Simulated | Yes |
| Retention and deletion | Simulated | Yes |
| Trace/metric names and redaction | Yes | Yes |

Managed promotion tests must poll with a bounded, generous timeout. They must not assert that an event is promoted immediately.

## CI policy

- Pull requests run unit tests and the Docker `offline` profile.
- Nightly builds run the Docker `managed` profile against isolated synthetic data.
- Release candidates run the managed compatibility matrix and retention/deletion cleanup verification.
- Forked or untrusted pull requests never receive managed-service credentials.
- Managed tests use concurrency controls or unique run scopes to prevent cross-run interference.
- Cleanup runs after success, failure, or cancellation and reports partial cleanup without masking the original test failure.

## Initial deliverables

1. `compose.yaml` with `managed`, `offline`, and `observability` profiles.
2. OTel Collector, Prometheus, Tempo, and Grafana configuration.
3. Agent Memory REST fake implementing only consumed production endpoints.
4. Context Retriever MCP fake built with the official MCP SDK.
5. Synthetic Context Retriever seed/reset tooling.
6. `.env.example` and secret-handling documentation.
7. One smoke test proving session persistence, LTM recall, MCP tool execution, trace parenting, and metric emission against managed services.

