#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${CONTEXT_RETRIEVER_ARTIFACT_DIR:-/artifacts}"
seed_path="${artifact_dir}/redis-iris-travel-context-seed.json"

mkdir -p "${artifact_dir}"
dotnet /app/publish/RedisIrisDevUI.dll --print-context-seed > "${seed_path}"
echo "Generated Context Retriever seed data: ${seed_path}"
python3 /usr/local/lib/seed-context-retriever.py "${seed_path}"
