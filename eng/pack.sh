#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
artifact_dir="${1:-artifacts/packages}"
# A fresh destination prevents stale versions from masking missing artifacts.
mkdir -p "$artifact_dir"
if find "$artifact_dir" -name '*.nupkg' -print -quit | grep -q .; then
  echo 'Package destination must contain no existing nupkg files.' >&2
  exit 1
fi
for project in Redis.Iris.AgentMemory Redis.Iris.ContextRetriever.Modeling Redis.Iris.ContextRetriever Microsoft.Agents.AI.Redis.Iris; do
  dotnet pack "src/$project" -c Release -o "$artifact_dir"
done
./eng/validate-packages.sh "$artifact_dir"
