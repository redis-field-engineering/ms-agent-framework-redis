"""Check Docker's real context filtering using synthetic files, never local secrets."""
from pathlib import Path
import subprocess
import tempfile


root = Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory(prefix="iris-docker-context-") as directory:
    context = Path(directory) / "context"
    output = Path(directory) / "output"
    context.mkdir()
    (context / ".dockerignore").write_bytes((root / ".dockerignore").read_bytes())
    (context / "Dockerfile").write_text("FROM scratch\nCOPY . /\n")
    sensitive = [
        "infra/aws-cognito-jwt/terraform.tfstate",
        "infra/aws-cognito-jwt/terraform.tfstate.backup",
        "infra/aws-cognito-jwt/terraform.tfvars",
        "infra/aws-cognito-jwt/.terraform/terraform.tfstate",
        "nested/terraform.tfstate",
        "nested/terraform.tfstate.backup",
        "nested/demo.tfvars",
        "nested/demo.tfvars.json",
        "nested/.terraform/provider",
        "secrets/synthetic.json",
        ".env.jwt.local",
    ]
    for name in sensitive + ["src/synthetic.cs"]:
        path = context / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("synthetic sentinel\n")
    subprocess.run([
        "docker", "build", "--output", f"type=local,dest={output}", str(context)
    ], check=True)
    assert (output / "src/synthetic.cs").is_file(), "Build omitted ordinary source"
    for name in sensitive:
        assert not (output / name).exists(), f"Sensitive path included: {name}"
print("Docker context excludes synthetic credential and Terraform files")
