# Run the samples against Iris on Kubernetes

The native sample runners use the existing `iris-demo` GKE deployment in project
`central-beach-194106`, region `us-east1`, namespace `redis-iris`. The deployment
and credential contract are documented in the cluster's
[integration handoff](https://github.com/redis-field-engineering/redis-iris-k8s/blob/main/docs/integration-handoff.md).
No Terraform or Helm deployment is needed.

## Connect and configure

Prerequisites: .NET 8, Python 3.9+, kubectl, gcloud, the GKE authentication plugin,
and access to the cluster. Keep your OpenAI application key in `.env.local` or
export `OPENAI_API_KEY`; the setup imports only `OPENAI_API_KEY` and `OPENAI_MODEL`
from that file, never its Cloud endpoints or Iris credentials.
On reruns, saved Kubernetes model settings take precedence over `.env.local`;
explicitly exported `OPENAI_API_KEY` and `OPENAI_MODEL` override either source.

Refresh Google authentication if needed:

```bash
gcloud auth login
```

Start the loopback forwards in a terminal and leave them running:

```bash
./scripts/forward-iris-k8s.sh
```

The default kubeconfig is
`~/projects/terraform/redis-iris-k8s/secrets/kubeconfig`. Override `KUBECONFIG` and
`IRIS_K8S_NAMESPACE` in **both** terminals when using another configuration.
The forwards expose RAM on 9000, Context MCP on 8081, Context administration on
8082, and the cluster smoke service on 8080. Ctrl-C stops all forwards; if one
exits, the script stops the rest so it can be restarted as a group.

In a second terminal:

```bash
dotnet build samples/RedisIrisDevUI
python3 scripts/configure-iris-k8s.py
export REDIS_IRIS_ENV_FILE="$PWD/secrets/iris-k8s.env"
./scripts/run-agent-memory-devui.sh
```

If a Docker DevUI already occupies port 5080, stop that service first with
`docker compose stop devui`. The other observability services can keep running.
Open [DevUI](http://localhost:5080/devui) and sign in with either user's local
`signInToken` from `secrets/iris-k8s-users.json`. Inspect the file locally; do not
paste its API keys into chat, logs, or version control.

Setup checks the cluster smoke status, reads the existing scoped RAM token from
`iris-bootstrap-state`, and creates a separate `ms-agent-framework-travel`
Context surface. It does not change the bootstrap `iris-demo` surface or rotate
the smoke service's credentials. The travel model is generated from the DevUI
POCOs. Nine synthetic records and two traveler-scoped agent keys provide the
same two-user Context Retriever demonstration as the Cloud sample.

The installed Context Retriever 0.4.2 API supports `/mcp` with `X-API-Key`.
Each traveler key carries server-enforced `tenant_id` and `traveler_id` access
tags. RAM continues using its scoped service token with application-enforced
owner filtering; this setup does not enable OIDC or server-side RAM ownership
enforcement.

## Reuse Cognito for the GKE Context Retriever

The existing AWS Cognito pool can authenticate the local DevUI while Context
Retriever and RAM run in GKE. No new identity provider or deployment is needed.
After the initial Kubernetes setup, keep the forwards running and execute:

```bash
python3 scripts/configure-iris-k8s-cognito.py
REDIS_IRIS_ENV_FILE="$PWD/secrets/iris-k8s-cognito.env" ./scripts/run-agent-memory-devui.sh
```

Stop any other DevUI process using port 5080 before starting this profile.
Open [login](http://localhost:5080/login) and use an existing Cognito demo account.
The local sign-in tokens in `iris-k8s-users.json` do not apply to this profile.

The script reads the existing `infra/aws-cognito-jwt` Terraform outputs without
applying Terraform or changing Cognito. It creates a tenant-scoped CR agent key,
configures the dedicated travel surface's `identity_provider` through the admin
API, and verifies that the service persists the issuer, JWKS URL, audience, claim
mappings, and required-token setting. It does not change the shared bootstrap
surface, Kubernetes deployments, or RAM authentication.

CR receives the tenant agent key in `X-API-Key` and the Cognito access token in
`Authorization: Bearer`. The access token supplies the narrower traveler scope;
the DevUI derives its identity from independently validated token claims. CR
rejects a missing JWT with HTTP 403 and an invalid JWT with HTTP 401 on MCP
initialization. The IdP settings apply to the entire travel surface, including
previously created traveler keys: the original local-token profile cannot call
that surface without a JWT while enforcement is enabled.

The script checkpoints its tenant key in `secrets/iris-k8s-cognito-context.json`
and writes the complete host environment to `secrets/iris-k8s-cognito.env`, both
with mode `0600`. Its first run also saves the prior surface IdP configuration
to `secrets/iris-k8s-idp-before-cognito.json`. Reruns reuse the same tenant key.

The development-only [JWT inspector](http://localhost:5080/debug/jwt) is enabled
in this profile and requires the authenticated session cookie. It shows the
exact delegated access token and decoded claims; tokens are not written to logs.
The existing Cognito sample's cookie lifetime follows the access token, so sign
in again after it expires.

On October 26, 2026, the existing GKE CR admin API accepted and persisted Cognito
trust without an image change or deployment rollout. Missing/invalid JWT
requests were rejected, and a real Cognito sign-in followed by a traveler name
and home-airport query succeeded through the DevUI. All eight DevUI
authentication unit tests passed. The expired application OpenAI key was
replaced in the private Kubernetes profiles with the deployment's valid
inference credential; the Cloud profile was not changed.

For delegated-JWT live tests, load a fresh, two-user shared-key bundle into the
profile's `secrets/iris-k8s-cognito-users.json` location using the existing bundle
format described in [the Cognito example](../infra/aws-cognito-jwt/README.md).
The setup script does not create tokens or substitute the old traveler keys for
signed-user validation; the live suite skips until that bundle is available.

Setup can be rerun: it updates its own surface model, overwrites its synthetic
records, and reuses saved keys. It checkpoints new keys immediately in
`secrets/iris-k8s-context.json`. Preserve that file; the service does not return
existing raw keys. If a key exists remotely but its local checkpoint is missing,
setup refuses to mint a duplicate and requires explicit key recovery/rotation.

All generated credentials live under ignored, Docker-excluded `secrets/`, with
mode `0600`. Administrative credentials and the Redis password stay in the setup
process and are not included in the sample environment.

For the C# notebooks, set `REDIS_IRIS_ENV_FILE` to the absolute path of the
generated `secrets/iris-k8s-settings.json` before starting the kernel.
Keep the forwards running and build their Release assembly references as
described in [the notebook README](../notebooks/csharp/README.md).

## Other samples and validation

### Optional RAM delegated JWT mode

After the infrastructure deployment supports RAM delegated authentication,
enable the connector with the same Cognito login used by CR:

```bash
# In a separate terminal, keep the isolated RAM forwards running as well as the CR forwards:
~/projects/terraform/redis-iris-k8s/scripts/forward-ram-jwt.sh
```

Then configure and start the application:

```bash
python3 scripts/configure-iris-k8s-cognito.py --ram-delegated-auth \
  --ram-profile "$HOME/projects/terraform/redis-iris-k8s/secrets/ram-jwt-connector.json"
REDIS_IRIS_ENV_FILE="$PWD/secrets/iris-k8s-cognito.env" ./scripts/run-agent-memory-devui.sh
```

The isolated deployment uses RAM 0.10.2 on local port 9002, a dedicated store,
and a separate Identity Service. The original RAM on port 9000 and its bootstrap
keys remain intact. Its forwarding script also exposes control and identity
administration on loopback ports 9003 and 9004. Only start a forwarding script if
those ports are not already forwarded.

Import the infrastructure-provided profile once with `--ram-profile`. Its JSON must contain
`endpoint`, `storeId`, and `agentKey`. The setup preserves the selected RAM mode
and credential in ignored `secrets/iris-k8s-cognito-ram.json` on reruns. Use
`--no-ram-delegated-auth` to explicitly disable forwarding; disabling it does not
restore personal access when the server enforces user ownership.
The setup refuses to enable delegation before a RAM profile has been imported,
so it cannot accidentally point JWT mode at the original unsupported deployment.

The application sends its RAM key in `Authorization` and the current Cognito
access JWT in `X-Iris-User-Token`. New RAM sessions belong to the validated
Cognito `sub`; previous tenant/traveler composite-owner records remain intact in
the original store and are not copied into this dedicated store.
Restarting DevUI clears its in-memory sessions and requires sign-in again.

This switch does not upgrade RAM, configure its trust, or enable server ownership
enforcement. Coordinate those changes with the infrastructure deployment, and
verify two-user isolation after cutover. In RAM 0.10.2 the enforcement setting is
`memory.ownership.enabled`; earlier source documentation used
`memory.namespace_features.enforce_ownership`. Use the deployed release's schema.
The original key-only smoke probe and personal-memory samples need a supported
identity path once enforcement is on. Do not run them unchanged against that
data plane and interpret their authorization errors as a service outage.

On October 26, 2026, the user confirmed a real Cognito sign-in and the query
“Remember that my preferred seat is a window seat. What is my name and home
airport?” succeeded with RAM delegation enabled against the isolated deployment.
The application log showed two initialized RAM sessions, no failure entries,
and no RAM HTTP errors. This validates the signed-user DevUI flow; the automated
two-user isolation suite and asynchronous memory promotion remain separate,
uncompleted checks.

The persistent memory demo uses the same explicitly selected profile:

```bash
./scripts/run-agent-memory-demo.sh teach
./scripts/run-agent-memory-demo.sh recall
```

Promotion is asynchronous. Use `full` for bounded promotion polling or `clear`
to clean up only `SAMPLE_OWNER_ID` (`ms-agent-framework-k8s-demo` by default).

For the fixed-identity Context Retriever sample or live tests, load the profile
explicitly into your shell:

```bash
source secrets/iris-k8s.env
dotnet run --project samples/RedisIrisContextRetrieverFixed
dotnet test tests/Redis.Iris.AgentMemory.LiveTests
dotnet test tests/Redis.Iris.ContextRetriever.LiveTests
```

The fixed-identity sample uses the first traveler's key. The Context live suite
tests both traveler keys, including foreign-ID denial and trusted argument
injection. It also continues to accept the existing shared-key/delegated-JWT
bundle format.

On October 22, 2026, all 21 cluster smoke checks and all seven connector live
tests passed against this deployment. The smoke resources remain shared; never
flush the underlying Redis databases or delete `iris-bootstrap-state`.

## Profiles and Docker

The native runners and notebooks default to the Cloud configuration in
`.env.local`. Kubernetes requires an explicit `REDIS_IRIS_ENV_FILE` selection.
To return to Cloud in a shell where that override is set:

```bash
unset REDIS_IRIS_ENV_FILE
./scripts/run-agent-memory-devui.sh
```

The generated Kubernetes environment uses host loopback addresses and absolute
host paths. It is for native runners, **not** the Docker Compose application.
The existing Compose configuration still reads `.env.local`; do not load the
Kubernetes profile into that container, whose loopback is a different network
namespace. Running the .NET sample on the host keeps the forwards private and
preserves the connector's loopback-only policy for unencrypted Context MCP.
