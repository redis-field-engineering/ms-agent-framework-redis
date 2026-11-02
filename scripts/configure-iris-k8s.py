#!/usr/bin/env python3
"""Configure the native samples against an existing Iris Kubernetes deployment.

Requires authenticated cluster access and forward-iris-k8s.sh. Uses KUBECONFIG
or ~/.kube/config, and IRIS_K8S_NAMESPACE or redis-iris. Creates only a
dedicated travel surface, its scoped keys, and synthetic travel records. Never
prints credentials, changes infrastructure, or modifies the bootstrap surface.
"""

import argparse
import base64
import json
import os
from pathlib import Path
import secrets
import shlex
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parent.parent
PRIVATE = ROOT / "secrets"
NAMESPACE = os.environ.get("IRIS_K8S_NAMESPACE", "redis-iris")
KUBECONFIG = os.environ.get(
    "KUBECONFIG", str(Path.home() / ".kube/config")
)
ADMIN = "http://127.0.0.1:8082"
SURFACE_NAME = "ms-agent-framework-travel"


def save_private(path, value):
    """Atomic replacement; temp files and destination are owner-readable only."""
    PRIVATE.mkdir(mode=0o700, exist_ok=True)
    fd, temporary = tempfile.mkstemp(dir=PRIVATE)
    try:
        with os.fdopen(fd, "w") as stream:
            stream.write(value)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def cluster_secret(name, field):
    result = subprocess.run(
        ["kubectl", "--kubeconfig", KUBECONFIG, "-n", NAMESPACE,
         "get", "secret", name, "-o", "json", "--request-timeout=15s"],
        capture_output=True, timeout=45,
    )
    if result.returncode:
        raise RuntimeError("Kubernetes secret lookup failed. Check kubeconfig and cluster authentication.")
    return base64.b64decode(json.loads(result.stdout)["data"][field]).decode().strip()


def request(method, base, path, body=None, key=None):
    headers = {"Accept": "application/json"}
    if key:
        headers["X-API-Key"] = key
    if body is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(
        base + path, method=method, headers=headers,
        data=None if body is None else json.dumps(body).encode(),
    )
    try:
        with urllib.request.urlopen(req, timeout=45) as response:
            payload = response.read()
            return json.loads(payload) if payload else {}
    except urllib.error.HTTPError as error:
        # Service error bodies can echo credentials or connection configuration.
        raise RuntimeError(f"{method} {path} returned HTTP {error.code}") from None
    except urllib.error.URLError:
        raise RuntimeError(f"Cannot reach {base}; start scripts/forward-iris-k8s.sh.") from None


def sample_json(flag):
    output = subprocess.run(
        ["dotnet", "run", "--project", str(ROOT / "samples/RedisIrisDevUI"),
         "--no-build", "--no-launch-profile", "--", flag],
        capture_output=True, timeout=45,
    )
    if output.returncode:
        raise RuntimeError("Build samples/RedisIrisDevUI before running this script.")
    return json.loads(output.stdout)


def model_settings():
    # Import only literal model settings, never the old Cloud endpoints or keys.
    settings = {}
    source = ROOT / ".env.local"
    if source.exists():
        for line in source.read_text().splitlines():
            name, separator, value = line.removeprefix("export ").partition("=")
            if separator and name.strip() in ("OPENAI_API_KEY", "OPENAI_MODEL"):
                parts = shlex.split(value, comments=True)
                if len(parts) == 1:
                    settings[name.strip()] = parts[0]
    # Preserve independently configured Kubernetes model credentials on reruns.
    saved_path = PRIVATE / "iris-k8s-settings.json"
    if saved_path.exists():
        saved = json.loads(saved_path.read_text())
        for name in ("OPENAI_API_KEY", "OPENAI_MODEL"):
            if saved.get(name):
                settings[name] = saved[name]
    for name in ("OPENAI_API_KEY", "OPENAI_MODEL"):
        if os.environ.get(name):
            settings[name] = os.environ[name]
    if not settings.get("OPENAI_API_KEY"):
        raise RuntimeError("Set OPENAI_API_KEY in your environment or .env.local.")
    return settings


def configure():
    settings = model_settings()
    model = sample_json("--print-context-model")
    profiles = sample_json("--print-context-users")
    records = sample_json("--print-context-seed")
    report = request("GET", "http://127.0.0.1:8080", "/status")
    if not report.get("ok"):
        raise RuntimeError("Cluster smoke checks are not healthy; inspect the smoke service.")
    print(f"Cluster healthy: {len(report['checks'])} smoke checks.")
    bootstrap = json.loads(cluster_secret("iris-bootstrap-state", "state.json"))
    admin = cluster_secret("context-admin", "token")
    binding_path = PRIVATE / "iris-k8s-context.json"
    binding = json.loads(binding_path.read_text()) if binding_path.exists() else {}
    source_identity = {"kubeconfig": KUBECONFIG, "namespace": NAMESPACE,
                       "memoryStoreId": bootstrap["memory_id"]}
    if binding and binding.get("cluster") != source_identity:
        raise RuntimeError("Saved context credentials belong to a different cluster/store.")

    surfaces = []
    page = 1
    while True:
        result = request("GET", ADMIN, f"/api/v1/context-surfaces?page={page}&page_size=100", key=admin)
        surfaces.extend(s for s in result.get("context_surfaces", []) if s["name"] == SURFACE_NAME)
        if not result.get("pagination", {}).get("has_next"):
            break
        page += 1
    if len(surfaces) > 1:
        raise RuntimeError("Duplicate sample surfaces; refusing to choose one.")
    if surfaces:
        surface = surfaces[0]
        if surface.get("metadata", {}).get("integration") != "ms-agent-framework-redis":
            raise RuntimeError("Existing surface is not owned by this integration.")
        if binding and binding["surfaceId"] != surface["id"]:
            raise RuntimeError("Saved keys do not match the sample surface.")
        request("PUT", ADMIN, f"/api/v1/context-surfaces/{surface['id']}",
                {"data_model": model}, admin)
    else:
        if binding:
            raise RuntimeError("Saved sample surface is missing; refusing to replace its keys silently.")
        surface = request("POST", ADMIN, "/api/v1/context-surfaces", {
            "name": SURFACE_NAME,
            "description": "Synthetic travel context for Microsoft Agent Framework samples",
            "metadata": {"integration": "ms-agent-framework-redis"},
            "data_model": model,
            "data_source": {"type": "redis", "connection_config": {
                "addr": f"context.{NAMESPACE}.svc.cluster.local:6379", "username": "default",
                "password": cluster_secret("context-credentials", "password"),
                "db": 0, "tls_enabled": False,
            }},
        }, admin)
    surface_id = surface["id"]
    binding = binding or {"cluster": source_identity, "surfaceId": surface_id, "users": []}
    save_private(binding_path, json.dumps(binding, indent=2) + "\n")
    users = binding["users"]
    for profile in profiles:
        if any(user["subject"] == profile["subject"] for user in users):
            continue
        name = "ms-agent-framework-" + profile["subject"]
        existing = request("GET", ADMIN,
                           f"/api/v1/context-surfaces/{surface_id}/agent-keys?page_size=100", key=admin)
        if any(key["name"] == name for key in existing.get("agent_keys", [])):
            raise RuntimeError("Sample agent already exists but its key is missing locally; rotate it explicitly.")
        agent = request("POST", ADMIN, f"/api/v1/context-surfaces/{surface_id}/agent-keys", {
            "name": name,
            "access_tags": {"tenant_id": [profile["tenant_id"]], "traveler_id": [profile["traveler_id"]]},
        }, admin)
        users.append({
            "subject": profile["subject"], "displayName": profile["display_name"],
            "tenantId": profile["tenant_id"], "travelerId": profile["traveler_id"],
            "signInToken": secrets.token_urlsafe(32), "contextRetrieverAgentKey": agent["key"],
            "contextRetrieverCredentialVersion": agent["id"],
        })
        # Checkpoint each returned key immediately; creation is not repeated on reruns.
        save_private(binding_path, json.dumps(binding, indent=2) + "\n")
    for group, entity in (("travelers", "Traveler"), ("flights", "FlightReservation"),
                          ("hotels", "HotelReservation"), ("receipts", "TravelReceipt")):
        result = request("POST", ADMIN, f"/api/v1/context-surfaces/{surface_id}/data", {
            "entity": entity, "records": records[group],
            "options": {"on_conflict": "overwrite", "on_error": "fail_fast"},
        }, admin)
        if result.get("failed", 0) or result.get("imported") != len(records[group]):
            raise RuntimeError(f"Incomplete {entity} import.")
        print(f"Seeded {result['imported']} {entity} records.")

    users_path = PRIVATE / "iris-k8s-users.json"
    save_private(users_path, json.dumps(users, indent=2) + "\n")
    settings.update({
        "REDIS_IRIS_ENDPOINT": "http://127.0.0.1:9000/",
        "REDIS_IRIS_STORE_ID": bootstrap["memory_id"],
        "REDIS_IRIS_API_KEY": bootstrap["memory_key"]["token"],
        "REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT": "http://127.0.0.1:8081/mcp",
        "REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY": users[0]["contextRetrieverAgentKey"],
        "REDIS_IRIS_CONTEXT_RETRIEVER_CREDENTIAL_VERSION": users[0]["contextRetrieverCredentialVersion"],
        "DEVUI_USERS_FILE": str(users_path), "DEVUI_USERS_JSON": "", "DEVUI_AUTH_MODE": "token",
        "REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE": str(users_path),
        "DEVUI_ENABLE_ACCESS_TOKEN_INSPECTOR": "false",
        "SAMPLE_OWNER_ID": "ms-agent-framework-k8s-demo",
    })
    save_private(PRIVATE / "iris-k8s-settings.json", json.dumps(settings, indent=2) + "\n")
    save_private(PRIVATE / "iris-k8s.env", "# Generated by configure-iris-k8s.py; native host runners only.\n" +
                 "".join(f"export {name}={shlex.quote(value)}\n" for name, value in settings.items()))
    print("Configured secrets/iris-k8s.env and secrets/iris-k8s-users.json (mode 0600).")
    print("Select Kubernetes with REDIS_IRIS_ENV_FILE=secrets/iris-k8s.env. DevUI sign-in tokens are in the users file.")


if __name__ == "__main__":
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        configure()
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        # Never emit captured command output, request bodies, or credential values.
        message = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print(f"Kubernetes sample configuration failed: {message}", file=sys.stderr)
        sys.exit(1)
