"""Manifest and recover a validated preview set; publication is always explicit."""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.error
import urllib.request
from xml.etree import ElementTree as ET
from zipfile import ZipFile

ORDER = ["Redis.Iris.AgentMemory", "Redis.Iris.ContextRetriever.Modeling",
         "Redis.Iris.ContextRetriever", "Microsoft.Agents.AI.Redis.Iris"]
MANIFEST = "release-manifest.json"
spec = importlib.util.spec_from_file_location("package_validator", Path(__file__).with_name("validate-packages.py"))
validator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator)


def content_map(data):
    # NuGet.org adds a repository signature. Compare every other archive entry,
    # independent of ZIP timestamps/compression, without extracting any files.
    with ZipFile(io.BytesIO(data)) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate archive entries")
        return {entry.filename: hashlib.sha256(archive.read(entry)).hexdigest()
                for entry in archive.infolist()
                if not entry.is_dir() and entry.filename != ".signature.p7s"}


def describe(directory, commit):
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Expected a full Git commit ID")
    directory = Path(directory)
    version = validator.validate(directory)
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+", version):
        raise ValueError("Expected a preview package version")
    packages = []
    for package_id in ORDER:
        filename = f"{package_id}.{version}.nupkg"
        data = (directory / filename).read_bytes()
        with ZipFile(io.BytesIO(data)) as archive:
            nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
            metadata = ET.fromstring(archive.read(nuspec)).find("{*}metadata")
            repository = metadata.find("{*}repository")
            if repository is None or repository.get("commit") != commit:
                raise ValueError(f"Package commit mismatch: {package_id}")
            if metadata.findtext("{*}id") != package_id:
                raise ValueError(f"Package filename/ID mismatch: {package_id}")
        packages.append({"id": package_id, "file": filename,
                         "sha256": hashlib.sha256(data).hexdigest(), "contents": content_map(data)})
    return {"schema": 1, "commit": commit, "version": version, "packages": packages}


def write_manifest(directory, commit):
    manifest = describe(directory, commit)
    with (Path(directory) / MANIFEST).open("x") as stream:
        json.dump(manifest, stream, indent=2, sort_keys=True)
        stream.write("\n")
    return manifest


def verify_manifest(directory, commit):
    manifest = json.loads((Path(directory) / MANIFEST).read_text())
    if manifest != describe(directory, commit):
        raise ValueError("Release manifest does not match the commit and package bytes")
    return manifest


def download_existing(package_id, version):
    filename = f"{package_id.lower()}.{version.lower()}.nupkg"
    url = f"https://api.nuget.org/v3-flatcontainer/{package_id.lower()}/{version.lower()}/{filename}"
    try:
        with urllib.request.urlopen(url, timeout=30) as response:
            return response.read()
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise RuntimeError(f"NuGet lookup failed for {package_id}: HTTP {error.code}") from None
    except urllib.error.URLError:
        raise RuntimeError(f"NuGet lookup unavailable for {package_id}; no packages pushed") from None


def plan_release(directory, commit, lookup=download_existing):
    manifest = verify_manifest(directory, commit)
    plan = []
    # Preflight the entire set before any push, so conflicting existing versions
    # cannot be hidden by duplicate suppression or cause avoidable partial uploads.
    for package in manifest["packages"]:
        existing = lookup(package["id"], manifest["version"])
        if existing is not None and content_map(existing) != package["contents"]:
            raise ValueError(f"Published package content conflicts: {package['id']}; use a new version")
        plan.append((Path(directory) / package["file"], existing is not None))
    return plan


def push_package(path):
    key = os.environ.get("NUGET_API_KEY")
    if not key:
        raise ValueError("Missing NUGET_API_KEY")
    # Never emit CalledProcessError or command/child output containing credentials.
    result = subprocess.run(["dotnet", "nuget", "push", str(path), "--api-key", key,
                             "--source", "https://api.nuget.org/v3/index.json"],
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise RuntimeError(f"NuGet push failed for {path.name} (exit {result.returncode}); inspect feed indexing before retrying")


def publish_plan(plan, push=push_package):
    for path, exists in plan:
        if not exists:
            push(path)
        print(f"{'Verified existing' if exists else 'Published'} {path.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["manifest", "plan", "publish"])
    parser.add_argument("directory", type=Path)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    try:
        if args.command == "manifest":
            write_manifest(args.directory, args.commit)
            print("Recorded release package hashes and source commit")
        else:
            plan = plan_release(args.directory, args.commit)
            if args.command == "publish":
                publish_plan(plan)
            else:
                for path, exists in plan:
                    print(f"{'Verified existing' if exists else 'Missing'} {path.name}")
    except (ValueError, OSError, RuntimeError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
