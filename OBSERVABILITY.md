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

`RedisIrisMemoryProvider` emits one structured application log containing the number of records returned by each recall search. Query text, identity values, memory IDs, source session IDs, and memory text are not logged or added to traces or metrics.
