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
user_profiles_path="${artifact_dir}/redis-iris-travel-user-profiles.json"
user_credentials_path="${artifact_dir}/redis-iris-travel-users.json"
tenant_credentials_path="${artifact_dir}/redis-iris-travel-tenant-agent.json"
delegated_tokens_path="${CONTEXT_RETRIEVER_DELEGATED_TOKENS_FILE:-${artifact_dir}/redis-iris-travel-delegated-tokens.json}"
update_existing="${CONTEXT_RETRIEVER_UPDATE_EXISTING:-true}"
credential_mode="${CONTEXT_RETRIEVER_CREDENTIAL_MODE:-local-users}"

case "${update_existing}" in
  true|false) ;;
  *)
    echo "CONTEXT_RETRIEVER_UPDATE_EXISTING must be true or false." >&2
    exit 2
    ;;
esac

case "${credential_mode}" in
  local-users|tenant-jwt|none) ;;
  *)
    echo "CONTEXT_RETRIEVER_CREDENTIAL_MODE must be local-users, tenant-jwt, or none." >&2
    exit 2
    ;;
esac

mkdir -p "${artifact_dir}"
dotnet /app/publish/RedisIrisDevUI.dll --print-context-model > "${model_path}"
dotnet /app/publish/RedisIrisDevUI.dll --print-context-users > "${user_profiles_path}"
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
    echo "Set CONTEXT_RETRIEVER_UPDATE_EXISTING=true to apply the generated DataModel (the default)."
  fi
else
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
  create_output="$(ctxctl --no-color --output json surface create "${create_args[@]}")"
  surface_id="$(jq -er '.id' <<<"${create_output}")"
  jq 'del(.redis_password, .credentials)' <<<"${create_output}"
fi

if [[ "${credential_mode}" == "none" ]]; then
  echo "Context Retriever surface '${surface_name}' is ready (${surface_id}); credential provisioning was skipped."
  exit 0
fi

if [[ "${credential_mode}" == "tenant-jwt" ]]; then
  if [[ -f "${tenant_credentials_path}" ]]; then
    if jq -e --arg surface_id "${surface_id}" '
        .surfaceId == $surface_id and
        (.tenantId | type == "string" and length > 0) and
        (.contextRetrieverAgentKey | startswith("cs_agent_")) and
        (.contextRetrieverCredentialVersion | type == "string" and length > 0)
      ' "${tenant_credentials_path}" >/dev/null; then
      echo "Tenant Context Retriever credential already exists; reusing it."
      exit 0
    fi
    echo "Existing tenant credential bundle does not match surface '${surface_name}'." >&2
    exit 1
  fi

  agent_list_output="$(ctxctl --no-color --output json agent list --surface-id "${surface_id}" --page-size 100)"
  if jq -e 'type == "array"' >/dev/null 2>&1 <<<"${agent_list_output}"; then
    existing_agents="${agent_list_output}"
  elif [[ "${agent_list_output}" == *"No agent"*"found"* ]]; then
    existing_agents='[]'
  else
    echo "ctxctl returned an unexpected agent-list response." >&2
    exit 1
  fi

  tenant_id="$(jq -er 'map(.tenant_id) | unique | if length == 1 then .[0] else error("profiles span multiple tenants") end' "${user_profiles_path}")"
  agent_name="redis-iris-devui-${tenant_id}-jwt"
  if [[ "$(jq --arg name "${agent_name}" '[.[] | select(.name == $name)] | length' <<<"${existing_agents}")" -ne 0 ]]; then
    echo "Tenant agent '${agent_name}' already exists but its key is not in ${tenant_credentials_path}." >&2
    echo "Delete or rotate that agent explicitly before regenerating the local credential bundle." >&2
    exit 1
  fi

  access_tags="$(jq -cn --arg tenant "${tenant_id}" '{tenant_id: [$tenant]}')"
  agent_output="$(ctxctl --no-color --output json agent create \
    --surface-id "${surface_id}" \
    --name "${agent_name}" \
    --description "DevUI tenant credential for delegated JWT authorization in ${tenant_id}" \
    --access-tags "${access_tags}")"
  agent_key="$(jq -er '.key | select(startswith("cs_agent_"))' <<<"${agent_output}")"
  credential_version="$(jq -er '.id' <<<"${agent_output}")"
  credentials_tmp="$(mktemp "${artifact_dir}/redis-iris-travel-tenant-agent.XXXXXX")"
  trap 'rm -f "${credentials_tmp}"' EXIT
  jq -cn \
    --arg surface_id "${surface_id}" \
    --arg surface_name "${surface_name}" \
    --arg tenant_id "${tenant_id}" \
    --arg agent_key "${agent_key}" \
    --arg credential_version "${credential_version}" \
    '{surfaceId:$surface_id, surfaceName:$surface_name, tenantId:$tenant_id,
      contextRetrieverAgentKey:$agent_key,
      contextRetrieverCredentialVersion:$credential_version}' > "${credentials_tmp}"
  chmod 0600 "${credentials_tmp}"
  mv "${credentials_tmp}" "${tenant_credentials_path}"
  trap - EXIT
  echo "Wrote the tenant-scoped JWT test credential to ${tenant_credentials_path} (mode 0600)."
  exit 0
fi

if [[ ! -f "${delegated_tokens_path}" ]]; then
  if [[ -f "${user_credentials_path}" ]]; then
    if jq -e 'type == "array" and all(.[]; (.contextRetrieverAgentKey | length > 0) and (.signInToken | length > 0))' \
      "${user_credentials_path}" >/dev/null; then
      echo "Per-traveler Context Retriever credentials already exist; reusing them."
      exit 0
    fi
    echo "Existing credential bundle is not the supported local per-traveler format." >&2
    exit 1
  fi

  agent_list_output="$(ctxctl --no-color --output json agent list --surface-id "${surface_id}" --page-size 100)"
  if jq -e 'type == "array"' >/dev/null 2>&1 <<<"${agent_list_output}"; then
    existing_agents="${agent_list_output}"
  elif [[ "${agent_list_output}" == *"No agent"*"found"* ]]; then
    existing_agents='[]'
  else
    echo "ctxctl returned an unexpected agent-list response." >&2
    exit 1
  fi

  credentials_tmp="$(mktemp "${artifact_dir}/redis-iris-travel-users.XXXXXX")"
  trap 'rm -f "${credentials_tmp}"' EXIT
  printf '[]\n' > "${credentials_tmp}"
  while IFS= read -r profile; do
    subject="$(jq -er '.subject' <<<"${profile}")"
    display_name="$(jq -er '.display_name' <<<"${profile}")"
    tenant_id="$(jq -er '.tenant_id' <<<"${profile}")"
    traveler_id="$(jq -er '.traveler_id' <<<"${profile}")"
    agent_name="redis-iris-devui-${subject}"
    if [[ "$(jq --arg name "${agent_name}" '[.[] | select(.name == $name)] | length' <<<"${existing_agents}")" -ne 0 ]]; then
      echo "Agent '${agent_name}' already exists but its key is not in the local bundle." >&2
      exit 1
    fi
    access_tags="$(jq -cn --arg tenant "${tenant_id}" --arg traveler "${traveler_id}" '{tenant_id: [$tenant], traveler_id: [$traveler]}')"
    agent_output="$(ctxctl --no-color --output json agent create --surface-id "${surface_id}" --name "${agent_name}" \
      --description "DevUI credential for ${tenant_id}/${traveler_id}" --access-tags "${access_tags}")"
    agent_key="$(jq -er '.key | select(startswith("cs_agent_"))' <<<"${agent_output}")"
    credential_version="$(jq -er '.id' <<<"${agent_output}")"
    sign_in_token="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"
    jq --arg subject "${subject}" --arg display_name "${display_name}" --arg tenant_id "${tenant_id}" \
      --arg traveler_id "${traveler_id}" --arg sign_in_token "${sign_in_token}" --arg agent_key "${agent_key}" \
      --arg credential_version "${credential_version}" '. + [{subject:$subject, displayName:$display_name,
      tenantId:$tenant_id, travelerId:$traveler_id, signInToken:$sign_in_token,
      contextRetrieverAgentKey:$agent_key, contextRetrieverCredentialVersion:$credential_version}]' \
      "${credentials_tmp}" > "${credentials_tmp}.next"
    mv "${credentials_tmp}.next" "${credentials_tmp}"
    chmod 0600 "${credentials_tmp}"
  done < <(jq -c '.[]' "${user_profiles_path}")
  mv "${credentials_tmp}" "${user_credentials_path}"
  chmod 0600 "${user_credentials_path}"
  trap - EXIT
  echo "Wrote per-traveler local credentials to ${user_credentials_path}."
  exit 0
fi

if ! jq -e --slurpfile profiles "${user_profiles_path}" '
    type == "array" and
    length == ($profiles[0] | length) and
    all(.[];
      (.subject | type == "string" and length > 0) and
      (.token | type == "string" and length > 0) and
      (.version | type == "string" and length > 0) and
      (.expiresAt | type == "string" and length > 0) and
      . as $token | any($profiles[0][]; .subject == $token.subject))
  ' "${delegated_tokens_path}" >/dev/null; then
  echo "Delegated-user token file does not contain exactly one complete token entry per travel profile." >&2
  exit 2
fi

if [[ -f "${user_credentials_path}" ]]; then
  if ! jq -e --slurpfile profiles "${user_profiles_path}" '
      (.contextRetrieverAgentKey | type == "string" and length > 0) and
      (.contextRetrieverCredentialVersion | type == "string" and length > 0) and
      (.users | length) == ($profiles[0] | length) and
      all(.users[];
        (.signInToken | type == "string" and length > 0) and
        (.contextRetrieverDelegatedToken | type == "string" and length > 0) and
        (.contextRetrieverDelegatedTokenVersion | type == "string" and length > 0) and
        (.contextRetrieverDelegatedTokenExpiresAt | type == "string" and length > 0) and
        . as $user | any($profiles[0][];
          .subject == $user.subject and
          .tenant_id == $user.tenantId and
          .traveler_id == $user.travelerId))
    ' "${user_credentials_path}" >/dev/null; then
    echo "Generated DevUI credential bundle does not match the current traveler profiles; refusing to overwrite it." >&2
    exit 1
  fi
  refreshed_tmp="$(mktemp "${artifact_dir}/redis-iris-travel-users.refresh.XXXXXX")"
  jq --slurpfile tokens "${delegated_tokens_path}" '
    .users |= map(. as $user | ($tokens[0][] | select(.subject == $user.subject)) as $token |
      .contextRetrieverDelegatedToken = $token.token |
      .contextRetrieverDelegatedTokenVersion = $token.version |
      .contextRetrieverDelegatedTokenExpiresAt = $token.expiresAt)
    ' "${user_credentials_path}" > "${refreshed_tmp}"
  mv "${refreshed_tmp}" "${user_credentials_path}"
  chmod 0600 "${user_credentials_path}"
  echo "Tenant agent credential already exists; refreshed delegated JWTs from ${delegated_tokens_path}."
  exit 0
fi

agent_list_output="$(ctxctl --no-color --output json agent list --surface-id "${surface_id}" --page-size 100)"
if jq -e 'type == "array"' >/dev/null 2>&1 <<<"${agent_list_output}"; then
  existing_agents="${agent_list_output}"
elif [[ "${agent_list_output}" == *"No agent"*"found"* ]]; then
  existing_agents='[]'
else
  echo "ctxctl returned an unexpected agent-list response." >&2
  exit 1
fi

if [[ "$(jq 'length' "${user_profiles_path}")" -lt 2 ]]; then
  echo "At least two travel user profiles are required for the multi-user demo." >&2
  exit 1
fi

tenant_id="$(jq -er 'map(.tenant_id) | unique | if length == 1 then .[0] else error("profiles span multiple tenants") end' "${user_profiles_path}")"
agent_name="redis-iris-devui-${tenant_id}"
existing_count="$(jq --arg name "${agent_name}" '[.[] | select(.name == $name)] | length' <<<"${existing_agents}")"
if [[ "${existing_count}" -ne 0 ]]; then
  echo "Tenant agent '${agent_name}' already exists but its full key is not in ${user_credentials_path}." >&2
  echo "Delete or rotate that agent explicitly before regenerating the local credential bundle." >&2
  exit 1
fi

access_tags="$(jq -cn --arg tenant "${tenant_id}" '{tenant_id: [$tenant]}')"
agent_output="$(ctxctl --no-color --output json agent create \
  --surface-id "${surface_id}" \
  --name "${agent_name}" \
  --description "DevUI tenant credential for ${tenant_id}" \
  --access-tags "${access_tags}")"
agent_key="$(jq -er '.key | select(startswith("cs_agent_"))' <<<"${agent_output}")"
credential_version="$(jq -er '.id' <<<"${agent_output}")"

credentials_tmp="$(mktemp "${artifact_dir}/redis-iris-travel-users.XXXXXX")"
trap 'rm -f "${credentials_tmp}"' EXIT
printf '{"users":[]}\n' > "${credentials_tmp}"

while IFS= read -r profile; do
  subject="$(jq -er '.subject' <<<"${profile}")"
  display_name="$(jq -er '.display_name' <<<"${profile}")"
  profile_tenant_id="$(jq -er '.tenant_id' <<<"${profile}")"
  traveler_id="$(jq -er '.traveler_id' <<<"${profile}")"
  delegated_token="$(jq -er --arg subject "${subject}" '.[] | select(.subject == $subject) | .token' "${delegated_tokens_path}")"
  delegated_token_version="$(jq -er --arg subject "${subject}" '.[] | select(.subject == $subject) | .version' "${delegated_tokens_path}")"
  delegated_token_expires_at="$(jq -er --arg subject "${subject}" '.[] | select(.subject == $subject) | .expiresAt' "${delegated_tokens_path}")"
  sign_in_token="$(python3 -c 'import secrets; print(secrets.token_urlsafe(32))')"

  jq --arg subject "${subject}" \
     --arg display_name "${display_name}" \
     --arg tenant_id "${profile_tenant_id}" \
     --arg traveler_id "${traveler_id}" \
     --arg sign_in_token "${sign_in_token}" \
     --arg delegated_token "${delegated_token}" \
     --arg delegated_token_version "${delegated_token_version}" \
     --arg delegated_token_expires_at "${delegated_token_expires_at}" \
     '.users += [{
       subject: $subject,
       displayName: $display_name,
       tenantId: $tenant_id,
       travelerId: $traveler_id,
       signInToken: $sign_in_token,
       contextRetrieverDelegatedToken: $delegated_token,
       contextRetrieverDelegatedTokenVersion: $delegated_token_version,
       contextRetrieverDelegatedTokenExpiresAt: $delegated_token_expires_at
     }]' "${credentials_tmp}" > "${credentials_tmp}.next"
  mv "${credentials_tmp}.next" "${credentials_tmp}"
  chmod 0600 "${credentials_tmp}"
done < <(jq -c '.[]' "${user_profiles_path}")

jq --arg agent_key "${agent_key}" --arg credential_version "${credential_version}" \
  '. + {contextRetrieverAgentKey: $agent_key, contextRetrieverCredentialVersion: $credential_version}' \
  "${credentials_tmp}" > "${credentials_tmp}.next"
mv "${credentials_tmp}.next" "${credentials_tmp}"

mv "${credentials_tmp}" "${user_credentials_path}"
chmod 0600 "${user_credentials_path}"
trap - EXIT
echo "Wrote the DevUI credential bundle to ${user_credentials_path} (mode 0600)."
echo "Created one tenant-scoped Context Retriever agent; delegated JWTs provide traveler-level narrowing."
