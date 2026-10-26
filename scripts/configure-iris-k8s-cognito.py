#!/usr/bin/env python3
"""Reuse the existing Cognito pool for the Kubernetes travel surface and DevUI.

Requires configure-iris-k8s.py setup and active port forwards. Reads Terraform
outputs only; does not apply Terraform, change Cognito, or deploy Kubernetes.
"""

import argparse
import importlib.util
import json
from pathlib import Path
import shlex
import subprocess
import sys


spec = importlib.util.spec_from_file_location(
    "iris_k8s", Path(__file__).with_name("configure-iris-k8s.py")
)
k8s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(k8s)


def terraform_output(name):
    result = subprocess.run(
        ["terraform", f"-chdir={k8s.ROOT / 'infra/aws-cognito-jwt'}", "output", "-json", name],
        capture_output=True, timeout=30,
    )
    if result.returncode:
        raise RuntimeError(f"Could not read existing Cognito Terraform output {name}.")
    return json.loads(result.stdout)


def configure():
    settings = json.loads((k8s.PRIVATE / "iris-k8s-settings.json").read_text())
    binding = json.loads((k8s.PRIVATE / "iris-k8s-context.json").read_text())
    surface_id = binding["surfaceId"]
    if binding["cluster"]["kubeconfig"] != k8s.KUBECONFIG or binding["cluster"]["namespace"] != k8s.NAMESPACE:
        raise RuntimeError("Saved sample credentials do not match the selected cluster.")
    users = binding["users"]
    tenants = {user["tenantId"] for user in users}
    if len(tenants) != 1:
        raise RuntimeError("The Cognito demo requires exactly one tenant.")
    tenant = next(iter(tenants))
    admin = k8s.cluster_secret("context-admin", "token")
    path = f"/api/v1/context-surfaces/{surface_id}"
    surface = k8s.request("GET", k8s.ADMIN, path, key=admin)
    if surface.get("metadata", {}).get("integration") != "ms-agent-framework-redis":
        raise RuntimeError("Refusing to modify a surface not owned by this integration.")

    checkpoint = k8s.PRIVATE / "iris-k8s-cognito-context.json"
    credential = json.loads(checkpoint.read_text()) if checkpoint.exists() else None
    expected = {"cluster": binding["cluster"], "surfaceId": surface_id, "tenantId": tenant}
    if credential and any(credential.get(key) != value for key, value in expected.items()):
        raise RuntimeError("Saved Cognito agent key does not match the selected surface and tenant.")
    if not credential:
        name = f"ms-agent-framework-cognito-{tenant}"
        page = 1
        while True:
            result = k8s.request("GET", k8s.ADMIN, path + f"/agent-keys?page={page}&page_size=100", key=admin)
            if any(key["name"] == name for key in result.get("agent_keys", [])):
                raise RuntimeError("Tenant agent exists but its local key is missing; recover or rotate it explicitly.")
            if not result.get("pagination", {}).get("has_next"):
                break
            page += 1
        agent = k8s.request("POST", k8s.ADMIN, path + "/agent-keys", {
            "name": name,
            "description": "Tenant-scoped application key; Cognito JWT narrows access to the traveler",
            "access_tags": {"tenant_id": [tenant]},
        }, admin)
        credential = dict(expected, contextRetrieverAgentKey=agent["key"],
                          contextRetrieverCredentialVersion=agent["id"])
        k8s.save_private(checkpoint, json.dumps(credential, indent=2) + "\n")

    before = k8s.PRIVATE / "iris-k8s-idp-before-cognito.json"
    if not before.exists():
        k8s.save_private(before, json.dumps({"surfaceId": surface_id,
                         "identity_provider": surface.get("identity_provider")}, indent=2) + "\n")
    trust = terraform_output("context_retriever_idp_configuration")
    trust.update(enabled=True, require_user_token=True, allow_insecure=False)
    k8s.request("PUT", k8s.ADMIN, path, {"identity_provider": trust}, admin)
    persisted = k8s.request("GET", k8s.ADMIN, path, key=admin).get("identity_provider", {})
    # The service omits false-valued optional booleans from JSON responses.
    if any(persisted.get(key, False if key == "allow_insecure" else None) != value
           for key, value in trust.items()):
        raise RuntimeError("CR did not persist the required Cognito trust settings.")
    print("CR surface trusts Cognito and requires a user JWT.")

    settings.update(terraform_output("devui_oidc_environment"))
    settings.update({
        "REDIS_IRIS_CONTEXT_RETRIEVER_AGENT_KEY": credential["contextRetrieverAgentKey"],
        "REDIS_IRIS_CONTEXT_RETRIEVER_CREDENTIAL_VERSION": credential["contextRetrieverCredentialVersion"],
        "DEVUI_USERS_FILE": "", "DEVUI_USERS_JSON": "",
        "DEVUI_ENABLE_ACCESS_TOKEN_INSPECTOR": "true",
        # Local traveler keys cannot authorize requests after JWT enforcement is enabled.
        "REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE": str(k8s.PRIVATE / "iris-k8s-cognito-users.json"),
    })
    k8s.save_private(k8s.PRIVATE / "iris-k8s-cognito.env",
                     "# Kubernetes endpoints with existing Cognito OIDC authentication.\n" +
                     "".join(f"export {name}={shlex.quote(value)}\n" for name, value in settings.items()))
    print("Wrote secrets/iris-k8s-cognito.env (mode 0600); RAM authentication is unchanged.")
    print("Run REDIS_IRIS_ENV_FILE=secrets/iris-k8s-cognito.env ./scripts/run-agent-memory-devui.sh")


if __name__ == "__main__":
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        configure()
    except (RuntimeError, OSError, ValueError, KeyError, subprocess.TimeoutExpired) as error:
        message = str(error) if isinstance(error, RuntimeError) else type(error).__name__
        print(f"Kubernetes Cognito setup failed: {message}", file=sys.stderr)
        sys.exit(1)
