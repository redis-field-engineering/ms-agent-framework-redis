#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_file="${repo_root}/.env.local"

if [[ ! -f "${env_file}" ]]; then
  echo "Missing ${env_file}. Create it from the sample README first." >&2
  exit 2
fi

set -a
# shellcheck disable=SC1090
source "${env_file}"
set +a

required=(REDIS_IRIS_ENDPOINT REDIS_IRIS_STORE_ID REDIS_IRIS_API_KEY OPENAI_API_KEY)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "Missing required value ${name} in ${env_file}." >&2
    exit 2
  fi
done

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-http://localhost:5080}"
exec dotnet run --project "${repo_root}/samples/RedisIrisDevUI/RedisIrisDevUI.csproj" --no-launch-profile
