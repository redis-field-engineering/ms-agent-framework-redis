# C# notebooks

These Jupyter notebooks demonstrate the Redis Iris Agent Memory integration from the data plane up through Microsoft Agent Framework.

## Prerequisites

1. Install a Jupyter client with a C# kernel, such as the VS Code Polyglot Notebooks extension or the .NET Interactive Jupyter kernel.
2. Ensure the repository root `.env.local` contains:

   ```dotenv
   REDIS_IRIS_ENDPOINT="https://..."
   REDIS_IRIS_STORE_ID="..."
   REDIS_IRIS_API_KEY="..."
   OPENAI_API_KEY="..."
   OPENAI_MODEL="gpt-4.1-mini"
   ```

3. Build the connector assemblies used by the notebooks:

   ```bash
   dotnet build ms-agent-framework-redis.slnx --configuration Release
   ```

4. Open the notebooks from `notebooks/csharp` and select the `.NET (C#)` kernel.

## Notebooks

- `01-redis-agent-memory-data-plane.ipynb` — writes and retrieves session events, displays the full session and bounded window, searches promoted long-term memory, and performs owner-scoped cleanup.
- `02-ms-agent-framework-memory.ipynb` — connects `RedisIrisChatHistoryProvider` and `RedisIrisMemoryProvider` to an Agent Framework agent, recreates the agent over the same session, opens a new session for recall, and cleans up both memory tiers.

Both notebooks generate a unique synthetic owner on every kernel run. Cleanup is deliberately scoped to that owner. Redis Agent Memory promotion is asynchronous, so long-term recall may initially be empty; the notebooks make that state visible without waiting indefinitely.
