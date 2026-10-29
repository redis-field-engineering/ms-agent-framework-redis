# C# notebooks

These Jupyter notebooks demonstrate Redis Iris Agent Memory, Context Retriever, POCO modeling, and their integration with Microsoft Agent Framework.

For the existing self-managed deployment, complete [Kubernetes sample setup](../../docs/kubernetes-samples.md)
and keep the port forwards running. Runtime notebooks default to Redis Cloud in
`.env.local`. Set `REDIS_IRIS_ENV_FILE` to the absolute path of
`secrets/iris-k8s-settings.json` before starting the kernel to select Kubernetes.

## Prerequisites

1. Install a Jupyter client with a C# kernel, such as the VS Code Polyglot Notebooks extension or the .NET Interactive Jupyter kernel.
2. For the memory notebooks (01, 02, and 05), configure Kubernetes as above, or ensure the repository root `.env.local` contains:

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

4. For notebooks 01–05, start with a fresh `.NET (C#)` kernel whose working directory is `notebooks/csharp`. Assembly references and `#load` paths are relative to that directory. Notebook 00 has separate executable PowerShell and Bash versions. Run cells in order and dispose connections at the end, including after a failed live call. Clear outputs before committing or sharing executed notebooks.

## Context Retriever setup

Start with [00 — Shared Context Retriever setup](00-context-retriever-setup.ipynb). It reuses the existing Docker Compose provisioner and seeder, requires no model or RAM credentials, and creates the complete travel fixture from the DevUI's POCOs. Use a dedicated demo Redis database: a different surface name alone does not isolate the stable seed keys. Provisioning updates the named surface's model; seeding overwrites its synthetic records.

Choose one executable setup notebook:

- [PowerShell setup](00-context-retriever-setup.ipynb): select the `.NET (PowerShell)` / Polyglot Notebooks kernel. Use PowerShell 7.4+ for native UTF-8 JSON redirection.
- [Bash setup](00-context-retriever-setup-bash.ipynb): select the `Bash` Jupyter kernel. If needed, run `python -m pip install bash_kernel` and `python -m bash_kernel.install` in your Jupyter Python environment, then refresh the kernel list. Bash must be on PATH; the JSON inspection cell also uses `jq`. Windows users can run this version in WSL.

Both versions contain actual code cells; run only one version. The build and Docker commands are identical in both shells. Initialization locates the repository root from your current directory inside the repository. When switching to the C# walkthroughs, start a fresh kernel in `notebooks/csharp`.

If VS Code reports `Failed to get language for textDocument/diagnostic` after changing the setup notebook from C# to PowerShell, close and reopen the notebook so it reloads the cell-language metadata, then select the .NET / Polyglot kernel with `pwsh` cells. If the old diagnostics persist, run **Developer: Reload Window**. The PowerShell notebook uses `polyglot-notebook` editor-language metadata and `pwsh` execution metadata; changing the selected runtime alone may leave stale C# editor state. The Bash notebook needs the separate Bash Jupyter kernel, not the Polyglot kernel.

Configure the admin endpoint/key, Redis connection, surface name, and `CONTEXT_RETRIEVER_CREDENTIAL_MODE=local-users` in `.env.local` as shown in the setup notebook. Set `REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT` to your deployment's runtime MCP URL. The equivalent terminal commands, run from the repository root, are:

```bash
docker compose --profile context-retriever run --rm --build context-provisioner
docker compose --profile context-retriever run --rm --build context-seeder
```

The jobs create a model JSON, seed JSON, and private traveler credential bundle under the ignored `artifacts/context-retriever` directory. Keep the credential bundle between runs; full agent keys are only returned at creation. The runtime notebooks select a scoped traveler from that bundle through [context-notebook.csx](context-notebook.csx). They do not provision or import data.

Notebooks 03–05 honor `REDIS_IRIS_ENV_FILE` (dotenv or JSON) and let environment variables override file values. `REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE` selects a different scoped credential bundle, with relative paths resolved from the repository root. The model artifact remains `artifacts/context-retriever/redis-iris-travel-context-model.json`. The Docker setup jobs always read `.env.local`; for an existing Kubernetes fixture, use its documented setup rather than rerunning the managed provisioner.

Direct reads in 03 and all of 04 need no OpenAI key. The agent section of 03 needs `OPENAI_API_KEY`; 05 also needs a synthetic-owner RAM store as used by 01–02. Bundled Context Retriever delegated tokens are supported when present and unexpired; this does not automatically configure RAM delegated authentication.

## Notebooks

- 00 — Shared Context Retriever setup ([PowerShell](00-context-retriever-setup.ipynb) / [Bash](00-context-retriever-setup-bash.ipynb)) — executable shell notebooks to build assemblies, provision the travel surface and scoped keys, import nine synthetic records, and inspect public artifacts.
- [01 — Redis Agent Memory data plane](01-redis-agent-memory-data-plane.ipynb) — writes and retrieves session events, displays the full session and bounded window, searches promoted long-term memory, and performs owner-scoped cleanup.
- [02 — Agent Framework memory](02-ms-agent-framework-memory.ipynb) — connects `RedisIrisChatHistoryProvider` and `RedisIrisMemoryProvider` to an agent, recreates the agent over the same session, opens a new session for recall, and cleans up both memory tiers.
- [03 — Context Retriever fundamentals](03-context-retriever-fundamentals.ipynb) — discovers tools and schemas, verifies the seed, queries flights, and attaches the per-invocation provider to an agent.
- [04 — POCO modeling](04-context-retriever-poco-modeling.ipynb) — compiles attributes and fluent configuration, inspects relationships and ACL mappings, compares model changes offline, and performs a typed live read. Its reduced read projection is never deployed over the full fixture.
- [05 — Memory and live travel context](05-memory-and-live-travel-context.ipynb) — teaches a preference in Session A and combines cross-session recall with current booking records in Session B.

The memory notebooks generate a unique synthetic owner on every kernel run. Cleanup is deliberately scoped to that owner and is not a complete-erasure guarantee. Redis Agent Memory promotion is asynchronous, so long-term recall may initially be empty; the notebooks make that state visible without waiting indefinitely. The shared Context Retriever surface is retained across notebooks.
