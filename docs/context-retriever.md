# Redis Context Retriever for Microsoft Agent Framework

Use `RedisContextRetrieverProvider` for most applications. Its access resolver runs for every invocation, so the current trusted application identity selects a server-enforced agent-key scope. Credentials are never read from model tool arguments or stored in Agent Framework session state.

The fixed helper is only appropriate when every caller may share one service identity and its complete Context Retriever authorization scope. Keep the returned `ContextRetrieverToolLease` alive for as long as its tools are attached.

## Migrating from fixed to per-invocation identity

Keep the shared `RedisContextRetrieverOptions` and client factory/cache. Replace `FixedAccessProfile` with an `AccessResolver` that maps trusted request or session context to `ContextRetrieverAccessProfile`. Then attach the resulting `RedisContextRetrieverProvider` instead of the fixed lease's tools. Do not place agent keys or access-profile choices in tool arguments.
