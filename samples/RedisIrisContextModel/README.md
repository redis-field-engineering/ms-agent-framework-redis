# Context Retriever POCO model sample

Generate a deterministic `ctxctl --datamodel` JSON artifact:

```bash
dotnet run --project samples/RedisIrisContextModel -- ./travel-context-model.json
```

To deploy, first authenticate `ctxctl`, set the Redis connection environment variables shown in `Program.cs`, and add `--deploy`. Deployment uses the stored ctxctl admin credential; the model JSON contains no secrets.
