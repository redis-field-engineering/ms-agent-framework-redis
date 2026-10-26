#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_file="${REDIS_IRIS_ENV_FILE:-${repo_root}/.env.local}"

if [[ ! -f "${env_file}" ]]; then
  echo "Missing ${env_file}. Create it from the sample README first." >&2
  exit 2
fi

set -a
# shellcheck disable=SC1090
source "${env_file}"
set +a

mode="${1:-full}"
required=(REDIS_IRIS_ENDPOINT REDIS_IRIS_STORE_ID REDIS_IRIS_API_KEY)
if [[ "${mode}" != "clear" ]]; then
  required+=(OPENAI_API_KEY)
fi
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "Missing required value ${name} in ${env_file}." >&2
    exit 2
  fi
done

exec dotnet run --project "${repo_root}/samples/RedisIrisPersistentAgent/RedisIrisPersistentAgent.csproj" -- "${mode}"
