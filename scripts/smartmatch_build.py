"""Shared helpers for packaging and manifest scripts (standard library only)."""

from __future__ import annotations

import hashlib
import json
import re
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PLUGIN_DLL = "Jellyfin.Plugin.SmartMatch.dll"
GUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+\.\d+$")
# Host assemblies must never ship inside the plugin ZIP.
FORBIDDEN_PREFIXES = ("MediaBrowser.", "Jellyfin.Controller", "Jellyfin.Model", "Jellyfin.Common", "Jellyfin.Data",
                      "Jellyfin.Extensions", "Jellyfin.Naming", "Jellyfin.Database", "Microsoft.", "System.", "Emby.")
ALLOWED_ZIP_ENTRIES = {PLUGIN_DLL, "meta.json"}


def read_build_yaml(path: Path | None = None) -> dict:
    """Parse the flat build.yaml used by this repository (scalars, folded '>' blocks, simple lists)."""
    path = path or ROOT / "build.yaml"
    data: dict = {}
    key = None
    mode = None
    for raw in path.read_text(encoding="utf-8").splitlines():
        if raw.strip() in ("---", "") and mode != "folded":
            continue
        if mode == "folded" and (raw.startswith("  ") or raw.strip() == ""):
            data[key] = (data[key] + " " + raw.strip()).strip()
            continue
        if mode == "list" and raw.lstrip().startswith("- "):
            data[key].append(raw.strip()[2:].strip().strip('"'))
            continue
        mode = None
        m = re.match(r"^([A-Za-z]+):\s*(.*)$", raw)
        if not m:
            raise ValueError(f"Unsupported build.yaml line: {raw!r}")
        key, value = m.group(1), m.group(2).strip()
        if value == ">":
            data[key] = ""
            mode = "folded"
        elif value == "":
            data[key] = []
            mode = "list"
        else:
            data[key] = value.strip('"')
    return data


def md5(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()  # noqa: S324 - Jellyfin's manifest checksum is MD5 by format


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def validate_zip(path: Path, expected_version: str | None = None, expected_guid: str | None = None) -> list[str]:
    """Return a list of problems with a plugin ZIP (empty when valid)."""
    problems: list[str] = []
    try:
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            if PLUGIN_DLL not in names:
                problems.append(f"{PLUGIN_DLL} missing")
            for name in names:
                base = name.rsplit("/", 1)[-1]
                if "/" in name:
                    problems.append(f"nested entry not allowed: {name}")
                if base.startswith(FORBIDDEN_PREFIXES) and base != PLUGIN_DLL:
                    problems.append(f"host assembly must not be packaged: {name}")
                if name not in ALLOWED_ZIP_ENTRIES:
                    problems.append(f"unexpected entry: {name}")
            if "meta.json" in names:
                meta = json.loads(z.read("meta.json"))
                if expected_guid and meta.get("guid") != expected_guid:
                    problems.append("meta.json guid mismatch")
                if expected_version and meta.get("version") != expected_version:
                    problems.append("meta.json version mismatch")
                if meta.get("assemblies") != [PLUGIN_DLL]:
                    problems.append("meta.json assemblies must list only the plugin assembly")
            else:
                problems.append("meta.json missing")
    except zipfile.BadZipFile:
        problems.append("not a ZIP archive")
    return problems


def validate_manifest(manifest: list, guid: str | None = None, allow_http: bool = False) -> list[str]:
    """Semantic invariants for a Jellyfin plugin repository manifest."""
    problems: list[str] = []
    if not isinstance(manifest, list) or not manifest:
        return ["manifest must be a non-empty JSON array"]
    for plugin in manifest:
        for field in ("guid", "name", "description", "overview", "owner", "category", "versions"):
            if not plugin.get(field):
                problems.append(f"plugin field '{field}' missing or empty")
        if not GUID_RE.match(str(plugin.get("guid", ""))):
            problems.append("guid is not a lowercase GUID")
        if guid and plugin.get("guid") != guid:
            problems.append("guid does not match build.yaml")
        versions = plugin.get("versions") or []
        seen = set()
        parsed = []
        for v in versions:
            ver = v.get("version", "")
            if not VERSION_RE.match(ver):
                problems.append(f"version '{ver}' is not four-part numeric")
                continue
            if ver in seen:
                problems.append(f"duplicate version {ver}")
            seen.add(ver)
            parsed.append(tuple(int(p) for p in ver.split(".")))
            if not VERSION_RE.match(v.get("targetAbi", "")):
                problems.append(f"{ver}: targetAbi must be four-part numeric")
            url = v.get("sourceUrl", "")
            if not (url.startswith("https://") or (allow_http and url.startswith("http://"))) or not url.endswith(".zip"):
                problems.append(f"{ver}: sourceUrl must be an https URL to a .zip")
            if not re.fullmatch(r"[0-9a-f]{32}", v.get("checksum", "")):
                problems.append(f"{ver}: checksum must be a 32-character lowercase MD5")
            if not v.get("timestamp"):
                problems.append(f"{ver}: timestamp missing")
        if parsed != sorted(parsed, reverse=True):
            problems.append("versions must be ordered newest first")
    return problems
