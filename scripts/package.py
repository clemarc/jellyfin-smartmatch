#!/usr/bin/env python3
"""Build the installable plugin ZIP.

Usage: scripts/package.py [--version 1.0.0.0] [--output dist] [--skip-build]

Produces dist/smartmatch_<version>-jf12.zip (plugin DLL + meta.json, nothing else),
dist/SHA256SUMS.txt and dist/smartmatch_<version>-jf12.zip.md5. The ZIP is byte-for-byte
reproducible for identical inputs: entries are sorted and carry a fixed timestamp.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from smartmatch_build import PLUGIN_DLL, ROOT, md5, read_build_yaml, sha256, validate_zip  # noqa: E402

FIXED_ZIP_TIME = (2026, 1, 1, 0, 0, 0)


def main() -> int:
    meta = read_build_yaml()
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default=meta["version"])
    parser.add_argument("--output", default=str(ROOT / "dist"))
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--skip-build", action="store_true", help="package an existing build output")
    parser.add_argument("--timestamp", default=os.environ.get("SOURCE_DATE_EPOCH"))
    args = parser.parse_args()

    version = args.version
    out = Path(args.output)
    out.mkdir(parents=True, exist_ok=True)
    publish = out / "publish"

    if not args.skip_build:
        cmd = [
            "dotnet", "publish", str(ROOT / "src/Jellyfin.Plugin.SmartMatch/Jellyfin.Plugin.SmartMatch.csproj"),
            "-c", args.configuration, "-o", str(publish), "--nologo",
            f"-p:Version={version}", f"-p:AssemblyVersion={version}", f"-p:FileVersion={version}",
            "-p:ContinuousIntegrationBuild=true", "-p:DebugType=none",
        ]
        subprocess.run(cmd, check=True)

    dll = publish / PLUGIN_DLL
    if not dll.exists():
        print(f"error: {dll} not found", file=sys.stderr)
        return 1

    if args.timestamp:
        stamp = datetime.fromtimestamp(int(args.timestamp), tz=timezone.utc)
    else:
        stamp = datetime(2026, 1, 1, tzinfo=timezone.utc)

    manifest = {
        "category": meta["category"],
        "changelog": meta["changelog"],
        "description": meta["description"],
        "guid": meta["guid"],
        "name": meta["name"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "targetAbi": meta["targetAbi"],
        "timestamp": stamp.strftime("%Y-%m-%dT%H:%M:%S.0000000Z"),
        "version": version,
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "",
        "assemblies": [PLUGIN_DLL],
    }

    zip_path = out / f"smartmatch_{version}-jf12.zip"
    entries = {
        "meta.json": (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode("utf-8"),
        PLUGIN_DLL: dll.read_bytes(),
    }
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as z:
        for name in sorted(entries):
            info = zipfile.ZipInfo(name, date_time=FIXED_ZIP_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            z.writestr(info, entries[name])

    problems = validate_zip(zip_path, version, meta["guid"])
    if problems:
        print("ZIP validation failed:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 1

    (out / "SHA256SUMS.txt").write_text(f"{sha256(zip_path)}  {zip_path.name}\n", encoding="utf-8")
    (out / f"{zip_path.name}.md5").write_text(md5(zip_path) + "\n", encoding="utf-8")
    print(f"{zip_path}\n  sha256 {sha256(zip_path)}\n  md5    {md5(zip_path)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
