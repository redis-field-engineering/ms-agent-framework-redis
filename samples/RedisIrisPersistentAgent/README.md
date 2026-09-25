# Persistent Agent sample

Set `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_STORE_ID`, `REDIS_IRIS_API_KEY`, `OPENAI_API_KEY`, and `SAMPLE_OWNER_ID`; optionally set `OPENAI_MODEL`. Then run:

```bash
dotnet run --project samples/RedisIrisPersistentAgent
```

The sample stores session history, serializes and resumes an Agent Framework session after recreating the agent, and starts a second session for owner-scoped long-term recall. Memory promotion is asynchronous, so the last query may need to be rerun after the managed store's configured promotion interval. The included console listeners demonstrate the same `ActivitySource` and `Meter` surfaces consumed by an OpenTelemetry exporter.
