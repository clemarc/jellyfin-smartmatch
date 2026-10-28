# Jellyfin Smart Match

**Better metadata matching for media with messy filenames, without renaming your files.**

> **Project status: specification / pre-implementation.** This repository currently contains product and technical design documents. The plugin, its installer, and its GitHub release catalogue are **not yet available**. Features and installation steps below describe the intended v1.0 release, not functionality that can already be installed.

## Why Smart Match?

Jellyfin can struggle to identify movies and TV series whose folder or file names contain release-site prefixes, quality markers, or other release information. For example:

```text
On disk:
www.UIndex.org - The Fall and Rise of Reggie Dinkins/

Expected metadata match:
The Fall and Rise of Reggie Dinkins
```

Smart Match is designed to generate a clean *search title*, ask Jellyfin's configured metadata providers for candidates, and present the findings in an administrator report. It does **not** rename, move, delete, or rewrite media files. That makes it suitable for libraries also used by Plex or referenced by torrent clients.

## Planned v1.0 features

- **Live scan integration:** Discover new or updated films and series alongside Jellyfin's normal library scans, without blocking the scanner.
- **Existing-library audit:** Check titles that were already indexed before installing the plugin; run an audit again on demand.
- **Safer name normalization:** Strip common release-site watermarks, extra separators, and technical release tags while preserving meaningful title text and year information.
- **Jellyfin provider search:** Use the metadata providers already enabled for the library rather than requiring a separate TMDb API key.
- **Persistent findings report:** See the original filename, normalized query, suggested identification, confidence, alternative candidates, and reason for any skipped item.
- **Flexible application:** Apply a single match, a selection, or **all eligible** matches from the admin interface, with preflight and per-item outcomes.
- **Optional automatic application:** Enable conservative auto-apply for high-confidence, unambiguous findings on selected libraries. **Off by default.**
- **Safe recovery:** Re-check current metadata before applying changes, protect existing correct matches, and resume safely after interruption.
- **Native Jellyfin experience:** An in-process C# plugin with an administrator page and scheduled tasks. No separate service or public inbound port is needed.

Ambiguous suggestions remain in the report for review. Even a bulk operation must skip candidates that do not meet the eligibility and safety rules.

## Intended workflow

```text
Jellyfin scans new media ──────┐
                               ├──> Smart Match analysis and findings report
Audit of existing media ───────┘                         │
                                                        ├── Apply one / selected
                                                        ├── Apply all eligible
                                                        └── Optional high-confidence auto-apply
                                                                 │
                                                    Verified metadata refresh
```

Smart Match only changes approved Jellyfin metadata fields. Physical media paths remain untouched. If Jellyfin itself is configured to save NFO files alongside media, that configuration must be assessed separately; a read-only media mount is recommended where practical.

## Compatibility target

| Component | Target |
| --- | --- |
| Jellyfin server | 12.0 baseline, with later 12.x versions verified explicitly |
| Plugin runtime | .NET 10, native Jellyfin C# plugin |
| Host architecture | Linux ARM64 (including Raspberry Pi 400) and Linux x64 |
| Media library | Movies and TV series |
| Playback | Independent of codecs, direct play, or transcoding settings |
| Network | Local Jellyfin access and existing Tailscale access; no new public endpoint |

Support for a given Jellyfin release is **not confirmed until integration tests pass against that exact release**.

## Project documents

- [PRD.md](./PRD.md) defines user journeys, feature requirements, safety rules, and acceptance criteria.
- [TECHNICAL_DESIGN.md](./TECHNICAL_DESIGN.md) defines the proposed plugin architecture, Jellyfin APIs, persistence, admin UI, CI/CD, and release packaging.

The two documents are the implementation contract. Where they leave a routine engineering choice open, the implementer should choose, document, and test it rather than interrupt work for milestone-by-milestone approval.

## Building the first version with Codex

1. Create a GitHub repository and commit `README.md`, `PRD.md`, and `TECHNICAL_DESIGN.md` to its root.
2. Open the repository in Codex and ask it to implement **all v1.0 requirements** from both documents in one autonomous build task.
3. Have Codex pin the Jellyfin 12 API, prove provider search and safe metadata application in an isolated Jellyfin test environment, then complete the rest of the plugin and test suite.
4. Request a pull request with the implementation, test results, installable release packaging, GitHub Actions workflows, and any documented limitations. Keep production deployment and the first public release as explicit owner-authorized steps.

**Suggested Codex task:**

> Read `PRD.md` and `TECHNICAL_DESIGN.md` in full. Implement Jellyfin Smart Match v1.0 end-to-end as a native C# plugin. Complete integration proof, scanning and existing-library audit, persistent findings, selective/bulk apply, opt-in automatic apply, administration UI, testing, documentation, and GitHub Actions packaging/release infrastructure. Work through all milestones autonomously; don't stop for routine decisions or intermediate approvals. Preserve media paths, default to report-only behaviour, and validate against a disposable Jellyfin 12 instance. Open a PR containing the completed implementation, validation evidence and any genuine unresolved blockers. Do not deploy to the live server or publish a release without owner authorization.

Source code, build commands, developer setup, and test instructions will be added once Codex has implemented and validated them. This README deliberately does not list speculative `dotnet` commands as working instructions.

## Distribution plan (after implementation)

The project plans to use **GitHub Actions + GitHub Releases + a Jellyfin plugin repository manifest**:

1. CI builds and tests the plugin, and packages a versioned **ZIP** suitable for Jellyfin.
2. A release workflow attaches the ZIP and integrity checksums to a tagged GitHub Release.
3. The workflow publishes or updates `manifest.json` on a dedicated `catalog` branch. The manifest points to the corresponding immutable release ZIP.
4. Users add the manifest's HTTPS URL under **Jellyfin Dashboard → Plugins → Repositories**, install Smart Match from the catalogue, and restart Jellyfin when required.

A future catalogue URL will have the form:

```text
https://raw.githubusercontent.com/<github-owner>/<repository>/catalog/manifest.json
```

**This is a template, not a working installation URL.** The actual URL will be published after the first tested release. GitHub Pages is **not required** to distribute the plugin, though it could host documentation later.

## Safety principles

- Never rename, move, delete, or overwrite media files.
- Never automatically replace a confirmed correct identity or override locked metadata.
- Report-only operation is the default; optional auto-apply is explicit and conservative.
- Always revalidate a candidate before writing provider IDs or refreshing metadata.
- Keep an audit trail of changes, skipped items, errors, and partial batch outcomes.
- Respect Jellyfin user permissions: matching controls and source paths are administrator-only.
- Avoid unrelated filesystem writes, video probing, transcoding, and unbounded network activity.

## Contributing and licence

The project is intended to be open source. An MIT licence is proposed for the new repository; add a real `LICENSE` file when creating it, and verify attribution obligations before incorporating third-party code. Prior work such as [Filename Title](https://github.com/adielsa/jellyfin-filename-title) can inform design but should not be copied without reviewing its licence and compatibility.

For feature scope or implementation details, start with [the PRD](./PRD.md) and [technical design](./TECHNICAL_DESIGN.md).
