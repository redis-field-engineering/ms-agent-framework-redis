#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
terraform_dir="${repo_root}/infra/aws-cognito-jwt"
credentials_path="${CONTEXT_RETRIEVER_TENANT_CREDENTIALS_FILE:-${repo_root}/artifacts/context-retriever-jwt/redis-iris-travel-tenant-agent.json}"
output_path="${DEVUI_JWT_ENV_FILE:-${repo_root}/.env.jwt.local}"

if [[ ! -f "${credentials_path}" ]]; then
  echo "Missing tenant Context Retriever credential bundle: ${credentials_path}" >&2
  exit 2
fi

oidc_environment="$(terraform -chdir="${terraform_dir}" output -json devui_oidc_environment)"
agent_key="$(jq -er '.contextRetrieverAgentKey | select(startswith("cs_agent_"))' "${credentials_path}")"
credential_version="$(jq -er '.contextRetrieverCredentialVersion | select(length > 0)' "${credentials_path}")"

output_tmp="$(mktemp "${output_path}.XXXXXX")"
trap 'rm -f "${output_tmp}"' EXIT

jq -r 'to_entries[] | "\(.key)=\(.value)"' <<<"${oidc_environment}" > "${output_tmp}"
printf 'REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY=%s\n' "${agent_key}" >> "${output_tmp}"
printf 'REDIS_IRIS_CONTEXT_RETRIEVER_CREDENTIAL_VERSION=%s\n' "${credential_version}" >> "${output_tmp}"
chmod 0600 "${output_tmp}"
mv "${output_tmp}" "${output_path}"
trap - EXIT

echo "Wrote the JWT DevUI environment overlay to ${output_path} (mode 0600)."
