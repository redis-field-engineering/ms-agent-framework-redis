# Preview operational contract

This contract defines the scope of `0.1.0-preview.1`. It is a preview integration, not a general-availability promise of exactly-once persistence, complete erasure, or parity with every Redis service API. Verify that your service deployment supports the APIs and identity mode your application uses.

## Supported surface

Agent Memory supports appending and reading session events, reading summaries, session/event deletion, owner-scoped session listing, filtered long-term-memory search, and bulk deletion by known memory IDs. Agent Framework integration supplies text history and untrusted recalled context. It does not expose direct LTM create/update, namespace administration, store provisioning, extraction configuration, or a complete owner-erasure operation.

The default message mapper stores text-only user and assistant messages. System messages are opt-in. Tool calls/results, images, audio, and mixed-content messages on supported roles are skipped unless an explicit text projection is configured. Unsupported roles are always skipped before content projection. It cannot reconstruct a full tool or multimodal transcript. Observe skipped-message metrics before relying on durable reconstruction.

Context Retriever discovers and invokes server-defined MCP tools, binds trusted arguments, and supports fixed service identities or a per-invocation access resolver. The modeling package exports and compares DataModels; the optional `Redis.Iris.ContextRetriever.Management` package creates, reads, and updates surfaces directly over the administrative HTTP API. It is not a dependency of the runtime integration. Model comparison reports contract changes; it does not prove a migration safe for existing data or apply data migrations.

## Persistence and retry semantics

The connector performs one HTTP send per RAM operation and does not install a retry policy. A successful append returns a server event ID. There is no application idempotency key or exactly-once guarantee across retries, failures, process restarts, or concurrent invocations. The event writer coordinates the two providers in one normal invocation; it is not a transactional outbox.

| Outcome | Application action |
| --- | --- |
| Validation or host authorization rejection before sending | Correct input or authorization; nothing was sent by this operation. |
| `AgentMemoryException` | Inspect `StatusCode`, `Operation`, and optional `ErrorCode`. Error bodies are not exposed. A server error is not proof that a write did not commit. |
| `HttpRequestException`, timeout/cancellation, or invalid response after sending | Write outcome may be unknown. Reconcile against session events before deciding whether to retry. Cancellation is not rollback. |
| `BulkDeleteLongTermMemoriesResult` | Inspect every `Deleted` ID and `Errors` item. HTTP success can contain partial failures. |

Do not add blanket retries around append calls or MCP tools with side effects. Retrying reads can be appropriate within a bounded host deadline after classifying the failure; the library provides no automatic backoff, Retry-After handling, circuit breaker, or durable recovery queue. A cache acquisition rejected for active capacity has not invoked a tool; an interrupted tool invocation may already have taken effect.

## Timeouts, failure policies, and lifetime

RAM uses the caller's `HttpClient.Timeout` (100 seconds for the DI-created client unless the host configures it) plus per-call cancellation. Set the named client `Redis.Iris.AgentMemory` timeout through `AddHttpClient` and pass request/shutdown cancellation. Manually constructed clients require handlers with redirects and cookies disabled; see [transport requirements](../SECURITY.md).

History reads and writes default to fail closed; recall defaults to fail open. Fail-open recall may continue without memory and emits a warning. Fail-open writes may lose history. Host identity validation, authorization callbacks, and scope-binding failures outside the operation always propagate. Credential-provider exceptions and service authentication/authorization errors inside an operation follow its failure policy; use fail-closed policies when those errors must stop the invocation. A failed invocation does not roll back writes that already succeeded.

Context Retriever defaults to 15-second connection and discovery deadlines, a 30-second execution deadline, and eight concurrent tool executions per protocol session. Cache limits include connections still initializing or awaiting cleanup. Active operations hold leases; capacity saturation rejects a new profile. Dispose the cache asynchronously at host shutdown. A custom factory that ignores cancellation can delay shutdown; no forceful abort guarantee is provided for arbitrary implementations.

## Identity and retention

Use trusted current host identity for every invocation and a stable session binding. Restored serialized state never authorizes its caller. `TenantId` is a host authorization binding, not a RAM wire partition: use globally unique owners or separate namespace resources/stores to isolate tenants. On ownership-enforcing deployments, the delegated token subject determines personal ownership. Credential versions must change whenever the credentials change. Retained tools must not be transferred to another principal or stored beyond their authorization scope.

Session history, summaries, and LTM are separate records with service-controlled retention. The connector does not configure or promise TTLs, extraction timing, geographic placement, encryption at rest, backup expiry, or data-processing policy. Confirm these settings in the deployed service before storing customer data. Summaries and recalled facts may be stale, incorrect, or contain hostile instructions; the model must treat them as untrusted context.

## Deletion is scoped, not complete erasure

`DeleteSessionMemoryAsync` deletes session state, not promoted LTM. Deleting one event does not retract summaries or extracted facts. Bulk deletion removes only supplied LTM IDs, and background extraction may create records after deletion. Search pagination, ranking, backup retention, and in-flight work mean a one-pass search-and-delete is not proof of complete erasure.

The connector and samples do not provide a complete privacy-erasure workflow. Such a workflow must prevent new writes, quiesce/drain background processing, enumerate all relevant records and namespaces through supported administrative APIs, inspect partial deletion errors, verify absence, and apply the service's backup/retention policy. Until that is implemented and validated by the host, do not advertise a user-facing “delete all my data” guarantee based on these helpers. Use dedicated synthetic data and short retention for demos and live tests.
