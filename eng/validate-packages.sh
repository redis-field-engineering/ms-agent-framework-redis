#!/usr/bin/env bash
set -euo pipefail
exec python3 "$(dirname "$0")/validate-packages.py" "${1:-artifacts/packages}"
