# Redis Iris Agent Memory demo

This sample follows Microsoft Agent Framework's canonical memory demonstration: Session A teaches an agent a user preference, the application agent is recreated and resumes the same durable session, and a brand-new Session B recalls the preference for the same owner.

Redis Agent Memory provides both Agent Framework extension points:

- `RedisIrisChatHistoryProvider` supplies durable, chronological session history.
- `RedisIrisMemoryProvider` supplies owner-scoped long-term memories as clearly labeled, untrusted user context.

## Configuration

For the existing self-managed GKE deployment, follow [Kubernetes sample setup](../../docs/kubernetes-samples.md).
The runner defaults to Redis Cloud in `.env.local`.
Set `REDIS_IRIS_ENV_FILE=secrets/iris-k8s.env` to select Kubernetes explicitly.

For Redis Cloud, the repository root `.env.local` holds the connection values. Add the model credential and, optionally, a stable demo owner:

```dotenv
REDIS_IRIS_ENDPOINT="https://..."
REDIS_IRIS_STORE_ID="..."
REDIS_IRIS_API_KEY="..."
OPENAI_API_KEY="..."
OPENAI_MODEL="gpt-4.1-mini"
SAMPLE_OWNER_ID="redis-iris-demo-shopper"
REDIS_IRIS_PROMOTION_TIMEOUT_MINUTES="8"
REDIS_IRIS_SHORT_TERM_WINDOW="4"
REDIS_IRIS_DEMO_TELEMETRY="false"
```

Do not commit `.env.local`. Use only a dedicated development store with synthetic data.

## Presentation-friendly flow

Teach the preference and demonstrate same-session durability:

```bash
./scripts/run-agent-memory-demo.sh teach
```

Redis Agent Memory promotes session events asynchronously. After a promotion window (depending on the service configuration), demonstrate recall in a completely new session:

```bash
./scripts/run-agent-memory-demo.sh recall
```

For an unattended end-to-end run, including bounded polling for promotion:

```bash
./scripts/run-agent-memory-demo.sh full
```

Every new RAM session prints the trusted user/owner ID and generated session ID before the agent writes any events. To remove all session history and promoted long-term memories owned by that demo user:

```bash
./scripts/run-agent-memory-demo.sh clear
```

`clear` is owner-scoped to `SAMPLE_OWNER_ID`; it does not delete records belonging to other users in the store.

By default, the output focuses on user and assistant turns plus the memory layers. The owner scope comes from trusted application configuration rather than model-generated input.

To include connector traces and low-cardinality metrics in an observability-focused demonstration, opt in with:

```dotenv
REDIS_IRIS_DEMO_TELEMETRY="true"
```

During `teach` and `full`, the demo prints three distinct memory views:

- `SESSION` — every event durably retained in the Redis Agent Memory session.
- `SUMMARY` — the managed session summary when Redis has compacted earlier events; otherwise the demo clearly reports that no summary exists yet.
- `WINDOW` — the newest `REDIS_IRIS_SHORT_TERM_WINDOW` events that `RedisIrisChatHistoryProvider` supplies to the current Agent Framework model invocation.

The deliberately small default window of four messages makes the distinction visible: Redis retains the complete session while the model receives the server summary (when present) followed by a bounded recent slice. `IncludeSessionSummary` defaults to true, and `MaxSummaryCharacters` bounds summary text separately from the event count. The summary is labeled untrusted user context. Long-term recall remains a separate owner-scoped layer and appears in Session B after promotion.

## What the demo proves

1. Agent Framework request and response messages are written once to Redis Agent Memory.
2. The full durable session, optional managed summary, and bounded short-term window are separate memory views.
3. Recreating the .NET agent does not lose the conversation; the history provider reconstructs it from Redis.
4. Long-term search is always constrained by the trusted owner equality filter.
5. A new Agent Framework session for the same owner receives relevant promoted memory.
6. Redis operations participate in the connector's tracing and metrics surfaces.

The `clear` route deliberately deletes both memory tiers because deleting a session alone does not delete memories that have already been promoted.
