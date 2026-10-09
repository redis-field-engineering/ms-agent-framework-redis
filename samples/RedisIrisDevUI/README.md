# Redis Iris Agent Memory DevUI

This sample adapts Microsoft Agent Framework's official ASP.NET Core DevUI pattern to the Redis Iris integration. It provides an interactive browser chat, owner-scoped RAM inspection and cleanup endpoints, and an annotated structured-context model for a travel agency.

The hosted agent uses Agent Framework's in-memory session store so each DevUI conversation preserves its `AgentSession` and Redis Iris provider state across HTTP requests. It supports an optional multi-user demonstration mode that partitions sessions, RAM, and Context Retriever access by a trusted composite tenant/traveler identity. The store remains process-local; production hosting should use its normal identity provider and durable session storage.

## Run

Add `OPENAI_API_KEY` to the repository root `.env.local`, then run:

```bash
./scripts/run-agent-memory-devui.sh
```

Open [http://localhost:5080](http://localhost:5080) for the sample dashboard or [http://localhost:5080/devui](http://localhost:5080/devui) for the Agent Framework DevUI.

## Inspect the structured travel model

The sample defines ordinary application POCOs for `Traveler`, `FlightReservation`, `HotelReservation`, and `TravelReceipt`. Their annotations compile at startup into the same deterministic DataModel accepted by `ctxctl surface create --datamodel`. Every entity maps its tag-indexed `tenant_id` and `traveler_id` fields through `acl_field_mappings`; stable entity IDs remain the storage-key components.

| Surface | Purpose |
| --- | --- |
| `/api/context/model` | Inspect or download the generated Context Retriever DataModel |
| `/api/context/examples` | Inspect import-ready synthetic POCO records and their foreign-key relationships |

Starting the DevUI does not perform administrative or data-loading operations. Surface deployment and demo-data seeding are explicit one-shot jobs.

The model can also be generated without Redis or OpenAI credentials:

```bash
dotnet run --project samples/RedisIrisDevUI -- --print-context-model
```

## Provision the managed Context Retriever surface

The optional one-shot Compose provisioner generates this model inside Docker, checks for a surface with the configured stable name, creates it when absent, and updates its DataModel when present. Add these values to the ignored `.env.local` file:

```dotenv
CONTEXT_RETRIEVER_ADMIN_URL="https://cloud.redis.io/context-surfaces"
CONTEXT_RETRIEVER_ADMIN_KEY="cs_admin_..."
CONTEXT_RETRIEVER_REDIS_ADDRESS="your-redis-host:port"
CONTEXT_RETRIEVER_REDIS_USERNAME="default"
CONTEXT_RETRIEVER_REDIS_PASSWORD="..."
CONTEXT_RETRIEVER_REDIS_TLS="true"
CONTEXT_RETRIEVER_SURFACE_NAME="redis-iris-travel-assistant"
```

Then run only the provisioner:

```bash
docker compose --profile context-retriever run --rm --build context-provisioner
```

The generated artifact is written to `artifacts/context-retriever/redis-iris-travel-context-model.json`. If exactly one surface with that name exists, its DataModel is updated by default so security mappings cannot remain stale. Set `CONTEXT_RETRIEVER_UPDATE_EXISTING=false` only when intentionally generating artifacts without applying them; duplicate names fail safely instead of selecting an arbitrary surface.

The administrative URL/key above are control-plane credentials. Do not use the regional runtime URL ending in `/mcp` as `CONTEXT_RETRIEVER_ADMIN_URL`. The DevUI's runtime Context Retriever provider separately needs that MCP endpoint and a least-privilege agent key. The Redis connection identifies the data source from which the managed surface generates its tools.

## Seed the synthetic travel context

After the surface exists, run the one-shot seeder with the same admin URL, admin key, and stable surface name:

```bash
docker compose --profile context-retriever run --rm --build context-seeder
```

The seeder generates `artifacts/context-retriever/redis-iris-travel-context-seed.json` from the sample's POCO instances, resolves exactly one surface by name, and imports `Traveler`, `FlightReservation`, `HotelReservation`, and `TravelReceipt` separately. It includes two travelers so access isolation can be tested. Stable composite keys and `on_conflict=overwrite` make repeated runs safe. A missing surface, duplicate surface names, validation error, or partial import causes the job to fail.

Context Retriever access tags must use the same `tenant_id` and `traveler_id` names. The POCOs declare `AccessTag` on both fields, causing the generated surface model to mark them for server-side ACL filtering; an ordinary TAG index or a model-supplied filter alone is not an authorization boundary.

The DevUI also configures `AuthorizationOwnedFilterFields` for `tenant_id` and `traveler_id`. Those fields are removed from model-visible tag-condition enums and rejected if a caller nevertheless supplies them. Its trusted access resolver binds `get_traveler_by_id.id` to the authenticated user's domain traveler ID; that argument is removed from the model-visible schema and injected immediately before execution. The local sample's traveler-scoped agent key provides the server authorization boundary. The separate composite RAM owner ID is never accepted as a Context Retriever entity identifier.

Identity values have deliberately separate jobs. `traveler_id` is the canonical domain and conversational actor ID used by Context Retriever, Cognito claims, bound tool arguments, and restored message authors. RAM's length-prefixed composite owner exists only as a tenant-safe storage partition. The history provider rewrites legacy RAM user-event actors to the canonical traveler ID before messages reach the model, so values such as `17-redis-travel-demo-12-traveler-001` cannot be mistaken for an entity ID.

The records are synthetic demonstration data. Production applications should ingest authoritative records through an appropriately authenticated system-of-record workflow rather than ship the admin key with the application.

Single-user mode can use the surface's runtime credential directly from `.env.local`:

```dotenv
REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT="https://your-context-retriever-mcp-endpoint/mcp"
REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY="cs_agent_..."
REDIS_IRIS_CONTEXT_RETRIEVER_CREDENTIAL_VERSION="environment-v1"
```

The agent key must belong to the provisioned travel surface. The optional non-secret credential version lets a rotated key establish a fresh cached MCP session. The admin key is not passed to the DevUI connector.

## Demonstrate multi-user isolation

The one-shot `context-provisioner` creates one explicitly scoped agent key per demo traveler. This is the supported local path and does not depend on the managed service's preview IdP configuration. The application selects credentials only from its authenticated server-side user directory; neither the model nor the browser receives an agent key.

Run the provisioner before starting DevUI:

```bash
docker compose --profile context-retriever run --rm context-provisioner
docker compose up --build -d devui
```

The provisioner writes the scoped keys and independent high-entropy DevUI sign-in tokens to `artifacts/context-retriever/redis-iris-travel-users.json` with mode `0600`. The DevUI container mounts that bundle read-only and enables multi-user mode automatically. To retrieve a local sign-in token, inspect the ignored credential file directly; do not commit or share it. Because the control plane only returns a full agent key at creation time, deleting the bundle requires explicitly deleting or rotating the corresponding managed agents before generating replacements.

When this setting contains at least two users, the sample enables cookie authentication and Agent Framework session isolation. Open `/login`, enter one user’s local token, and then use DevUI normally. The authenticated server-side mapping supplies all of the following; none are accepted from model tool arguments:

- Agent Framework isolation key: `tenant_id:traveler_id`
- RAM tenant and actor identity, with a storage-safe length-prefixed owner derived from `tenant_id` and `traveler_id`
- Context Retriever access-profile ID and traveler-scoped agent key
- Filtering for the `/api/context/examples` inspection endpoint

Sign out before testing the second traveler. The provisioned keys are distinct and carry different `traveler_id` access tags. For a deployable delegated-JWT example backed by Cognito, see [`infra/aws-cognito-jwt`](../../infra/aws-cognito-jwt/README.md). Runtime application instances should never receive Context Retriever administrative credentials.

## Run the Docker observability demo

The repository-level Compose stack builds the DevUI and exports connector metrics and traces over OTLP to a local OpenTelemetry Collector:

```bash
docker compose up --build
```

The stack reads Redis Cloud and OpenAI credentials from the ignored `.env.local` file. It does not bake that file into the application image.
Docker bridge traffic requires DevUI's remote-access option, so Compose enables it while binding every published port to `127.0.0.1`. The non-container runner retains DevUI's loopback-only default.

| Surface | URL | Purpose |
| --- | --- | --- |
| DevUI | <http://localhost:5080/devui> | Exercise short- and long-term memory |
| Context status | <http://localhost:5080/api/context/status> | Confirm lazy MCP initialization and an active authorized session |
| Grafana | <http://localhost:3000/d/redis-iris-agent-memory> | View the provisioned Agent Memory metrics dashboard |
| Jaeger | <http://localhost:16686> | Inspect `redis-iris-devui` traces |
| Prometheus | <http://localhost:9090> | Query the raw exported metric series |

Generate some traffic in DevUI, allow one metrics export interval, then select the `redis-iris-devui` service in Jaeger. Grafana is configured for anonymous local viewing. Stop the stack with `docker compose down`; add `--volumes` only when you also want to discard the local Prometheus and Grafana data.

Jaeger stores and visualizes traces. Metrics travel over the same OTLP connection but are exposed by the Collector for Prometheus and visualized in Grafana.

The runner loads the existing Redis Cloud and OpenAI settings from `.env.local`. Optional settings are:

```dotenv
OPENAI_MODEL="gpt-4.1-mini"
SAMPLE_OWNER_ID="redis-iris-devui-shopper"
REDIS_IRIS_SHORT_TERM_WINDOW="4"
REDIS_IRIS_DEMO_TELEMETRY="false"
```

## Demonstrate both memory tiers

1. Start a DevUI conversation and tell the agent your name and several travel preferences.
2. Continue that conversation. RAM durably retains the complete session while the model receives the configured recent-message window.
3. Open `/api/ram` to see the trusted owner, generated RAM session IDs, and promoted long-term memories.
4. Open `/api/ram/sessions/{sessionId}` to inspect a session's events and managed summary.
5. After RAM's asynchronous promotion runs, create a new DevUI conversation and ask what the agent remembers.
6. After running `context-seeder`, ask for flight `IRIS42`, hotel reservation `HTL-6379`, or Avery Morgan's receipts and inspect `/api/context/status` plus the Context Retriever spans in Jaeger.

The application also prints `RAM USER` and `RAM SESSION` when it assigns a new Agent Framework session to RAM.

For recall troubleshooting, `RedisIrisMemoryProvider` logs the number of long-term memory records returned by each search. Query text, identity, memory IDs, and memory content are not logged.

## Clear the demo owner

Cleanup requires an exact owner confirmation and deletes only that owner's sessions and long-term memories:

```bash
curl -X DELETE 'http://localhost:5080/api/ram?confirm=redis-iris-devui-shopper'
```

This is deliberately a local-development sample. It refuses to start outside the ASP.NET Core `Development` environment. A production application must authenticate callers and derive the owner and tenant from trusted claims rather than a shared environment variable.
