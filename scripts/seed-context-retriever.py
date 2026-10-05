#!/usr/bin/env python3
"""Import the DevUI's synthetic travel records into a Context Retriever surface."""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import httpx


ENTITY_GROUPS = (
    ("travelers", "Traveler"),
    ("flights", "FlightReservation"),
    ("hotels", "HotelReservation"),
    ("receipts", "TravelReceipt"),
)


def required_environment(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        raise ValueError(f"Missing required Context Retriever seeding value: {name}")
    return value


def find_surface(client: httpx.Client, surface_name: str) -> dict[str, Any]:
    matches: list[dict[str, Any]] = []
    page = 1

    while True:
        response = client.get("/api/v1/context-surfaces", params={"page": page, "page_size": 100})
        response.raise_for_status()
        body = response.json()
        matches.extend(
            surface for surface in body.get("context_surfaces", []) if surface.get("name") == surface_name
        )
        pagination = body.get("pagination", {})
        if not pagination.get("has_next", False):
            break
        page += 1

    if not matches:
        raise RuntimeError(
            f"Context Retriever surface '{surface_name}' does not exist; run the context-provisioner first."
        )
    if len(matches) > 1:
        raise RuntimeError(
            f"Found {len(matches)} surfaces named '{surface_name}'; refusing to choose one."
        )
    return matches[0]


def seed(seed_path: Path) -> None:
    configured_admin_url = required_environment("CONTEXT_RETRIEVER_ADMIN_URL")
    if urlparse(configured_admin_url).path.rstrip("/").endswith("/mcp"):
        raise ValueError(
            "CONTEXT_RETRIEVER_ADMIN_URL must be the control-plane base URL, not the runtime MCP endpoint."
        )
    admin_url = configured_admin_url.rstrip("/") + "/"
    admin_key = required_environment("CONTEXT_RETRIEVER_ADMIN_KEY")
    surface_name = os.environ.get("CONTEXT_RETRIEVER_SURFACE_NAME", "redis-iris-travel-assistant")
    records_by_group = json.loads(seed_path.read_text(encoding="utf-8"))

    with httpx.Client(
        base_url=admin_url,
        headers={"X-API-Key": admin_key, "Accept": "application/json"},
        timeout=30.0,
    ) as client:
        surface = find_surface(client, surface_name)
        surface_id = surface.get("id")
        if not isinstance(surface_id, str) or not surface_id:
            raise RuntimeError(f"Surface '{surface_name}' did not include a valid id.")

        imported_total = 0
        for group_name, entity_name in ENTITY_GROUPS:
            records = records_by_group.get(group_name)
            if not isinstance(records, list) or not records:
                raise ValueError(f"Seed data group '{group_name}' must be a non-empty array.")

            response = client.post(
                f"api/v1/context-surfaces/{surface_id}/data",
                json={
                    "entity": entity_name,
                    "records": records,
                    "options": {"on_conflict": "overwrite", "on_error": "fail_fast"},
                },
            )
            response.raise_for_status()
            result = response.json()
            failed = result.get("failed", 0)
            imported = result.get("imported", 0)
            if failed or imported != len(records):
                errors = result.get("errors", [])
                raise RuntimeError(
                    f"Import of {entity_name} returned imported={imported}, failed={failed}, errors={errors}"
                )
            imported_total += imported
            print(f"Seeded {imported} {entity_name} record(s).")

        print(f"Seeded {imported_total} record(s) into '{surface_name}' ({surface_id}).")


def main() -> int:
    if len(sys.argv) != 2:
        print(f"Usage: {Path(sys.argv[0]).name} SEED_JSON", file=sys.stderr)
        return 2
    try:
        seed(Path(sys.argv[1]))
    except (ValueError, RuntimeError, OSError, json.JSONDecodeError, httpx.HTTPError) as error:
        print(f"Context Retriever seeding failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
