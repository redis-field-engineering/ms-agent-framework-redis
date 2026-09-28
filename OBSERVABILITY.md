# Observability

The connector emits OpenTelemetry-compatible activities and .NET metrics under `Redis.Iris.AgentMemory`.

Activities cover `session.load`, `session.write`, and `ltm.search`. Attributes are limited to operation, outcome, server address, HTTP status, result count, retry count, and error type. Response bodies, credentials, query text, identity values, store IDs, session IDs, and memory IDs are never recorded.

Metrics:

| Instrument | Type | Unit | Attributes |
| --- | --- | --- | --- |
| `redis.iris.requests` | Counter | `{request}` | `redis.iris.operation` |
| `redis.iris.errors` | Counter | `{error}` | `redis.iris.operation` |
| `redis.iris.retries` | Counter | `{retry}` | `redis.iris.operation` |
| `redis.iris.operation.duration` | Histogram | `ms` | operation, outcome |
| `redis.iris.recall.results` | Histogram | `{memory}` | operation |
| `redis.iris.messages.skipped` | Counter | `{message}` | bounded reason |

All metric attributes are bounded enums. Do not add endpoint, tenant, owner, session, actor, memory, query, or message content as metric dimensions.

`RedisIrisMemoryProvider` emits structured application logs for recall start and result count. Query text, identity values, memory IDs, source session IDs, and memory text are logged only when `RedisIrisMemoryProviderOptions.EnableSensitiveLogging` is explicitly enabled. This switch is intended for controlled development diagnostics and remains disabled by default. Sensitive recall fields are never added to traces or metrics.
