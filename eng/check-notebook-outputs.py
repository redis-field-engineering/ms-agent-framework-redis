"""Reject saved output and execution counts in tracked notebooks without executing them."""
import json
from pathlib import Path
import subprocess
import sys


def main():
    root = Path(__file__).resolve().parents[1]
    paths = subprocess.check_output(
        ["git", "ls-files", "-z", "--", "*.ipynb"], cwd=root,
    ).decode().split("\0")
    errors = []
    checked = 0
    for name in filter(None, paths):
        checked += 1
        try:
            notebook = json.loads((root / name).read_text(encoding="utf-8"))
        except (OSError, UnicodeError, ValueError):
            errors.append(f"{name}: cannot read notebook JSON")
            continue
        if not isinstance(notebook, dict) or not isinstance(notebook.get("cells"), list):
            errors.append(f"{name}: invalid notebook structure")
            continue
        for index, cell in enumerate(notebook["cells"], start=1):
            if not isinstance(cell, dict):
                errors.append(f"{name}: cell {index} has an invalid structure")
            elif cell.get("cell_type") == "code" and (
                cell.get("outputs") or cell.get("execution_count") is not None
            ):
                errors.append(f"{name}: cell {index} has saved output or an execution count; clear before committing")
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print(f"All {checked} tracked notebooks have no saved output or execution counts.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
