#!/usr/bin/env python3
"""Run required live suites; absent credentials, skips, and empty test runs fail release."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from xml.etree import ElementTree as ET

suite = sys.argv[1] if len(sys.argv) > 1 else ""
suites = {
    "managed": ("Redis.Iris.AgentMemory.LiveTests", "ManagedAgentMemoryLiveTests", 5,
                ["REDIS_IRIS_ENDPOINT", "REDIS_IRIS_STORE_ID", "REDIS_IRIS_API_KEY", "REDIS_IRIS_RUN_PROMOTION_TESTS"]),
    "delegated": ("Redis.Iris.AgentMemory.LiveTests", "DelegatedAgentMemoryLiveTests", 3,
                  ["REDIS_IRIS_ENDPOINT", "REDIS_IRIS_STORE_ID", "REDIS_IRIS_API_KEY", "REDIS_IRIS_RAM_DELEGATED_USERS_FILE"]),
    "context": ("Redis.Iris.ContextRetriever.LiveTests", "ManagedContextRetrieverLiveTests", 3,
                ["REDIS_IRIS_CONTEXT_RETRIEVER_MCP_ENDPOINT", "REDIS_IRIS_CONTEXT_RETRIEVER_USERS_FILE"]),
}
if suite not in suites:
    sys.exit("Usage: live-release-gate.py managed|delegated|context")
project, test_class, expected, required = suites[suite]
missing = [name for name in required if not os.environ.get(name)]
if missing:
    sys.exit("Missing required live settings: " + ", ".join(missing))
if suite == "managed" and os.environ["REDIS_IRIS_RUN_PROMOTION_TESTS"] != "1":
    sys.exit("The managed release gate requires REDIS_IRIS_RUN_PROMOTION_TESTS=1")
root = Path(__file__).resolve().parent.parent
with tempfile.TemporaryDirectory() as directory:
    result = subprocess.run(["dotnet", "test", str(root / "tests" / project), "-c", "Release", "-f", "net10.0",
                             "--filter", "FullyQualifiedName~" + test_class,
                             "--logger", "trx;LogFileName=live.trx", "--results-directory", directory])
    if result.returncode:
        sys.exit(result.returncode)
    results = ET.parse(Path(directory) / "live.trx").findall(".//{*}UnitTestResult")
    if len(results) != expected or any(item.attrib["outcome"] != "Passed" for item in results):
        sys.exit(f"Release requires exactly {expected} passing {suite} tests; skips/empty runs are not evidence.")
print(f"{suite} live release gate passed")
