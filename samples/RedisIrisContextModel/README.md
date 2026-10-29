# Context Retriever POCO model sample

Generate a deterministic DataModel JSON artifact:

```bash
dotnet run --project samples/RedisIrisContextModel -- ./travel-context-model.json
```

To deploy through the optional HTTP management client, set these environment variables and add `--deploy` to the command:

- `CONTEXT_RETRIEVER_MANAGEMENT_ENDPOINT`: service base URL, without `/mcp` or `/api/v1` (for example `https://cloud.redis.io/context-surfaces`).
- `CONTEXT_RETRIEVER_ADMIN_API_KEY`: an administrative API key.
- `CONTEXT_RETRIEVER_REDIS_ADDRESS`: Redis host and port.
- `CONTEXT_RETRIEVER_REDIS_USERNAME` and `CONTEXT_RETRIEVER_REDIS_PASSWORD`: Redis credentials when required.

The sample enables Redis TLS and uses database 0. Deployment creates a new surface each time; it does not import records or update an existing surface. The sample reads the process environment, not `.env.local`; load that file into your shell beforehand if needed. No `ctxctl` installation or CLI login is required. The exported model JSON contains no credentials.

See [modeling and management](../../docs/context-retriever-modeling.md) for reading and updating surfaces and dependency injection.
