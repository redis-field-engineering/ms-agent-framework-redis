# Custom memory types and attribute recall

This deployment/demo executable registers a server-side type, writes a custom memory, and searches it through the owner-scoped Agent Framework service. Use a dedicated test store. Registration persists the type, and each `seed` or `extract` run creates new synthetic data.

The management commands require a control plane implementing the Iris v2 `/iris/v1/agent-memory/stores/{storeId}/memory-types` resources and an administrative bearer token. This is not the Redis Cloud account API at `/memory-stores`. For Cloud deployments without access to this control-plane contract, register the matching type in the console and use only the data-plane commands.

Export connection values through your shell or secret manager; the sample does not read `.env` files automatically:

- `REDIS_IRIS_STORE_ID`: existing store.
- `REDIS_IRIS_MANAGEMENT_ENDPOINT`, `REDIS_IRIS_MANAGEMENT_TOKEN`: administrative base URL and bearer token, used only by `register`, `enable`, and `disable`.
- `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_API_KEY`: data-plane base URL and runtime agent credential.
- `REDIS_IRIS_EXAMPLE_OWNER`: synthetic owner for the example. In an agent host, resolve owner identity from authentication. Deployments requiring delegated user tokens also need an appropriate token provider on the data-plane client.

```sh
dotnet run --project samples/RedisIrisCustomMemory -- register
dotnet run --project samples/RedisIrisCustomMemory -- seed
dotnet run --project samples/RedisIrisCustomMemory -- recall
```

The `trip_preference` definition has one `destinations` string-list field. Registration supplies an extraction strategy but explicitly disables it. A second registration returns HTTP 409; inspect the existing definition instead of retrying or assuming it matches. Type name, description and fields cannot be edited by this SDK.

To demonstrate asynchronous extraction, explicitly enable it before appending a new session:

```sh
dotnet run --project samples/RedisIrisCustomMemory -- enable
dotnet run --project samples/RedisIrisCustomMemory -- extract
dotnet run --project samples/RedisIrisCustomMemory -- disable
```

The example polls for at most three minutes. Longer extraction cadences can exceed that timeout; extraction content is model-dependent. Enabling a strategy does not promise historical backfill. Disable it when finished, including after a failed extraction run. Strategy changes use the revision read immediately beforehand; a 412 requires a fresh review, not automatic retries.

Clean up only the synthetic memory IDs and session IDs printed by the sample through the normal delete APIs. Deleting a session does not delete its promoted memories. The custom type remains registered; do not run this example in a store where that persistent configuration is unwanted. Administrative credentials and clients belong in deployment tooling, not agent tools or runtime registration.
