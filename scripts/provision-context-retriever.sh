#!/usr/bin/env bash
set -euo pipefail

required=(
  CONTEXT_RETRIEVER_ADMIN_URL
  CONTEXT_RETRIEVER_ADMIN_KEY
  CONTEXT_RETRIEVER_REDIS_ADDRESS
  CONTEXT_RETRIEVER_REDIS_PASSWORD
)

for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "Missing required Context Retriever provisioning value: ${name}" >&2
    exit 2
  fi
done

surface_name="${CONTEXT_RETRIEVER_SURFACE_NAME:-redis-iris-travel-assistant}"
artifact_dir="${CONTEXT_RETRIEVER_ARTIFACT_DIR:-/artifacts}"
model_path="${artifact_dir}/redis-iris-travel-context-model.json"
update_existing="${CONTEXT_RETRIEVER_UPDATE_EXISTING:-false}"

case "${update_existing}" in
  true|false) ;;
  *)
    echo "CONTEXT_RETRIEVER_UPDATE_EXISTING must be true or false." >&2
    exit 2
    ;;
esac

mkdir -p "${artifact_dir}"
dotnet /app/publish/RedisIrisDevUI.dll --print-context-model > "${model_path}"
echo "Generated Context Retriever DataModel: ${model_path}"

# ctxctl reads its control-plane endpoint from CTX_API_URL. Store the admin key
# in this one-shot container's ephemeral credential store so it never appears in
# the ctxctl process arguments.
export CTX_API_URL="${CONTEXT_RETRIEVER_ADMIN_URL}"
export CTX_NO_COLOR=true
export HOME="${CONTEXT_RETRIEVER_CTXCTL_HOME:-/tmp/ctxctl-home}"
export CTX_CREDENTIAL_PASSWORD="${CTX_CREDENTIAL_PASSWORD:-redis-iris-ephemeral-provisioner}"
mkdir -p "${HOME}"
python3 -c 'import os; from context_surfaces.cli.services.credential_manager import CredentialManager; CredentialManager(password=os.environ["CTX_CREDENTIAL_PASSWORD"]).store_admin_key("default", os.environ["CONTEXT_RETRIEVER_ADMIN_KEY"])'

list_output="$(ctxctl --no-color --output json surface list --page-size 100)"
if jq -e 'type == "array"' >/dev/null 2>&1 <<<"${list_output}"; then
  matches="$(jq --arg name "${surface_name}" '[.[] | select(.name == $name)]' <<<"${list_output}")"
else
  # ctxctl 0.0.6 emits a human-readable message rather than [] when no surfaces exist.
  if [[ "${list_output}" == *"No context surfaces found"* ]]; then
    matches='[]'
  else
    echo "ctxctl returned an unexpected surface-list response." >&2
    exit 1
  fi
fi

match_count="$(jq 'length' <<<"${matches}")"
if [[ "${match_count}" -gt 1 ]]; then
  echo "Found ${match_count} surfaces named '${surface_name}'; refusing to choose one." >&2
  exit 1
fi

if [[ "${match_count}" -eq 1 ]]; then
  surface_id="$(jq -r '.[0].id' <<<"${matches}")"
  if [[ "${update_existing}" == "true" ]]; then
    echo "Updating existing Context Retriever surface '${surface_name}' (${surface_id})."
    ctxctl --no-color --output json surface update "${surface_id}" \
      --description "Structured travel context for the Redis Iris Microsoft Agent Framework DevUI" \
      --datamodel "${model_path}"
  else
    echo "Context Retriever surface '${surface_name}' already exists (${surface_id}); leaving it unchanged."
    echo "Set CONTEXT_RETRIEVER_UPDATE_EXISTING=true to apply the generated DataModel."
  fi
  exit 0
fi

create_args=(
  --name "${surface_name}"
  --description "Structured travel context for the Redis Iris Microsoft Agent Framework DevUI"
  --datamodel "${model_path}"
  --redis-addr "${CONTEXT_RETRIEVER_REDIS_ADDRESS}"
  --redis-password "${CONTEXT_RETRIEVER_REDIS_PASSWORD}"
)

if [[ -n "${CONTEXT_RETRIEVER_REDIS_USERNAME:-}" ]]; then
  create_args+=(--redis-username "${CONTEXT_RETRIEVER_REDIS_USERNAME}")
fi
if [[ "${CONTEXT_RETRIEVER_REDIS_TLS:-true}" == "true" ]]; then
  create_args+=(--redis-tls)
elif [[ "${CONTEXT_RETRIEVER_REDIS_TLS}" != "false" ]]; then
  echo "CONTEXT_RETRIEVER_REDIS_TLS must be true or false." >&2
  exit 2
fi
if [[ -n "${CONTEXT_RETRIEVER_REDIS_DB:-}" ]]; then
  create_args+=(--redis-db "${CONTEXT_RETRIEVER_REDIS_DB}")
fi

echo "Creating Context Retriever surface '${surface_name}'."
# ctxctl currently accepts the Redis password only as a create-command option.
# Keep this provisioner isolated and do not enable shell tracing.
ctxctl --no-color --output json surface create "${create_args[@]}"
