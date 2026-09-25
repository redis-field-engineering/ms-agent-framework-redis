# PRD: Redis Iris Integration for Microsoft Agent Framework

## 1. Introduction

Build a Redis-owned integration between Redis Iris and Microsoft Agent Framework, designed so it can later be contributed upstream. The integration will prioritize .NET and deliver Redis Agent Memory before Redis Context Retriever. Python parity will follow; Go is out of scope.

The integration must give Agent Framework applications:

- Durable, ordered chat history backed by Redis Agent Memory session memory.
- Automatic, owner-scoped recall from Redis Agent Memory long-term memory.
- Governed access to live business data through Redis Context Retriever's local MCP endpoint.
- Production hooks for authentication, authorization, tenant isolation, OpenTelemetry, resilience, retention, privacy deletion, and credential rotation.

Context Retriever will support two entry points over one implementation:

1. A recommended `AIContextProvider` for per-invocation identity and authorization.
2. A direct tool-attachment helper for applications with one fixed service identity.

Both entry points will use the same local MCP client, authentication, connection management, authorization boundary, and telemetry. Hosted MCP is not part of the initial scope.

## 2. Goals

- Deliver a production-capable .NET Agent Memory integration before Context Retriever.
- Preserve the semantic separation between session history, long-term personal memory, and live operational data.
- Require explicit owner and tenant scoping so an application cannot accidentally perform unscoped user-memory retrieval.
- Support both fixed and per-invocation Context Retriever identities without duplicating protocol logic.
- Emit OpenTelemetry-compatible traces, metrics, and logs without exposing secrets or sensitive content by default.
- Provide deterministic retention and deletion behavior across session and long-term memory.
- Make failure behavior configurable while choosing safe defaults.
- Produce packages, tests, samples, and documentation suitable for eventual Microsoft Agent Framework upstream contribution.
- Follow Agent Framework lifecycle, state, dependency-injection, logging, and packaging conventions.

## 3. Delivery Structure

### Milestone 0: Contracts and foundations

- Epic 0A: Production API contract
- Epic 0B: Shared .NET client foundation
- Epic 0C: Security and observability conventions
- Epic 0D: Managed-service local integration environment

### Milestone 1: .NET Agent Memory MVP

- Epic 1A: Session history provider
- Epic 1B: Long-term memory context provider
- Epic 1C: Agent Memory authentication and authorization
- Epic 1D: Agent Memory telemetry and diagnostics
- Epic 1E: MVP documentation and release readiness

### Milestone 2: Agent Memory production hardening

- Epic 2A: Retention and privacy lifecycle
- Epic 2B: Resilience and idempotency
- Epic 2C: Explicit long-term-memory operations

### Milestone 3: .NET Context Retriever over local MCP

- Epic 3A: Shared local MCP client
- Epic 3B: Recommended context-provider integration
- Epic 3C: Fixed-identity direct attachment
- Epic 3D: Context Retriever security and observability

### Milestone 4: Python parity and upstream readiness

- Epic 4A: Python Agent Memory adapters
- Epic 4B: Python Context Retriever adapters
- Epic 4C: Cross-language contract and upstream preparation

## 4. User Stories

### Milestone 0: Contracts and foundations

#### US-001: Validate the Agent Memory production contract

**Epic:** 0A — Production API contract  
**Description:** As a connector developer, I want a versioned production API contract so that the .NET client is built against supported behavior rather than inferred payloads.

**Acceptance Criteria:**

- [ ] Obtain or document the authoritative endpoints and schemas for health, session event CRUD, session retrieval/deletion, long-term-memory search, and long-term-memory CRUD.
- [ ] Document authentication headers, supported identifier formats, payload limits, pagination, error envelopes, rate-limit headers, and retry guidance.
- [ ] Confirm how session ownership is established and whether it can be supplied explicitly.
- [ ] Confirm whether session-event writes support client event IDs or idempotency keys.
- [ ] Record API compatibility and versioning expectations.
- [ ] Capture unresolved contract questions as blocking issues with named owners.

#### US-002: Define the shared .NET client abstraction

**Epic:** 0B — Shared .NET client foundation  
**Description:** As an integration developer, I want a framework-neutral Iris client abstraction so that Agent Framework adapters do not own transport concerns.

**Acceptance Criteria:**

- [ ] Define async, cancellation-aware interfaces for all Milestone 1 operations.
- [ ] Keep Agent Framework types out of the client package.
- [ ] Use strongly typed request, response, filter, pagination, and error contracts.
- [ ] Define client ownership and disposal semantics.
- [ ] Define which types are public and which remain internal until the API stabilizes.
- [ ] Public APIs include XML documentation.
- [ ] Build and API-surface validation pass.

#### US-003: Implement the typed HTTP transport foundation

**Epic:** 0B — Shared .NET client foundation  
**Description:** As an application developer, I want a reusable `HttpClient`-based Iris client so that connections are pooled and requests follow normal .NET hosting practices.

**Acceptance Criteria:**

- [ ] Support caller-supplied `HttpClient` and `IHttpClientFactory` registration.
- [ ] Do not construct one HTTP client per agent invocation.
- [ ] Propagate cancellation tokens and use bounded request timeouts.
- [ ] Serialize contracts with `System.Text.Json` and trim-compatible metadata.
- [ ] Map non-success responses to typed exceptions without including credentials or response secrets.
- [ ] Unit tests cover serialization, cancellation, disposal, and representative error responses.
- [ ] Build, format, analyzers, and tests pass.

#### US-004: Define authentication and secret-handling hooks

**Epic:** 0C — Security and observability conventions  
**Description:** As a security engineer, I want credentials resolved through an explicit abstraction so that applications can use configuration, secret managers, and rotation without embedding keys in sessions.

**Acceptance Criteria:**

- [ ] Define a credential-provider abstraction that can return the current credential and a non-secret version identifier.
- [ ] Support a fixed API key as the simple configuration path.
- [ ] Never store credentials in `AgentSession.StateBag`, tool arguments, telemetry tags, exception messages, or caches.
- [ ] Apply bearer credentials only to the intended Iris origin.
- [ ] Define behavior for credential refresh and authentication failures.
- [ ] Tests prove credentials are redacted from logs and exception text.

#### US-005: Define the OpenTelemetry semantic conventions

**Epic:** 0C — Security and observability conventions  
**Description:** As an operator, I want stable telemetry names and attributes so that dashboards and alerts remain compatible across connector releases.

**Acceptance Criteria:**

- [ ] Define one `ActivitySource` and one `Meter` name with versioning guidance.
- [ ] Define spans for HTTP operations, memory recall, session load/store, MCP connection, MCP discovery, and MCP tool execution.
- [ ] Define low-cardinality metrics for operation count, error count, latency, retry count, result count, cache hit/miss, and active MCP connections.
- [ ] Define allowed attributes including operation, outcome, deployment mode, tool name, and non-sensitive access-profile identifier.
- [ ] Prohibit raw keys, authorization headers, prompts, memory text, tool arguments/results, session IDs, and owner IDs from default telemetry.
- [ ] Define an explicit sensitive-telemetry opt-in and redaction hook.
- [ ] Review conventions against current OpenTelemetry HTTP and generative-AI guidance.

#### US-006: Define identity normalization and tenancy rules

**Epic:** 0C — Security and observability conventions  
**Description:** As a platform owner, I want deterministic mapping of application identities to Iris identifiers so that invalid IDs and tenant collisions cannot cause leakage.

**Acceptance Criteria:**

- [ ] Define distinct types or clearly named values for store, tenant, owner, actor, session, agent, namespace, and access profile.
- [ ] Implement a deterministic codec producing identifiers accepted by Iris and no longer than 64 characters.
- [ ] Include a version prefix so the codec can evolve safely.
- [ ] Test determinism, allowed characters, length bounds, and representative collision resistance.
- [ ] Require authorization before accepting caller-controlled store or tenant selection.
- [ ] Document that storage normalization is not a substitute for application authorization.

#### US-007: Create the managed-service Docker integration environment

**Epic:** 0D — Managed-service local integration environment
**Description:** As a connector developer, I want both Iris products hosted as managed services while repository-owned dependencies run in Docker so that local testing is production-representative without requiring local Kubernetes.

**Acceptance Criteria:**

- [ ] Add Docker Compose profiles named `managed`, `offline`, and `observability`.
- [ ] The `managed` profile runs the sample/test application and local observability stack while connecting to managed Agent Memory and Context Retriever.
- [ ] The `offline` profile replaces managed endpoints with an Agent Memory REST fake and a local MCP fake.
- [ ] The `observability` profile runs the OTel Collector, Prometheus, Tempo, and Grafana without requiring the application container.
- [ ] Add `.env.example` with variable names and safe placeholder values but no secrets.
- [ ] Validate required managed-profile configuration before tests begin.
- [ ] Document that self-managed Iris and local Kubernetes are not default developer prerequisites.
- [ ] Docker configuration validates and all containers report healthy.

#### US-008: Seed and clean managed synthetic test data

**Epic:** 0D — Managed-service local integration environment
**Description:** As a test maintainer, I want repeatable synthetic data setup and cleanup so that managed integration tests are isolated and cannot affect customer or production data.

**Acceptance Criteria:**

- [ ] Seed a documented synthetic Context Retriever entity model, records, access tags, and least-privilege agent keys.
- [ ] Generate a unique run prefix for owners, sessions, namespaces, and seeded entity identifiers.
- [ ] Provide reset tooling that deletes only data associated with the current run prefix.
- [ ] Clean both session and long-term Agent Memory data; do not assume session deletion removes promoted LTM.
- [ ] Cleanup executes after success, failure, and CI cancellation where supported.
- [ ] Forked or untrusted pull requests cannot access managed-service credentials.
- [ ] Tests prove concurrent run prefixes cannot read or delete each other's data.

#### US-009: Provide one-command local environment lifecycle

**Epic:** 0D — Managed-service local integration environment
**Description:** As a developer, I want one command surface for setup, testing, inspection, reset, and teardown so that the integration environment is reproducible.

**Acceptance Criteria:**

- [ ] Provide commands for `up managed`, `up offline`, `up observability`, `seed`, `test`, `reset`, and `down`.
- [ ] Commands fail with actionable messages when Docker, configuration, network access, or credentials are unavailable.
- [ ] `down` stops local containers without deleting managed data implicitly.
- [ ] `reset` requires a resolved test-run prefix and refuses an empty or broad deletion scope.
- [ ] The README links to the complete local-development guide.
- [ ] CI invokes the same commands developers use locally.

### Milestone 1: .NET Agent Memory MVP

#### US-101: Implement Agent Memory session operations

**Epic:** 1A — Session history provider  
**Description:** As a provider developer, I want typed session event operations so that chat history can be stored and reconstructed.

**Acceptance Criteria:**

- [ ] Implement add, retrieve-one, retrieve-session, delete-one, and delete-session operations.
- [ ] Preserve client-created and server-ingested timestamps.
- [ ] Return events in the order guaranteed by the service contract.
- [ ] Validate role, text content, metadata size, and identifiers before sending requests.
- [ ] Contract tests cover success, empty sessions, invalid input, not found, unauthorized, forbidden, throttled, and server errors.

#### US-102: Map Agent Framework messages to Iris events

**Epic:** 1A — Session history provider  
**Description:** As an agent developer, I want predictable message mapping so that supported chat history round-trips without corrupting roles or ownership.

**Acceptance Criteria:**

- [ ] Map user, assistant, and opted-in system text messages to the corresponding Iris roles.
- [ ] Skip empty, tool, function, image, audio, and other unsupported content by default.
- [ ] Provide an application callback for projecting unsupported content to text.
- [ ] Preserve safe provenance metadata without exceeding service limits.
- [ ] Reject or safely handle a first event that would establish the wrong session owner.
- [ ] Unit tests cover all supported and unsupported content types.

#### US-103: Implement `RedisIrisChatHistoryProvider`

**Epic:** 1A — Session history provider  
**Description:** As an Agent Framework developer, I want Iris session memory exposed as a `ChatHistoryProvider` so that conversations survive process restarts.

**Acceptance Criteria:**

- [ ] Derive from the current Agent Framework `ChatHistoryProvider` abstraction.
- [ ] Load chronological history before model invocation.
- [ ] Store only new external request and response messages after a successful invocation.
- [ ] Never re-store loaded history or messages injected by context providers.
- [ ] Store session-specific identifiers only through `ProviderSessionState<TState>`.
- [ ] Support a maximum returned-message count or reducer hook for model-context limits.
- [ ] Default history-load and history-write failures to fail closed.
- [ ] Unit tests include session serialization and concurrent independent sessions.

#### US-104: Enforce a single session-event writer

**Epic:** 1A — Session history provider  
**Description:** As an application developer, I want safe composition of Iris providers so that installing history and long-term recall never duplicates events.

**Acceptance Criteria:**

- [ ] Define one component as the designated event writer for each configured agent.
- [ ] The standard registration helper rejects or prevents configurations with multiple writers.
- [ ] Direct construction documents how to disable event capture in the memory provider.
- [ ] Tests verify exactly one Iris event per logical message when both providers are installed.
- [ ] Tests verify standalone memory-provider mode can capture events when no Iris history provider is used.

#### US-105: Implement owner-scoped long-term-memory search

**Epic:** 1B — Long-term memory context provider  
**Description:** As an agent user, I want relevant memories from earlier sessions so that the agent can personalize its response without exposing another user's memories.

**Acceptance Criteria:**

- [ ] Build search text from filtered external input, defaulting to the latest non-empty user message.
- [ ] Require an owner equality filter for the standard per-user API.
- [ ] Allow additional namespace, topic, memory-type, timestamp, and threshold filters without allowing them to remove or replace the owner filter.
- [ ] Apply filtering on the server rather than filtering a broad result set locally.
- [ ] Support result limits and opaque pagination tokens in the client API.
- [ ] Tests demonstrate that different owners cannot retrieve each other's memories through the provider.

#### US-106: Implement `RedisIrisMemoryProvider`

**Epic:** 1B — Long-term memory context provider  
**Description:** As an Agent Framework developer, I want long-term memories supplied through `MessageAIContextProvider` so that recall participates in the standard context pipeline.

**Acceptance Criteria:**

- [ ] Derive from the current Agent Framework `MessageAIContextProvider` abstraction.
- [ ] Search before invocation and inject one bounded, clearly delimited context message.
- [ ] Label recalled material as untrusted remembered context rather than instructions.
- [ ] Do not inject recalled material with the system role.
- [ ] Attach non-sensitive provenance metadata identifying the provider and memory IDs.
- [ ] Cap result count and aggregate context size.
- [ ] Default recall failures to fail open with a redacted warning.
- [ ] Allow applications to select fail-open or fail-closed behavior.

#### US-107: Add provider registration and options validation

**Epic:** 1B — Long-term memory context provider  
**Description:** As an application developer, I want one idiomatic registration path so that the history and memory providers are configured consistently.

**Acceptance Criteria:**

- [ ] Provide dependency-injection registration for the client and both providers.
- [ ] Provide explicit delegates for tenant, owner, session, actor, agent, namespace, and topic resolution.
- [ ] Validate required endpoint, store, credential, identity, threshold, and result-limit options during startup where possible.
- [ ] Standard registration enables exactly one event writer.
- [ ] Options support independent load, recall, and write failure policies.
- [ ] Configuration errors identify the invalid setting without revealing secret values.

#### US-108: Enforce Agent Memory authorization boundaries

**Epic:** 1C — Agent Memory authentication and authorization  
**Description:** As a security engineer, I want all store and owner selection authorized by the host application so that a malicious caller cannot switch scopes through session input.

**Acceptance Criteria:**

- [ ] Identity resolvers receive trusted invocation/session context rather than raw model-generated arguments.
- [ ] The connector fails closed when tenant, store, owner, or session identity cannot be resolved.
- [ ] A caller cannot override the mandatory owner filter through optional filter configuration.
- [ ] Cross-owner and cross-store administrative operations use separately named APIs and require an explicit authorization callback.
- [ ] Tests attempt store, owner, namespace, and filter substitution attacks.
- [ ] Security documentation includes confused-deputy and indirect-prompt-injection risks.

#### US-109: Instrument Agent Memory tracing

**Epic:** 1D — Agent Memory telemetry and diagnostics  
**Description:** As an operator, I want distributed traces for Agent Memory operations so that latency and failures can be correlated with an agent run.

**Acceptance Criteria:**

- [ ] Create child spans under the active Agent Framework invocation for session load, session write, and LTM search.
- [ ] Propagate W3C trace context through HTTP automatically.
- [ ] Record operation, outcome, server address, result count, retry count, and status code using approved attributes.
- [ ] Mark errors according to OpenTelemetry conventions without recording sensitive response bodies.
- [ ] Tests use an in-memory listener/exporter to verify span parenting and redaction.

#### US-110: Instrument Agent Memory metrics

**Epic:** 1D — Agent Memory telemetry and diagnostics  
**Description:** As an operator, I want low-cardinality metrics so that I can monitor connector health without creating an unbounded telemetry bill.

**Acceptance Criteria:**

- [ ] Record operation duration, request count, error count, retry count, recall result count, and skipped-message count.
- [ ] Separate session load, session write, LTM search, and explicit LTM operations using bounded attributes.
- [ ] Do not use owner, session, memory, raw endpoint, or query values as metric dimensions.
- [ ] Document metric units and attribute cardinality.
- [ ] Tests verify emitted measurements and the absence of prohibited attributes.

#### US-111: Provide a runnable .NET Agent Memory sample

**Epic:** 1E — MVP documentation and release readiness  
**Description:** As a developer evaluating the connector, I want a concise sample so that I can run a persistent, cross-session agent safely.

**Acceptance Criteria:**

- [ ] Demonstrate environment-based endpoint, store ID, and API-key configuration.
- [ ] Demonstrate explicit owner and session resolution.
- [ ] Demonstrate session resume after recreating the application agent.
- [ ] Demonstrate cross-session long-term recall while explaining promotion delay.
- [ ] Include OpenTelemetry console or test exporter setup.
- [ ] Do not contain embedded secrets or recommend owner-less searches.

#### US-112: Establish the .NET MVP quality gate

**Epic:** 1E — MVP documentation and release readiness  
**Description:** As a maintainer, I want a repeatable quality gate so that releases meet Redis and Agent Framework contribution standards.

**Acceptance Criteria:**

- [ ] Unit, contract, and opt-in live-test suites are documented and runnable independently.
- [ ] Live tests skip cleanly when required environment variables are absent.
- [ ] Formatting, analyzers, nullable analysis, API compatibility, package validation, and tests pass in CI.
- [ ] Public C# files use required headers and XML documentation.
- [ ] A supported Agent Framework version matrix is documented.
- [ ] Package contents contain no test credentials or captured customer data.

### Milestone 2: Agent Memory production hardening

#### US-201: Configure session and long-term retention expectations

**Epic:** 2A — Retention and privacy lifecycle  
**Description:** As a data owner, I want retention behavior visible and configurable so that stored conversation data follows organizational policy.

**Acceptance Criteria:**

- [ ] Document which TTLs are store-managed and which can be controlled by the connector or API.
- [ ] Expose supported TTL inputs without claiming the connector can override store policy.
- [ ] Surface effective or configured retention information where the production API provides it.
- [ ] Warn or fail validation for unsupported retention combinations.
- [ ] Include session-memory and long-term-memory retention in the operational runbook.

#### US-202: Implement explicit session deletion

**Epic:** 2A — Retention and privacy lifecycle  
**Description:** As an application operator, I want to delete a conversation session so that expired or user-requested session data is removed.

**Acceptance Criteria:**

- [ ] Expose delete-one-event and delete-session operations through the .NET client.
- [ ] Require the same tenant/store/session authorization used for reads.
- [ ] Treat not-found deletion according to a documented idempotent contract.
- [ ] Emit redacted audit and telemetry events for deletion outcome.
- [ ] Document that deleting session memory does not delete promoted long-term memories.

#### US-203: Implement privacy deletion across memory tiers

**Epic:** 2A — Retention and privacy lifecycle  
**Description:** As a privacy administrator, I want an authorized deletion workflow covering session and long-term memory so that a user-erasure request is complete and auditable.

**Acceptance Criteria:**

- [ ] Provide an application-facing workflow or documented primitives to enumerate and delete authorized owner-scoped LTM records.
- [ ] Require a distinct administrative authorization callback.
- [ ] Handle pagination until the authorized scope is exhausted.
- [ ] Report partial success without treating successfully deleted items as failures.
- [ ] Produce a completion result containing counts and non-sensitive failure identifiers.
- [ ] Tests cover interrupted, repeated, partial, and unauthorized deletion attempts.

#### US-204: Add retry and timeout policies

**Epic:** 2B — Resilience and idempotency  
**Description:** As an operator, I want bounded retries so that transient Iris failures recover without multiplying side effects or exhausting request budgets.

**Acceptance Criteria:**

- [ ] Retry only operations and failure classes confirmed safe by the production contract.
- [ ] Honor server retry guidance such as `Retry-After` when present.
- [ ] Use exponential backoff with jitter and a maximum attempt/time budget.
- [ ] Do not retry caller cancellation, validation errors, authentication failures, or authorization failures.
- [ ] Emit retry metrics and span events without logging secrets or bodies.
- [ ] Tests use deterministic time/backoff controls.

#### US-205: Protect session appends from ambiguous retries

**Epic:** 2B — Resilience and idempotency  
**Description:** As an agent user, I want transient network failures handled without duplicated conversation turns.

**Acceptance Criteria:**

- [ ] Use the service's client event ID or idempotency mechanism if supported.
- [ ] If unsupported, do not automatically retry an append after an ambiguous post-send failure.
- [ ] Return a typed unknown-outcome error that applications can reconcile.
- [ ] Document recovery and reconciliation behavior.
- [ ] Tests cover failures before send, after send, before response, and after response.

#### US-206: Implement deterministic bulk long-term writes

**Epic:** 2C — Explicit long-term-memory operations  
**Description:** As an application developer, I want to write known facts directly so that structured facts do not require promotion.

**Acceptance Criteria:**

- [ ] Support batches of 1–100 records.
- [ ] Require or generate deterministic, valid IDs for retry safety.
- [ ] Support semantic, episodic, and message memory types plus owner, session, namespace, and topics.
- [ ] Return created records and per-record failures separately.
- [ ] Retry only failed IDs when policy permits.
- [ ] Tests cover validation, partial success, repeated requests, and deterministic IDs.

#### US-207: Implement long-term update and delete operations

**Epic:** 2C — Explicit long-term-memory operations  
**Description:** As an application developer, I want to correct and remove memories so that stale or inaccurate context does not persist.

**Acceptance Criteria:**

- [ ] Support authorized update, retrieve, and delete operations by memory ID.
- [ ] Preserve the distinction between omitted fields and explicitly cleared fields.
- [ ] Enforce owner/store authorization before mutation.
- [ ] Emit redacted traces, metrics, and logs.
- [ ] Tests cover successful mutation, not found, forbidden, validation, and repeated deletion.

### Milestone 3: .NET Context Retriever over local MCP

#### US-301: Implement the shared Context Retriever local MCP client

**Epic:** 3A — Shared local MCP client  
**Description:** As an integration developer, I want one local MCP client layer so that direct attachment and the context provider share protocol behavior.

**Acceptance Criteria:**

- [ ] Use the official MCP C# SDK and expose discovered tools as `Microsoft.Extensions.AI` tools.
- [ ] Support Context Retriever endpoint and agent-key authentication.
- [ ] Define connection creation, reuse, reconnect, and asynchronous disposal behavior.
- [ ] Do not implement hosted MCP in this milestone.
- [ ] Keep Agent Framework provider types out of the low-level MCP client package.
- [ ] Contract tests cover initialization, tool listing, execution, cancellation, protocol errors, and reconnect behavior.

#### US-302: Resolve per-invocation Context Retriever access

**Epic:** 3B — Recommended context-provider integration  
**Description:** As a multi-tenant application developer, I want an access resolver called for each invocation so that Context Retriever uses the current caller's authorized profile.

**Acceptance Criteria:**

- [ ] Define a resolver that returns endpoint identity, a non-secret access-profile ID, and a credential provider.
- [ ] Resolve access from trusted application/session context, never model-generated tool input.
- [ ] Fail closed when access cannot be authenticated or authorized.
- [ ] Never store agent keys in Agent Framework session state.
- [ ] Allow short-lived credentials and credential-version changes.
- [ ] Tests cover tenant changes, expired credentials, denied access, and resolver cancellation.

#### US-303: Isolate and cache MCP clients by security principal

**Epic:** 3B — Recommended context-provider integration  
**Description:** As a security engineer, I want connection reuse bounded by principal so that one tenant cannot inherit another tenant's tools or protocol state.

**Acceptance Criteria:**

- [ ] Cache keys use endpoint, non-secret access profile, and credential version.
- [ ] Never include secret credential material in cache keys or diagnostics.
- [ ] Do not reuse MCP protocol sessions, cookies, discovered tools, or authorization state across access profiles.
- [ ] Bound cache size and idle lifetime.
- [ ] Dispose evicted and shutdown clients asynchronously.
- [ ] Concurrent initialization for the same profile is deduplicated.
- [ ] Tests attempt cross-profile reuse and race conditions.

#### US-304: Implement `RedisContextRetrieverProvider`

**Epic:** 3B — Recommended context-provider integration  
**Description:** As an Agent Framework developer, I want Context Retriever tools supplied by an `AIContextProvider` so that tool access follows the caller automatically.

**Acceptance Criteria:**

- [ ] Derive from the current Agent Framework `AIContextProvider` abstraction.
- [ ] Resolve the current access profile before every invocation.
- [ ] Obtain the principal-isolated local MCP tool set and return it through `AIContext.Tools`.
- [ ] Attribute tools to the provider without altering server-defined schemas or descriptions.
- [ ] Fail closed on authentication, authorization, or tool-discovery failure.
- [ ] Support cancellation and deterministic client cleanup.
- [ ] Tests verify different access profiles receive only their authorized tool sets.

#### US-305: Implement fixed-identity direct tool attachment

**Epic:** 3C — Fixed-identity direct attachment  
**Description:** As a single-identity application developer, I want a concise helper for attaching Context Retriever tools so that I do not need a dynamic provider.

**Acceptance Criteria:**

- [ ] Provide a helper that connects, discovers, and returns local MCP tools for one fixed access profile.
- [ ] Use the same client factory, authentication, tool conversion, telemetry, and error types as the context provider.
- [ ] Clearly document that all users of the agent receive the fixed profile's authorization scope.
- [ ] Require the returned client/lease to have explicit lifetime ownership.
- [ ] Do not create a second MCP protocol implementation.
- [ ] Include a fixed-service-identity sample.

#### US-306: Provide one ergonomic Context Retriever registration boundary

**Epic:** 3C — Fixed-identity direct attachment  
**Description:** As an application developer, I want one configuration model so that choosing fixed or dynamic identity does not require learning unrelated APIs.

**Acceptance Criteria:**

- [ ] Share endpoint, credentials, connection policy, telemetry, and redaction options between both entry points.
- [ ] Recommend the context provider in primary documentation.
- [ ] Present direct attachment as a fixed-service-identity convenience.
- [ ] Reject ambiguous configuration that enables both paths for the same agent unintentionally.
- [ ] Provide a migration example from fixed attachment to per-invocation access.

#### US-307: Enforce Context Retriever authentication and authorization

**Epic:** 3D — Context Retriever security and observability  
**Description:** As a security engineer, I want the connector to preserve Context Retriever's server-side access model so that access tags cannot be bypassed locally.

**Acceptance Criteria:**

- [ ] Treat the agent key as the server-enforced identity and access-tag boundary.
- [ ] Do not add client-side filters as a replacement for server authorization.
- [ ] Do not accept credentials or access-profile changes from model tool arguments.
- [ ] Reject cross-origin credential forwarding and unauthorized redirects.
- [ ] Document least-privilege agent-key creation and rotation.
- [ ] Security tests cover confused-deputy, cross-tenant cache, redirect, logging, and tool-injection scenarios.

#### US-308: Instrument Context Retriever MCP tracing

**Epic:** 3D — Context Retriever security and observability  
**Description:** As an operator, I want traces across MCP discovery and execution so that Context Retriever latency can be correlated with model tool calls.

**Acceptance Criteria:**

- [ ] Create spans for connect, initialize, discover tools, and execute tool.
- [ ] Preserve parentage under the Agent Framework invocation and function-call spans where available.
- [ ] Record only approved low-cardinality attributes.
- [ ] Exclude keys, headers, arguments, results, and customer identifiers by default.
- [ ] Record authorization outcome without recording authorization data.
- [ ] Tests verify span hierarchy, error status, and redaction.

#### US-309: Instrument Context Retriever MCP metrics

**Epic:** 3D — Context Retriever security and observability  
**Description:** As an operator, I want MCP health metrics so that I can detect discovery failures, slow tools, and connection churn.

**Acceptance Criteria:**

- [ ] Record connection attempts/failures, discovery duration/failures, tool-call duration/failures, active connections, reconnects, and client-cache hit/miss.
- [ ] Use tool name only when bounded by the deployed Context Retriever schema; document cardinality implications.
- [ ] Never use tenant, session, raw endpoint, arguments, or results as metric dimensions.
- [ ] Provide example alert conditions without prescribing vendor-specific dashboards.
- [ ] Tests verify measurements and prohibited-attribute absence.

#### US-310: Add local MCP operational controls

**Epic:** 3D — Context Retriever security and observability  
**Description:** As an operator, I want bounded MCP execution so that unavailable or slow tools cannot exhaust the agent's resources.

**Acceptance Criteria:**

- [ ] Configure connection, discovery, and execution timeouts separately.
- [ ] Bound concurrent initialization and tool execution per access profile.
- [ ] Define safe reconnect behavior that does not duplicate ambiguous side-effecting calls.
- [ ] Support host shutdown cancellation and asynchronous cleanup.
- [ ] Emit health information without performing customer-data queries.
- [ ] Tests cover timeout, cancellation, reconnect, shutdown, and concurrency limits.

### Milestone 4: Python parity and upstream readiness

#### US-401: Implement Python Agent Memory history provider

**Epic:** 4A — Python Agent Memory adapters  
**Description:** As a Python Agent Framework developer, I want Iris session memory exposed as a `HistoryProvider` so that behavior matches .NET.

**Acceptance Criteria:**

- [ ] Use the official asynchronous `redis-agent-memory` SDK.
- [ ] Implement `get_messages` and `save_messages` with the same mapping and single-writer rules as .NET.
- [ ] Keep secrets out of provider-scoped session state.
- [ ] Match .NET identity normalization and failure defaults.
- [ ] Pass Python lint, type-check, unit, and integration tests.

#### US-402: Implement Python long-term memory context provider

**Epic:** 4A — Python Agent Memory adapters  
**Description:** As a Python Agent Framework developer, I want owner-scoped long-term recall so that Python agents have behavioral parity with .NET.

**Acceptance Criteria:**

- [ ] Implement an Agent Framework `ContextProvider` using the official async SDK.
- [ ] Require the owner filter and preserve namespace/topic/type filtering rules.
- [ ] Match .NET context formatting, bounding, provenance, and fail-open defaults.
- [ ] Emit equivalent OpenTelemetry operations and privacy defaults where supported.
- [ ] Share language-neutral contract fixtures for mapping and failure cases.

#### US-403: Implement Python Context Retriever integration

**Epic:** 4B — Python Context Retriever adapters  
**Description:** As a Python developer, I want the same fixed and per-invocation local MCP paths so that application architecture is portable across languages.

**Acceptance Criteria:**

- [ ] Provide a recommended dynamic context provider and fixed-identity convenience path.
- [ ] Share one local MCP client/factory implementation underneath both paths.
- [ ] Preserve principal-isolated connection and tool caching.
- [ ] Match .NET authentication, authorization, timeout, telemetry, and redaction semantics.
- [ ] Pass cross-profile isolation and contract tests.

#### US-404: Publish a cross-language behavioral contract

**Epic:** 4C — Cross-language contract and upstream preparation  
**Description:** As a maintainer, I want language-neutral expected behavior so that .NET and Python integrations do not drift.

**Acceptance Criteria:**

- [ ] Define shared fixtures for role mapping, identity normalization, filters, context formatting, error classification, and deletion outcomes.
- [ ] Run applicable fixtures in both .NET and Python CI.
- [ ] Document intentional language-specific differences.
- [ ] Version the fixture format and compatibility policy.

#### US-405: Prepare the Agent Framework upstream contribution

**Epic:** 4C — Cross-language contract and upstream preparation  
**Description:** As a Redis maintainer, I want the connector organized for upstream review so that Microsoft can adopt it with minimal redesign.

**Acceptance Criteria:**

- [ ] Align naming, package layout, public API, tests, samples, and documentation with current Agent Framework conventions.
- [ ] Separate Redis-owned generic clients from Agent Framework-specific adapters.
- [ ] Document dependency ownership, release cadence, support policy, and compatibility expectations.
- [ ] Complete security, privacy, observability, and API-surface reviews.
- [ ] Prepare an upstream proposal describing design choices and alternatives.

## 5. Functional Requirements

- **FR-1:** The .NET integration must provide a session-memory-backed `ChatHistoryProvider`.
- **FR-2:** The .NET integration must provide a long-term-memory-backed `MessageAIContextProvider`.
- **FR-3:** The standard per-user recall path must require an owner equality filter that optional filters cannot override.
- **FR-4:** The integration must store session-specific state in Agent Framework session state, never in shared provider instance fields.
- **FR-5:** The standard registration path must permit exactly one Iris session-event writer.
- **FR-6:** The integration must not persist loaded history or context-provider-injected messages as new conversation events.
- **FR-7:** The integration must support fixed and refreshable credentials through a credential-provider abstraction.
- **FR-8:** Secrets must not appear in session state, tool arguments, logs, metrics, traces, exceptions, or cache keys.
- **FR-9:** Store, tenant, owner, session, and access-profile resolution must be host-controlled and authorization-aware.
- **FR-10:** Agent Memory recall may fail open when configured; history durability and authorization failures must fail closed by default.
- **FR-11:** Context Retriever must use local MCP for the initial implementation.
- **FR-12:** Context Retriever must have one shared MCP client layer used by both dynamic context-provider and fixed-identity entry points.
- **FR-13:** The dynamic Context Retriever provider must resolve access for every invocation and isolate connections, discovery, and protocol state by security principal.
- **FR-14:** Context Retriever authentication or authorization failure must fail the affected invocation closed.
- **FR-15:** The connector must preserve Context Retriever's server-side access-tag enforcement and must not replace it with client-only filtering.
- **FR-16:** All network and provider operations must accept and propagate cancellation.
- **FR-17:** The integration must emit OpenTelemetry-compatible traces and metrics using documented low-cardinality attributes.
- **FR-18:** Sensitive content collection must be disabled by default and require explicit opt-in.
- **FR-19:** Retention documentation must distinguish session TTL, long-term-memory TTL, connector behavior, and store policy.
- **FR-20:** Session deletion must not be represented as deleting promoted long-term memories.
- **FR-21:** Privacy deletion across memory tiers must require explicit administrative authorization and report partial completion.
- **FR-22:** Retries must be bounded and limited to operations whose idempotency is established.
- **FR-23:** Promotion must be treated as eventually consistent; request paths and ordinary tests must not assume immediate LTM availability.
- **FR-24:** Direct LTM bulk writes must use deterministic IDs and handle partial success.
- **FR-25:** .NET and Python must share language-neutral behavioral fixtures once Python work begins.

## 6. Non-Goals

- Go connector.
- Hosted MCP integration in the initial Context Retriever milestone.
- A generic `Microsoft.Extensions.VectorData` adapter for Agent Memory.
- Workflow checkpoint or workflow-state persistence.
- Model-callable remember, forget, or privacy-deletion tools in the initial releases.
- Client-side recreation of Context Retriever's authorization or row filtering.
- Provisioning Redis Cloud Agent Memory or Context Retriever services.
- Building the Context Retriever schema-modeling administration client.
- Redis Data Integration pipeline management.
- LangCache integration; it should be planned as a separate capability.
- Guarantees that Iris background promotion is synchronously visible.
- Automatic conversion of arbitrary multimodal and tool content into session text.

## 7. Design Considerations

### One implementation, two Context Retriever entry points

The user-facing distinction should be small:

- `AddRedisContextRetrieverProvider(...)` is the recommended path. It resolves an authorized access profile for each invocation.
- `GetRedisContextRetrieverToolsAsync(...)` is the fixed-identity convenience. It returns tools and a lifetime owner/lease.

Both must delegate to the same client factory and tool conversion layer. Documentation should teach the provider first and introduce direct attachment only for applications where every caller legitimately shares one Context Retriever access profile.

### Context trust levels

- Chat history represents prior messages in the current conversation.
- Agent Memory LTM represents remembered, potentially stale or adversarial text and must be labeled as untrusted context.
- Context Retriever results represent live governed business data, but tool output still must not be interpreted as higher-priority instructions.

### Failure defaults

| Operation | Default behavior | Reason |
|---|---|---|
| LTM recall | Fail open | Optional personalization should not usually take down an agent |
| Session-history load | Fail closed | Continuing silently changes conversation semantics |
| Session-history write | Fail closed | The application must not falsely claim durability |
| Identity resolution | Fail closed | Scope is a security boundary |
| Context Retriever authentication/authorization | Fail closed | Tools must not fall back to broader access |
| Context Retriever discovery | Fail closed | The agent otherwise has an unexpected capability set |
| Promotion delay | Continue | Promotion is asynchronous by design |

## 8. Technical Considerations

- Target .NET 8 or the minimum supported version agreed with current Agent Framework maintainers.
- Use `HttpClient` and the official MCP C# SDK rather than implementing protocol transports from scratch.
- Prefer Redis-owned framework-neutral client packages and separate Agent Framework adapter packages.
- Use `ProviderSessionState<TState>` for serializable per-session identifiers.
- Use `ActivitySource`, `Meter`, `ILogger`, and established .NET redaction abstractions.
- Avoid high-cardinality metric dimensions. IDs belong in carefully controlled logs or trace baggage only when explicitly enabled and policy-approved.
- Context Retriever MCP client pooling must account for protocol session state and any HTTP cookie behavior, not only authorization headers.
- Tool discovery may differ by agent key/access tags; cached tool sets are part of the authorization boundary.
- Any retry around tool calls must distinguish known read-only/idempotent calls from calls with unknown side effects. Do not automatically replay an ambiguous tool invocation.
- The live test suite requires separately provisioned test services and synthetic data. It must never run against customer stores.

## 9. Success Metrics

- A .NET agent can resume an Iris-backed session after process restart.
- A new session for the same owner can recall an authorized fact promoted from an earlier session.
- Automated isolation tests produce zero cross-owner, cross-store, or cross-access-profile results.
- Installing both Agent Memory providers creates exactly one stored event per logical message.
- All network operations appear as correctly parented spans when an OpenTelemetry listener is enabled.
- Default telemetry contains no API keys, authorization headers, raw prompts, raw memory content, raw tool arguments/results, owner IDs, or session IDs.
- Metrics use bounded dimensions verified by automated tests.
- Credential rotation succeeds without application restart where a refreshable credential provider is configured.
- Repeated authorized deletion workflows converge without recreating or double-counting deleted data.
- The recommended Context Retriever provider and fixed-identity helper pass the same MCP contract suite.
- Samples can be completed from documented environment variables without modifying connector source.
- .NET release quality gates pass against every supported Agent Framework version.

## 10. Open Questions

1. What is the authoritative, versioned production OpenAPI contract for Redis Agent Memory?
2. Can Agent Memory session ownership be explicitly supplied, or must the connector control the first event actor?
3. What idempotency primitive is available for session-event append?
4. Which Agent Memory retention settings are API-readable and API-writable after store creation?
5. Can LTM records always be enumerated reliably by owner and originating session for privacy deletion?
6. What authentication header and rotation semantics does the managed Context Retriever MCP endpoint guarantee?
7. Are Context Retriever tool listings guaranteed to be stable for a given agent-key access profile, and how are schema changes signaled?
8. Does the Context Retriever MCP transport use cookies or server-side session state that further constrains client pooling?
9. Which Context Retriever generated tools are guaranteed read-only, and how are side-effecting tools identified for retry and approval policy?
10. Should Redis publish the framework-neutral .NET Agent Memory and Context Retriever clients as independent supported packages?
11. Which package names and namespaces are acceptable for eventual Microsoft Agent Framework upstream contribution?
12. Which current OpenTelemetry generative-AI semantic conventions should be adopted versus isolated behind connector-owned attributes while the conventions evolve?
