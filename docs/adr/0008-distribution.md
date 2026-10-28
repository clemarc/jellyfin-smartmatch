# ADR 0008: Release ZIP with meta.json, MD5 manifest on a catalog branch

**Status:** accepted · **Date:** 2026-10-28

## Decision

- `scripts/package.py` publishes the plugin and writes `smartmatch_<version>-jf12.zip` containing exactly `Jellyfin.Plugin.SmartMatch.dll` and `meta.json`. Entries are sorted and carry a fixed timestamp, so the ZIP is byte-reproducible for identical inputs.
- `meta.json` is included because Jellyfin 12's `PluginManager.LoadManifest` otherwise invents a manifest for manual installs with `Id = MD5(folder name)` and no `targetAbi` check. For catalogue installs `ReconcileManifest` merges it, with the package version as the source of truth and a GUID mismatch marking the plugin malfunctioned.
- The release workflow creates the GitHub Release (immutable assets; a re-run verifies bytes instead of replacing them), waits until the asset downloads identically, then updates `manifest.json` on the orphan `catalog` branch with `GITHUB_TOKEN`, and finally re-downloads the public raw manifest and ZIP to verify MD5 and layout. Any failure fails the workflow; `workflow_dispatch` with the same tag republishes the catalogue without a new version.
- The manifest `checksum` is the MD5 Jellyfin verifies on install; `SHA256SUMS.txt` is published for humans.
- GitHub Pages is not used.

## Consequences

The catalogue URL is `https://raw.githubusercontent.com/<owner>/<repo>/catalog/manifest.json`. It only works without authentication if the repository is public.
