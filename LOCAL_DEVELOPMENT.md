# Run the samples locally

Use the [standalone quickstart](docs/quickstart.md) for a small application, or the
commands below to build and run the samples. Redis service provisioning is separate
from the .NET libraries. Start with the public [Redis Iris documentation](https://redis.io/docs/latest/develop/ai/context-engine/)
and obtain an Agent Memory store and/or Context Retriever surface accessible to
your application. An ordinary Redis server alone does not implement these APIs.

## Prerequisites

Install the .NET 10 SDK selected by `global.json` and the ASP.NET Core 8 runtime
(which includes the .NET 8 runtime) or the .NET 8 SDK for the samples. Docker
with Compose is needed for the container sample and Context Retriever provisioning jobs.

Build from the repository root:

```bash
dotnet build ms-agent-framework-redis.slnx -c Release
```

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
test -f artifacts/context-retriever/redis-iris-travel-users.json &&
DEVUI_USERS_FILE="$PWD/artifacts/context-retriever/redis-iris-travel-users.json" \
  ./scripts/run-agent-memory-devui.sh
```

The persistent demo requires RAM and a model key. DevUI additionally requires a
compatible travel Context Retriever surface; follow its [provisioning and seeding
instructions](samples/RedisIrisDevUI/README.md#provision-the-managed-context-retriever-surface).
These are explicit administrative operations against your chosen surface.
For a different saved shell profile, set `REDIS_IRIS_ENV_FILE` to its path.
The explicit user-bundle setting enables native multi-user login and matches the
seeded travelers; Compose supplies that setting automatically. Open the native
DevUI at http://localhost:5080/login and use a local sign-in token from the
generated bundle. Inspect it privately; do not paste the file into logs or chat.
Stop the process with Ctrl-C.

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
