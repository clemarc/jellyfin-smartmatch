#!/usr/bin/env python3
"""Verify a published (or local) Smart Match catalogue the way a Jellyfin client sees it.

Usage:
  scripts/verify-release.py --manifest https://raw.githubusercontent.com/OWNER/REPO/catalog/manifest.json \
      [--version 1.0.0.0] [--local-zip dist/smartmatch_1.0.0.0-jf12.zip]

Checks manifest schema and invariants, downloads the ZIP for the selected version (newest by
default), verifies the MD5 recorded in the manifest, the ZIP layout, and optionally that the hosted
bytes are identical to a locally built ZIP.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import tempfile
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from smartmatch_build import read_build_yaml, validate_manifest, validate_zip  # noqa: E402


def fetch(url: str) -> bytes:
    if url.startswith(("http://", "https://")):
        with urllib.request.urlopen(url, timeout=60) as r:  # noqa: S310 - fixed https catalogue URLs
            return r.read()
    return Path(url).read_bytes()


def main() -> int:
    meta = read_build_yaml()
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, help="URL or path of manifest.json")
    parser.add_argument("--version")
    parser.add_argument("--local-zip")
    parser.add_argument("--zip-override", help="read the ZIP from this path/URL instead of sourceUrl (pre-publication check)")
    args = parser.parse_args()

    manifest = json.loads(fetch(args.manifest))
    problems = validate_manifest(manifest, meta["guid"])
    if problems:
        print("manifest invalid:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1

    plugin = next(p for p in manifest if p["guid"] == meta["guid"])
    versions = plugin["versions"]
    entry = next((v for v in versions if v["version"] == args.version), None) if args.version else versions[0]
    if entry is None:
        print(f"version {args.version} not in manifest", file=sys.stderr)
        return 1

    data = fetch(args.zip_override or entry["sourceUrl"])
    digest = hashlib.md5(data).hexdigest()  # noqa: S324 - Jellyfin manifest checksum format
    if digest != entry["checksum"]:
        print(f"checksum mismatch: manifest {entry['checksum']} vs downloaded {digest}", file=sys.stderr)
        return 1

    with tempfile.TemporaryDirectory() as tmp:
        zip_path = Path(tmp) / "plugin.zip"
        zip_path.write_bytes(data)
        problems = validate_zip(zip_path, entry["version"], meta["guid"])
    if problems:
        print("ZIP invalid:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1

    if args.local_zip:
        local = Path(args.local_zip).read_bytes()
        if local != data:
            print("hosted ZIP differs from the locally built ZIP", file=sys.stderr)
            return 1

    print(f"OK: {plugin['name']} {entry['version']} targetAbi {entry['targetAbi']} md5 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
