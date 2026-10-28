#!/usr/bin/env python3
"""Add (or verify) a release entry in a Jellyfin plugin repository manifest.

Usage:
  scripts/update-manifest.py --manifest catalog/manifest.json --zip dist/smartmatch_1.0.0.0-jf12.zip \
      --url https://github.com/OWNER/REPO/releases/download/v1.0.0.0/smartmatch_1.0.0.0-jf12.zip \
      [--version 1.0.0.0] [--changelog "..."] [--timestamp 2026-10-28T00:00:00Z]

Published versions are immutable: re-running for an existing version is accepted only when the
checksum and URL are identical (safe republish); anything else fails.
"""

from __future__ import annotations

import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from smartmatch_build import md5, read_build_yaml, validate_manifest, validate_zip  # noqa: E402


def main() -> int:
    meta = read_build_yaml()
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--zip", required=True)
    parser.add_argument("--url", required=True)
    parser.add_argument("--version", default=meta["version"])
    parser.add_argument("--changelog", default=meta["changelog"])
    parser.add_argument("--allow-http", action="store_true", help="local test catalogues only")
    parser.add_argument("--timestamp", default=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
    args = parser.parse_args()

    zip_path = Path(args.zip)
    problems = validate_zip(zip_path, args.version, meta["guid"])
    if problems:
        print("ZIP validation failed:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1

    if not args.url.endswith(zip_path.name):
        print("error: --url must point at the same ZIP file name", file=sys.stderr)
        return 1

    manifest_path = Path(args.manifest)
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else []
    plugin = next((p for p in manifest if p.get("guid") == meta["guid"]), None)
    if plugin is None:
        plugin = {"guid": meta["guid"], "versions": []}
        manifest.append(plugin)

    # Plugin-level metadata always follows build.yaml.
    plugin.update({
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
    })

    entry = {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.url,
        "checksum": md5(zip_path),
        "timestamp": args.timestamp,
    }
    existing = next((v for v in plugin["versions"] if v["version"] == args.version), None)
    if existing is not None:
        if existing["checksum"] != entry["checksum"] or existing["sourceUrl"] != entry["sourceUrl"]:
            print(f"error: version {args.version} is already published with a different checksum or URL; "
                  "published versions are immutable, bump the version instead", file=sys.stderr)
            return 1
        print(f"version {args.version} already present with identical checksum; nothing to do")
    else:
        plugin["versions"].append(entry)

    plugin["versions"].sort(key=lambda v: tuple(int(x) for x in v["version"].split(".")), reverse=True)
    # Field order matches the Jellyfin repository format for readability.
    ordered = [{
        "guid": p["guid"], "name": p["name"], "description": p["description"], "overview": p["overview"],
        "owner": p["owner"], "category": p["category"], "versions": p["versions"],
    } for p in manifest]

    problems = validate_manifest(ordered, meta["guid"], allow_http=args.allow_http)
    if problems:
        print("manifest validation failed:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1

    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(ordered, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"updated {manifest_path}: {args.version} md5 {entry['checksum']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
