#!/usr/bin/env bash
# Keep these forwards in the foreground; Ctrl-C cleans up every child.
set -euo pipefail

export KUBECONFIG="${KUBECONFIG:-${HOME}/.kube/config}"
namespace="${IRIS_K8S_NAMESPACE:-redis-iris}"
kubectl -n "${namespace}" get pods --request-timeout=15s >/dev/null

pids=()
cleanup() {
  if [[ ${#pids[@]} -gt 0 ]]; then
    kill "${pids[@]}" 2>/dev/null || true
    wait "${pids[@]}" 2>/dev/null || true
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

for forward in 'redis-agent-memory 9000:9000' \
               'redis-context-retriever-mcp 8081:8081' \
               'redis-context-retriever-admin 8082:8080' \
               'iris-smoke 8080:8080'; do
  read -r service ports <<<"${forward}"
  kubectl -n "${namespace}" port-forward --address=127.0.0.1 "svc/${service}" "${ports}" &
  pids+=("$!")
done

# Bash 3.2 (macOS) has no wait -n. Stop the group if any forward exits.
while true; do
  for pid in "${pids[@]}"; do
    if ! kill -0 "${pid}" 2>/dev/null; then
      echo "An Iris port forward exited; restart this script." >&2
      exit 1
    fi
  done
  sleep 1
done
