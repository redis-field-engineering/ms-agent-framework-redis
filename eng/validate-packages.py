"""Validate the complete release set, dependency closure, and archive contents."""
import re
import sys
from pathlib import Path
from xml.etree import ElementTree as ET
from zipfile import ZipFile

EXPECTED = {
    "Redis.Iris.AgentMemory",
    "Redis.Iris.ContextRetriever.Modeling",
    "Redis.Iris.ContextRetriever",
    "Microsoft.Agents.AI.Redis.Iris",
}


def validate(directory):
    packages = {}
    for path in Path(directory).glob("*.nupkg"):
        with ZipFile(path) as archive:
            names = archive.namelist()
            specs = [name for name in names if name.endswith(".nuspec")]
            if len(specs) != 1:
                raise ValueError(f"Expected one nuspec: {path.name}")
            metadata = ET.fromstring(archive.read(specs[0])).find("{*}metadata")
            package_id = metadata.findtext("{*}id")
            version = metadata.findtext("{*}version")
            if package_id not in EXPECTED or package_id in packages:
                raise ValueError(f"Unexpected or duplicate package: {package_id}")
            if not any(name.startswith("lib/") and name.endswith(f"/{package_id}.dll") for name in names):
                raise ValueError(f"Missing library: {package_id}")
            for name in names:
                if re.search(r"(^|/)(tests?|TestResults|bin|obj)/|\.env($|\.)", name, re.I):
                    raise ValueError(f"Development content: {path.name}/{name}")
                if re.search(rb"test-secret|private-token|BEGIN (RSA |EC )?PRIVATE KEY", archive.read(name), re.I):
                    raise ValueError(f"Potential test credential: {path.name}/{name}")
            dependencies = [(dep.attrib["id"], dep.attrib["version"])
                            for dep in metadata.findall(".//{*}dependency")]
            packages[package_id] = (version, dependencies)
    if packages.keys() != EXPECTED:
        raise ValueError(f"Missing release packages: {', '.join(sorted(EXPECTED - packages.keys()))}")
    versions = {value[0] for value in packages.values()}
    if len(versions) != 1:
        raise ValueError("Release package versions must match")
    version = versions.pop()
    for package_id, (_, dependencies) in packages.items():
        for dependency_id, required in dependencies:
            if dependency_id in EXPECTED and required not in (version, f"[{version}]", f"[{version}, )"):
                raise ValueError(f"Dependency version mismatch: {package_id} -> {dependency_id} {required}")
    return version


if __name__ == "__main__":
    try:
        print(validate(sys.argv[1]))
    except (ValueError, OSError) as error:
        sys.exit(str(error))
