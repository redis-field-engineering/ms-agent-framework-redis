# Local development

Use the [standalone quickstart](docs/quickstart.md) for a small application, or the
commands below to develop this repository. Redis service provisioning is separate
from the .NET libraries. Start with the public [Redis Iris documentation](https://redis.io/docs/latest/develop/ai/context-engine/)
and obtain an Agent Memory store and/or Context Retriever surface accessible to
your application. An ordinary Redis server alone does not implement these APIs.
The [Kubernetes guide](docs/kubernetes-samples.md) describes an optional internal
Redis Field Engineering deployment; it is not a prerequisite for using the packages.

## Tools and local checks

Install the .NET 10 SDK selected by `global.json`, the .NET 8 runtime for the
compatibility tests, and Python 3 for packaging helpers. Docker with Compose is
needed only for the container sample and Docker-context regression.

From the repository root:

```bash
dotnet restore ms-agent-framework-redis.slnx
dotnet build ms-agent-framework-redis.slnx -c Release --no-restore
dotnet test ms-agent-framework-redis.slnx -c Release --no-build
dotnet format ms-agent-framework-redis.slnx --verify-no-changes --no-restore
python3 eng/test-package-validation.py
./eng/pack.sh
./eng/package-smoke.sh
```

The package destination must be empty; use `./eng/pack.sh /path/to/new-directory`
and pass that same directory to `./eng/package-smoke.sh` for repeated builds.
Unit tests use synthetic handlers and fixtures. Unconfigured live tests report
skips, which do not count as release evidence. There is no offline Docker profile
or scheduled nightly live-test workflow. CI runs unit/build/package checks and
builds the sample container; the manual release workflow requires the configured
live gates in [TESTING.md](TESTING.md).

## Configure a sample

```bash
cp .env.example .env.local
chmod 600 .env.local
```

Fill only the settings required by the sample you run. Keep the file out of logs
and version control. The native runner scripts source this shell-format file;
the standalone C# snippets read environment variables directly.

| Setting | Required by |
| --- | --- |
| `REDIS_IRIS_ENDPOINT`, `REDIS_IRIS_STORE_ID`, `REDIS_IRIS_API_KEY` | RAM samples |
| `OPENAI_API_KEY` | Model-backed samples (not data-plane tests or model export) |
| `REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT`, `REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY` | DevUI and fixed Context Retriever sample |
| `REDIS_IRIS_CONTEXT_RETRIEVER_CREDENTIAL_VERSION` | Non-secret CR key rotation version; change when rotating the key |

Use HTTPS except for loopback development. Key-only RAM samples require a store
that permits service identities; ownership-enforcing personal stores require
signed user delegation. The [security model](SECURITY.md) explains the distinction.
A fixed Context Retriever key gives every caller its entire authorized scope.
Use dedicated synthetic resources with narrow permissions and short retention.

```bash
./scripts/run-agent-memory-demo.sh full
./scripts/run-agent-memory-devui.sh
```

The persistent demo requires RAM and a model key. DevUI additionally requires a
compatible travel Context Retriever surface; follow its [provisioning and seeding
instructions](samples/RedisIrisDevUI/README.md#provision-the-managed-context-retriever-surface).
These are explicit administrative operations against your chosen surface.
For a different saved shell profile, set `REDIS_IRIS_ENV_FILE` to its path.
Open the native DevUI at http://localhost:5080. Stop it with Ctrl-C.

Generate the travel model and synthetic records without any service credentials:

```bash
dotnet run --project samples/RedisIrisDevUI -- --print-context-model
dotnet run --project samples/RedisIrisDevUI -- --print-context-seed
```

## Authentication, containers, and cleanup

The [DevUI guide](samples/RedisIrisDevUI/README.md) describes local sign-in tokens,
scoped keys, and optional Cognito authentication. It also covers Compose setup,
which requires its synthetic user bundle for remote-container access. The
[Cognito example](infra/aws-cognito-jwt/README.md) is optional; its Terraform state
contains sensitive credentials and must remain private. Docker contexts exclude
infrastructure and Terraform-sensitive files.

Use `docker compose down` to stop the container demonstration. Stopping containers
does not delete managed service data. The persistent sample's `clear` command is
limited to its configured `SAMPLE_OWNER_ID`; session deletion does not retract
promoted long-term memories or guarantee complete erasure. Inspect partial cleanup
failures and asynchronous extraction as described in the
[operational contract](docs/operational-contract.md).
