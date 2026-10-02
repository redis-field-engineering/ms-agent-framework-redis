# Redis Iris Agent Memory DevUI

This sample adapts Microsoft Agent Framework's official ASP.NET Core DevUI pattern to the Redis Iris integration. It provides an interactive browser chat plus owner-scoped RAM inspection and cleanup endpoints.

## Run

Add `OPENAI_API_KEY` to the repository root `.env.local`, then run:

```bash
./scripts/run-agent-memory-devui.sh
```

Open [http://localhost:5080](http://localhost:5080) for the sample dashboard or [http://localhost:5080/devui](http://localhost:5080/devui) for the Agent Framework DevUI.

## Run the Docker observability demo

The repository-level Compose stack builds the DevUI and exports connector metrics and traces over OTLP to a local OpenTelemetry Collector:

```bash
docker compose up --build
```

The stack reads Redis Cloud and OpenAI credentials from the ignored `.env.local` file. It does not bake that file into the application image.
Docker bridge traffic requires DevUI's remote-access option, so Compose enables it while binding every published port to `127.0.0.1`. The non-container runner retains DevUI's loopback-only default.

| Surface | URL | Purpose |
| --- | --- | --- |
| DevUI | <http://localhost:5080/devui> | Exercise short- and long-term memory |
| Grafana | <http://localhost:3000/d/redis-iris-agent-memory> | View the provisioned Agent Memory metrics dashboard |
| Jaeger | <http://localhost:16686> | Inspect `redis-iris-devui` traces |
| Prometheus | <http://localhost:9090> | Query the raw exported metric series |

Generate some traffic in DevUI, allow one metrics export interval, then select the `redis-iris-devui` service in Jaeger. Grafana is configured for anonymous local viewing. Stop the stack with `docker compose down`; add `--volumes` only when you also want to discard the local Prometheus and Grafana data.

Jaeger stores and visualizes traces. Metrics travel over the same OTLP connection but are exposed by the Collector for Prometheus and visualized in Grafana.

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

For recall troubleshooting, `RedisIrisMemoryProvider` logs the number of long-term memory records returned by each search. Query text, identity, memory IDs, and memory content are not logged.

## Clear the demo owner

Cleanup requires an exact owner confirmation and deletes only that owner's sessions and long-term memories:

```bash
curl -X DELETE 'http://localhost:5080/api/ram?confirm=redis-iris-devui-shopper'
```

This is deliberately a local-development sample. It refuses to start outside the ASP.NET Core `Development` environment. A production application must authenticate callers and derive the owner and tenant from trusted claims rather than a shared environment variable.
