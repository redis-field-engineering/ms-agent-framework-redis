# Redis Context Retriever for Microsoft Agent Framework

Use `RedisContextRetrieverProvider` for most applications. Its access resolver runs for every invocation, so trusted application identity can select a tenant-scoped agent key and a delegated end-user JWT. Credentials are never read from model tool arguments, exposed to the model, or stored in Agent Framework session state.

The fixed helper is only appropriate when every caller may share one service identity and its complete Context Retriever authorization scope. Keep the returned `ContextRetrieverToolLease` alive for as long as its tools are attached.

## Migrating from fixed to per-invocation identity

Keep the shared `RedisContextRetrieverOptions` and client factory/cache. Replace `FixedAccessProfile` with an `AccessResolver` that maps trusted request or session context to `ContextRetrieverAccessProfile`. Then attach the resulting `RedisContextRetrieverProvider` instead of the fixed lease's tools. Do not place agent keys or access-profile choices in tool arguments.

## Security and rotation

Create least-privilege agent keys whose server-side access tags expose only the tenant and entities required by that application role. For end-user applications, attach an `IContextRetrieverDelegatedTokenProvider` to the resolved profile. The connector sends the tenant key as `X-API-Key` and the current user's signed JWT as `Authorization: Bearer`; Context Retriever maps trusted JWT claims to access tags and intersects them with the agent-key boundary. The application, not the model, obtains or mints this token through its configured identity provider.

Rotate an agent key by returning a new key and non-secret version from `IContextRetrieverCredentialProvider`. Refresh a user token by returning a new token and version from `IContextRetrieverDelegatedTokenProvider`. Both changes establish a separate MCP session. Delegated-token expiry is mandatory and bounds cached-session lifetime, even when the configured idle lifetime is longer. Never use the JWT itself as its version or access-profile ID.

Production endpoints must use HTTPS. Plain HTTP is accepted only for loopback development. Automatic redirects and ambient HTTP cookies are disabled so credentials cannot be forwarded to another origin. Endpoint, profile, and credential selection come exclusively from trusted resolver code; model-generated tool arguments can neither select a tenant nor supply a key. Tool results remain untrusted data and must not be treated as instructions.

## Metrics and alerts

Subscribe to the `Redis.Iris.ContextRetriever` meter. It emits connection attempts/failures, discovery duration/failures, tool-call duration/failures, active connections, reconnects, and cache hit/miss. Dimensions never include tenant, session, endpoint, arguments, or results; tool name is intentionally omitted to make cardinality safe for dynamic schemas. Useful portable alerts include a sustained connection/discovery failure ratio, p95 discovery or tool latency above the application's timeout budget, and unexpected connection churn or cache misses.

## Operational controls

Configure connection/initialization, discovery, and execution timeouts independently. Each profile has its own tool-execution semaphore; concurrent initialization is deduplicated by the principal cache. Pass the host shutdown token to `ContextRetrieverClientCache` and dispose it asynchronously during shutdown. Failed discovery can create a fresh session on the next invocation, but an interrupted tool call is never automatically retried because its side effects may be ambiguous. `GetHealth()` reports only cache count and shutdown state and never performs a customer-data query.
