# Redis Iris Agent Memory demo

This sample follows Microsoft Agent Framework's canonical memory demonstration: Session A teaches an agent a user preference, the application agent is recreated and resumes the same durable session, and a brand-new Session B recalls the preference for the same owner.

Redis Agent Memory provides both Agent Framework extension points:

- `RedisIrisChatHistoryProvider` supplies durable, chronological session history.
- `RedisIrisMemoryProvider` supplies owner-scoped long-term memories as clearly labeled, untrusted user context.

## Configuration

The repository root `.env.local` already holds the Redis Cloud values. Add the model credential and, optionally, a stable demo owner:

```dotenv
REDIS_IRIS_ENDPOINT="https://..."
REDIS_IRIS_STORE_ID="..."
REDIS_IRIS_API_KEY="..."
OPENAI_API_KEY="..."
OPENAI_MODEL="gpt-4.1-mini"
SAMPLE_OWNER_ID="redis-iris-demo-shopper"
REDIS_IRIS_PROMOTION_TIMEOUT_MINUTES="8"
REDIS_IRIS_SHORT_TERM_WINDOW="4"
```

Do not commit `.env.local`. Use only a dedicated development store with synthetic data.

## Presentation-friendly flow

Teach the preference and demonstrate same-session durability:

```bash
./scripts/run-agent-memory-demo.sh teach
```

Redis Cloud promotes session events asynchronously. After at least one five-minute promotion window, demonstrate recall in a completely new session:

```bash
./scripts/run-agent-memory-demo.sh recall
```

For an unattended end-to-end run, including bounded polling for promotion:

```bash
./scripts/run-agent-memory-demo.sh full
```

The output labels user and assistant turns, promoted memory content, connector traces, and low-cardinality metrics. The owner scope comes from trusted application configuration rather than model-generated input.

During `teach` and `full`, the demo prints three distinct memory views:

- `SESSION` — every event durably retained in the Redis Agent Memory session.
- `SUMMARY` — the managed session summary when Redis has compacted earlier events; otherwise the demo clearly reports that no summary exists yet.
- `WINDOW` — the newest `REDIS_IRIS_SHORT_TERM_WINDOW` events that `RedisIrisChatHistoryProvider` supplies to the current Agent Framework model invocation.

The deliberately small default window of four messages makes the distinction visible: Redis retains the complete session while the model receives a bounded recent slice. Long-term recall remains a separate owner-scoped layer and appears in Session B after promotion.

## What the demo proves

1. Agent Framework request and response messages are written once to Redis Agent Memory.
2. The full durable session, optional managed summary, and bounded short-term window are separate memory views.
3. Recreating the .NET agent does not lose the conversation; the history provider reconstructs it from Redis.
4. Long-term search is always constrained by the trusted owner equality filter.
5. A new Agent Framework session for the same owner receives relevant promoted memory.
6. Redis operations participate in the connector's tracing and metrics surfaces.

Session deletion does not delete memories that have already been promoted. Use a dedicated synthetic demo owner/store until explicit long-term-memory cleanup is added to the connector.
