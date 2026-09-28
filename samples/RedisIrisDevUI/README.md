# Redis Iris Agent Memory DevUI

This sample adapts Microsoft Agent Framework's official ASP.NET Core DevUI pattern to the Redis Iris integration. It provides an interactive browser chat plus owner-scoped RAM inspection and cleanup endpoints.

## Run

Add `OPENAI_API_KEY` to the repository root `.env.local`, then run:

```bash
./scripts/run-agent-memory-devui.sh
```

Open [http://localhost:5080](http://localhost:5080) for the sample dashboard or [http://localhost:5080/devui](http://localhost:5080/devui) for the Agent Framework DevUI.

The runner loads the existing Redis Cloud and OpenAI settings from `.env.local`. Optional settings are:

```dotenv
OPENAI_MODEL="gpt-4.1-mini"
SAMPLE_OWNER_ID="redis-iris-devui-shopper"
REDIS_IRIS_SHORT_TERM_WINDOW="4"
REDIS_IRIS_DEMO_TELEMETRY="false"
```

## Demonstrate both memory tiers

1. Start a DevUI conversation and tell the agent your name and several travel preferences.
2. Continue that conversation. RAM durably retains the complete session while the model receives the configured recent-message window.
3. Open `/api/ram` to see the trusted owner, generated RAM session IDs, and promoted long-term memories.
4. Open `/api/ram/sessions/{sessionId}` to inspect a session's events and managed summary.
5. After RAM's asynchronous promotion runs, create a new DevUI conversation and ask what the agent remembers.

The application also prints `RAM USER` and `RAM SESSION` when it assigns a new Agent Framework session to RAM.

For recall troubleshooting, this development-only sample enables sensitive `RedisIrisMemoryProvider` logs. Each agent turn prints the exact owner, session, search query, search settings, result count, and returned memory IDs/types/text. These logs can contain user data and must not be copied into a production configuration.

## Clear the demo owner

Cleanup requires an exact owner confirmation and deletes only that owner's sessions and long-term memories:

```bash
curl -X DELETE 'http://localhost:5080/api/ram?confirm=redis-iris-devui-shopper'
```

This is deliberately a local-development sample. It refuses to start outside the ASP.NET Core `Development` environment. A production application must authenticate callers and derive the owner and tenant from trusted claims rather than a shared environment variable.
