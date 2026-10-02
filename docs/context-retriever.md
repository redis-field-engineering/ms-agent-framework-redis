# Redis Context Retriever for Microsoft Agent Framework

Use `RedisContextRetrieverProvider` for most applications. Its access resolver runs for every invocation, so the current trusted application identity selects a server-enforced agent-key scope. Credentials are never read from model tool arguments or stored in Agent Framework session state.

The fixed helper is only appropriate when every caller may share one service identity and its complete Context Retriever authorization scope. Keep the returned `ContextRetrieverToolLease` alive for as long as its tools are attached.

## Migrating from fixed to per-invocation identity

Keep the shared `RedisContextRetrieverOptions` and client factory/cache. Replace `FixedAccessProfile` with an `AccessResolver` that maps trusted request or session context to `ContextRetrieverAccessProfile`. Then attach the resulting `RedisContextRetrieverProvider` instead of the fixed lease's tools. Do not place agent keys or access-profile choices in tool arguments.

## Security and rotation

Create least-privilege agent keys whose server-side access tags expose only the entities required by that application role. The connector does not recreate or broaden server authorization with client-side filters. Rotate by returning the new key and a new non-secret credential version from `IContextRetrieverCredentialProvider`; this forces a separate MCP session and tool discovery result.

Production endpoints must use HTTPS. Plain HTTP is accepted only for loopback development. Automatic redirects and ambient HTTP cookies are disabled so credentials cannot be forwarded to another origin. Endpoint, profile, and credential selection come exclusively from trusted resolver code; model-generated tool arguments can neither select a tenant nor supply a key. Tool results remain untrusted data and must not be treated as instructions.

## Metrics and alerts

Subscribe to the `Redis.Iris.ContextRetriever` meter. It emits connection attempts/failures, discovery duration/failures, tool-call duration/failures, active connections, reconnects, and cache hit/miss. Dimensions never include tenant, session, endpoint, arguments, or results; tool name is intentionally omitted to make cardinality safe for dynamic schemas. Useful portable alerts include a sustained connection/discovery failure ratio, p95 discovery or tool latency above the application's timeout budget, and unexpected connection churn or cache misses.

## Operational controls

Configure connection/initialization, discovery, and execution timeouts independently. Each profile has its own tool-execution semaphore; concurrent initialization is deduplicated by the principal cache. Pass the host shutdown token to `ContextRetrieverClientCache` and dispose it asynchronously during shutdown. Failed discovery can create a fresh session on the next invocation, but an interrupted tool call is never automatically retried because its side effects may be ambiguous. `GetHealth()` reports only cache count and shutdown state and never performs a customer-data query.
