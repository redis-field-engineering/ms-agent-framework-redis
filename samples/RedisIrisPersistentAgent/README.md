# Redis Iris Agent Memory demo

This sample follows Microsoft Agent Framework's canonical memory demonstration: Session A teaches an agent a user preference, the application agent is recreated and resumes the same durable session, and a brand-new Session B recalls the preference for the same owner.

Redis Agent Memory provides both Agent Framework extension points:

- `RedisIrisChatHistoryProvider` supplies durable, chronological session history.
- `RedisIrisMemoryProvider` supplies owner-scoped long-term memories as clearly labeled, untrusted user context.

## Configuration

Copy the repository root `.env.example` to `.env.local` and configure your Agent Memory connection, model credential, and optional stable demo owner. To use a different shell-format configuration file, set `REDIS_IRIS_ENV_FILE` to its path:

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

## Run the demo

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

Every new RAM session prints the trusted user/owner ID and generated session ID before the agent writes any events. To run best-effort cleanup of session history and currently discoverable promoted memories owned by that synthetic demo user:

```bash
./scripts/run-agent-memory-demo.sh clear
```

`clear` is owner-scoped to `SAMPLE_OWNER_ID`. It is a demo cleanup helper, not complete erasure: background extraction may finish after cleanup, and partial deletion errors or search limits require follow-up. See the [preview operational contract](../../docs/operational-contract.md).

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

## Library behavior demonstrated

1. Supported text request and response messages are written during a normal successful invocation. Retries after ambiguous failures can duplicate events.
2. The full durable session, optional managed summary, and bounded short-term window are separate memory views.
3. Recreating the .NET agent does not lose the conversation; the history provider reconstructs it from Redis.
4. Long-term search is always constrained by the trusted owner equality filter.
5. A new Agent Framework session for the same owner receives relevant promoted memory.
6. Redis operations participate in the connector's tracing and metrics surfaces.

The `clear` route deliberately deletes both memory tiers because deleting a session alone does not delete memories that have already been promoted.
