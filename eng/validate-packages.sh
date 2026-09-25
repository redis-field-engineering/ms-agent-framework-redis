#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:-artifacts}"
shopt -s nullglob
packages=("${artifact_dir}"/*.nupkg)
if (( ${#packages[@]} == 0 )); then
  echo "No packages found in ${artifact_dir}" >&2
  exit 1
fi

for package in "${packages[@]}"; do
  listing="$(unzip -Z1 "${package}")"
  if grep -Eiq '(^|/)(tests?|TestResults|bin|obj)/|\.env($|\.)' <<<"${listing}"; then
    echo "Unexpected development content in ${package}" >&2
    exit 1
  fi
  if unzip -p "${package}" | grep -Eiq 'test-secret|private-token|BEGIN (RSA |EC )?PRIVATE KEY'; then
    echo "Potential test credential in ${package}" >&2
    exit 1
  fi
done
