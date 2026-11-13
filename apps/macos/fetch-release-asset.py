#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""Fetch an exact repository release asset and publish it with its verified checksum."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile


class ReleaseAssetError(RuntimeError):
    """An asset's identity, transfer or content did not match the requested input."""


def fetch_asset(arguments: argparse.Namespace) -> dict[str, object]:
    """Download through gh authentication; replace a local candidate only after validation."""
    repository, asset_id, digest = arguments.repository, arguments.asset_id, arguments.sha256
    output = arguments.output.absolute()
    if (not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository)
            or any(part in (".", "..") for part in repository.split("/"))):
        raise ReleaseAssetError("Expected one owner/repository name.")
    if not re.fullmatch(r"[1-9][0-9]*", asset_id) or not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise ReleaseAssetError("Expected a positive asset ID and an exact lowercase SHA-256 digest.")
    checksum = output.with_name(output.name + ".sha256")
    if output.is_symlink() or checksum.is_symlink():
        raise ReleaseAssetError("Candidate output and checksum cannot be symbolic links.")
    endpoint = f"repos/{repository}/releases/assets/{asset_id}"
    metadata = subprocess.run(["gh", "api", endpoint], capture_output=True, text=True, timeout=30)
    if metadata.returncode:
        raise ReleaseAssetError(f"Asset {asset_id} metadata request failed: {metadata.stderr.strip()}")
    asset = json.loads(metadata.stdout)
    if (asset.get("id") != int(asset_id) or asset.get("state") != "uploaded"
            or asset.get("name") != output.name or asset.get("digest") != "sha256:" + digest
            or type(asset.get("size")) is not int or asset["size"] <= 0):
        raise ReleaseAssetError(f"Release asset {asset_id} does not match the requested name, state, size and digest.")
    output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".sloptank-release-asset-", dir=output.parent) as directory:
        pending = Path(directory) / output.name
        with pending.open("xb") as stream:
            transfer = subprocess.run(["gh", "api", "-H", "Accept: application/octet-stream", endpoint],
                                      stdout=stream, stderr=subprocess.PIPE, timeout=600)
        if transfer.returncode:
            raise ReleaseAssetError(f"Asset {asset_id} transfer failed: {transfer.stderr.decode(errors='replace')}")
        actual = hashlib.sha256()
        with pending.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                actual.update(block)
        if pending.stat().st_size != asset["size"] or actual.hexdigest() != digest:
            raise ReleaseAssetError(f"Release asset {asset_id} failed the downloaded size or SHA-256 check.")
        pending_checksum = Path(directory) / checksum.name
        pending_checksum.write_text(f"{digest}  {output.name}\n", encoding="ascii")
        os.replace(pending, output)
        os.replace(pending_checksum, checksum)
    return {"repository": repository, "asset_id": int(asset_id), "sha256": digest,
            "bytes": asset["size"], "output": str(output)}


def main() -> int:
    """Resolve an authenticated asset by ID, without accepting arbitrary download URLs."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--asset-id", required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        result = fetch_asset(arguments)
    except Exception as error:
        print(f"Release asset fetch failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
