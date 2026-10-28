#!/usr/bin/env bash
# Create or complete the GitHub Release for a tag, never replacing published bytes.
#
# Usage: scripts/publish-github-release.sh TAG VERSION ZIP SHA256SUMS MD5FILE
# Needs: gh (authenticated via GH_TOKEN), GITHUB_REPOSITORY.
#
# Three cases:
#   1. No release for the tag (tag pushed from git): create it with the ZIP and checksums.
#   2. Release exists without the ZIP (created in the GitHub web UI, which makes the tag
#      and an empty release): upload the ZIP and checksums; add notes only if it has none.
#   3. Release already has the ZIP (re-run): verify the published bytes are identical.
set -euo pipefail

tag="$1"
version="$2"
zip="$3"
sums="$4"
md5file="$5"
zip_name="$(basename "$zip")"
sums_name="$(basename "$sums")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

write_notes() {
    cat > "$work/notes.md" <<EOF
Smart Match $version for Jellyfin 12 (target ABI 12.0.0.0, .NET 10, AnyCPU: Linux x64 and ARM64).

Install: Dashboard → Plugins → Repositories → add
\`https://raw.githubusercontent.com/${GITHUB_REPOSITORY}/catalog/manifest.json\`, then install **Smart Match** from the catalogue and restart Jellyfin.

SHA-256: \`$(cut -d' ' -f1 "$sums")\` · MD5 (manifest checksum): \`$(cat "$md5file")\`
EOF
}

if ! gh release view "$tag" --json tagName >/dev/null 2>&1; then
    echo "No release for $tag; creating it."
    write_notes
    gh release create "$tag" "$zip" "$sums" --title "Smart Match $version" --notes-file "$work/notes.md" --verify-tag
    exit 0
fi

assets="$(gh release view "$tag" --json assets --jq '.assets[].name')"

verify_asset() {
    local name="$1" local_path="$2"
    gh release download "$tag" --pattern "$name" --dir "$work/existing" --clobber
    if ! cmp -s "$work/existing/$name" "$local_path"; then
        echo "::error::Published $name differs from this build. Published versions are immutable; bump the version."
        exit 1
    fi
}

if grep -qxF "$zip_name" <<<"$assets"; then
    echo "Release $tag already has $zip_name; verifying published bytes instead of replacing them."
    verify_asset "$zip_name" "$zip"
    exit 0
fi

echo "Release $tag exists without $zip_name (created in the web UI); attaching this build."
upload=("$zip")
if grep -qxF "$sums_name" <<<"$assets"; then
    verify_asset "$sums_name" "$sums"
else
    upload+=("$sums")
fi
gh release upload "$tag" "${upload[@]}"

body="$(gh release view "$tag" --json body --jq '.body')"
if [[ -z "${body//[[:space:]]/}" ]]; then
    write_notes
    gh release edit "$tag" --notes-file "$work/notes.md"
else
    echo "Keeping the release notes written in the web UI."
fi
