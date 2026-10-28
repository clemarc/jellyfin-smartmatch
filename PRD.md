# Smart Match for Jellyfin — Product Requirements Document

**Status:** Draft for product review  
**Version:** 0.2  
**Date:** 28 October 2026  
**Target:** Jellyfin 12.x  
**Proposed repository:** `jellyfin-plugin-smartmatch` (working name; owner TBD)  
**Companion document:** [TECHNICAL_DESIGN.md](./TECHNICAL_DESIGN.md)

## 1. Executive summary

Smart Match is a native Jellyfin plugin that identifies films and TV series when their filesystem names contain release-site watermarks, torrent/release tags, or other noise that prevents Jellyfin's normal metadata providers from matching them. It derives a clean search title **without renaming or moving any file**, asks Jellyfin's already-enabled metadata providers for candidates, and **continuously builds a reviewable findings report while Jellyfin scans new media**, as well as auditing media indexed before installation. Administrators can apply an individual finding, any selected findings, or all eligible findings from a report; they may also opt into conservative automatic application. All these workflows are **v1.0 requirements**, not subsequent enhancements.

The initial target user runs Jellyfin alongside Plex on a Raspberry Pi 400. Both services read the same existing media library, which includes H.264/H.265 in MP4/MKV containers. Some source paths may also be referenced by qBittorrent. Changing filenames or paths is unacceptable. The Jellyfin server is accessible on the LAN and over Tailscale; the plugin does not require public ingress, a cloud account, a separate service, or transcoding.

**Example:** A TV-series folder named `www.UIndex.org - The Fall and Rise of Reggie Dinkins` should be recognized as **The Fall and Rise of Reggie Dinkins**. The plugin must remove the leading site watermark and separator for search, find the canonical series through a currently enabled metadata provider, assign its verified provider ID with an administrator-approved or high-confidence action, and request a safe metadata refresh. The original directory name must remain unchanged.

### Product promise

- Better identification despite noisy library names.
- A continuously updated findings report for both new and previously scanned media, with selective and bulk apply actions.
- Safe defaults: scanning and reporting run automatically; applying metadata requires a deliberate **Apply selected**, **Apply all eligible**, or opt-in **Auto-apply high confidence** action. Never replace correct identifications.
- A standard Jellyfin admin/plugin experience, installable through a third-party plugin repository hosted on GitHub.

## 2. Problem and opportunity

Jellyfin often derives a series title from its directory name and a film title from its containing folder or filename. A release-site prefix such as `www.UIndex.org -` can become part of the search name and cause failed identification. Plex may successfully match some of these files, but this is not guaranteed by either product.

Existing `adielsa/jellyfin-filename-title` is useful prior art but insufficient: it cleans the **displayed title** after the fact; its bulk scheduled task excludes folders (`IsFolder = false`); it does not search remote metadata, assign provider IDs, or manage reviewable confidence. Its main branch targets old Jellyfin dependencies and its 12.1 PR contains a concerning inversion of its raw-name safety condition. Smart Match should be a new implementation with explicit matching and safety, not a silent title-cleaner fork.

### Primary pain points

1. New TV series show as raw filenames or folders without posters, descriptions, or canonical episode metadata.
2. Fixing each item via Jellyfin's **Identify** screen does not scale for an established collection.
3. Renaming is risky because Plex and torrent clients may depend on existing paths.
4. Repeated rescans can reintroduce noise or repeat failed searches.
5. A plugin that guesses incorrectly is worse than an item left unmatched.

## 3. Goals and success criteria

| ID | Goal | Measurable criterion |
|---|---|---|
| G1 | Correct common release-site-prefix failures | In a curated fixture set of at least 40 problematic names, 95% of the expected clean query titles are produced without losing meaningful title text. |
| G2 | Match real, unmatched titles safely | At least 90% of unambiguous fixtures with accessible provider results appear as the correct top candidate; **zero** wrong automatic applications in the acceptance corpus. |
| G3 | Protect existing media and metadata | No rename, move, delete, media-file edit, or torrent-path change; no plugin-written NFO or sidecar; no application against a previously identified item by default. |
| G4 | Provide administrative control | Admin sees a persistent findings report; can apply one, selected, or all eligible findings; can enable or disable high-confidence auto-apply; sees a change history. |
| G8 | Handle new and existing content | Initial/repeated full-library audit and near-real-time scan-event processing discover media without delaying Jellyfin scanning. |
| G9 | Deliver autonomously through Codex | Codex completes API proof, implementation, testing, packaging and release-ready artefacts in one assignment, without asking for approval between milestones. |
| G5 | Work on low-power hardware | Runs on ARM64 Jellyfin without video probing/transcoding, bounded memory, serialized searches, and throttled provider requests. |
| G6 | Native distribution and updates | Versioned ZIP released from GitHub; Jellyfin catalogue installation via `manifest.json`; fresh install and update tested. |
| G7 | Recover from interruption | Restart/cancellation leaves the library consistent; completed decisions are idempotent and runs can resume safely. |

The accuracy targets are acceptance criteria over **labeled fixtures with successful metadata-provider connectivity**; they are not promises about arbitrary libraries or internet provider availability.

## 4. Users and roles

**Primary administrator:** Manages a home Jellyfin server; wants fixes through Jellyfin's dashboard and no additional container. Needs full transparency and low-effort review.

**Playback users:** Family members using Android TV, iPad, iPhone, or a laptop. They should only notice improved titles, posters, and episode information. They must never see the administrative queue or plugin settings.

**Maintainer/contributor:** Builds in C#, runs local tests, packages through CI, publishes releases and handles Jellyfin ABI changes.

Only Jellyfin administrators may inspect full filesystem paths, approve matches, configure automatic execution, or access plugin APIs.

## 5. Scope

### P0 — v1.0 required scope (complete workflow)

1. Native Jellyfin 12.x plugin (no external service) with a dashboard page and administrator-only APIs.
2. **Continuous discovery alongside Jellyfin library scanning:** subscribe to supported item/metadata events and enqueue work *outside* the scanner critical path; coalesce events and process after the specific item's metadata activity has settled. Use a post-scan reconciliation hook when available so no events are lost. Never slow down or interrupt library scans.
3. **Existing-library audit:** on first installation automatically run a nonblocking, report-only audit of all previously scanned films and series after server startup; make this configurable, resumable and safe on Pi 400. Support subsequent manual full audit and scheduled reconciliation. Report the status and completion of this coverage.
4. Normalize site watermarks and release tags without changing filesystem paths. Match candidates through Jellyfin's configured metadata providers, respecting metadata locks and correctly identified titles.
5. Persistent **findings report** with item, normalized title, candidate(s), score/explanation, match confidence, proposed action, last checked, eligibility, applied result and errors; filter, search, sort, inspect alternatives and export.
6. **Apply individual**, **Apply selected** (explicit checkboxes) and **Apply all eligible** (current filtered scope or all eligible, explicitly stated). Show an actionable preflight summary with exclusions/conflicts and require confirmation; apply item-by-item with partial-success reporting and safe retry. Never imply that ambiguous findings can be bulk-applied automatically.
7. **Configurable automatic apply** available in v1.0, **off by default**. When enabled, high-confidence unique matches for selected libraries are automatically applied during ongoing scan/reconciliation processing; ambiguous, weak or conflicting matches remain in the report. Admin may disable auto-apply immediately.
8. Refresh metadata after a verified application and record the postcondition, origin (`manual`, `bulk`, `automatic`) and audit trail; preserve paths and existing good matches.
9. Scheduled tasks, admin API, UI, persistent queues and reports, bounded retries, startup recovery and protection against reentrant Jellyfin refresh events.
10. Full automated tests, ARM64-compatible build, GitHub Actions, versioned release ZIP, generated Jellyfin repository manifest, installation/update documentation and release verification.

### P1 — refinements after the complete v1.0 workflow

- Advanced title aliases and locale-specific heuristics beyond the safe v1.0 normalizer.
- Historical analytics/graphs and optional notification summaries.
- Additional media types, multi-edition handling, complex anime/absolute episode numbering and performance tuning based on real-world data.

### P2 — future exploration (not a requirement for 1.0)

- Early/pre-refresh integration to supply normalized lookup names *before* Jellyfin's default matching, **only after** verifying provider ordering and metadata refresh semantics for target Jellyfin ABI.
- Better parsing of scene release names, aliases, multi-language titles, anime naming conventions, multi-version films, special episodes, and local NFO/provider hints.
- Lightweight metrics/dashboard of identification improvements over time.
- Compatibility builds for other Jellyfin versions when there is demand.

### Explicitly out of scope

- Renaming, moving, deleting, hard-linking or modifying media files.
- Changing Plex, qBittorrent, Tailscale, NAS, or Docker configuration.
- Video/audio analysis, subtitle processing, transcoding, intro/credits detection.
- Replacing TMDb/TVDb/OMDb, introducing proprietary match servers, or scraping unsupported sources.
- Guessing the identity of episodes independently of their parent series in v1.0.
- Automatically overriding a correct existing provider ID or manually locked metadata.
- Installing arbitrary plugins or packages from untrusted URLs.
- A separate Python/TypeScript container or external web application.

## 6. Core user journeys

### Journey A — new content during a library scan

1. Jellyfin detects a new film or series during its normal library scan.
2. Smart Match observes the item change/refresh, coalesces any duplicate notifications and enqueues background analysis. It must not block the Jellyfin scanner or begin a conflicting refresh while Jellyfin owns that item.
3. When the item settles, Smart Match evaluates whether identification is missing or suspicious, normalizes its name, searches the enabled metadata providers and writes a result to the persistent findings report.
4. In report-only mode, no media metadata changes. In auto-apply mode, only candidates meeting the stringent policy are applied; all other cases remain pending with an explanation.
5. A final post-scan reconciliation catches missed events and does not re-query already processed, unchanged items.

### Journey B — audit existing media (first install or on demand)

1. Admin opens **Dashboard → Plugins → Smart Match** and sees scan monitoring status, coverage of already-scanned libraries, current apply mode, last run and report totals.
2. An initial background audit examines existing `Series` and `Movie` records, including items indexed before installation; it is resumable, rate-limited and does not re-encode or read video contents.
3. Admin can also run **Audit existing library** manually or schedule reconciliation. Results merge into the same report, not a separate queue.
4. Successfully identified media is left unchanged, and repeated audits do not generate duplicate findings.

### Journey C — apply one, some or all

1. The report shows original and cleaned title, candidates, confidence reasons, statuses and proposed operation.
2. Admin applies one item, selects multiple rows then **Apply selected**, or chooses **Apply all eligible** with the scope explicitly displayed.
3. A preflight preview lists totals to apply, ambiguous/ignored/locked/ineligible exclusions, stale decisions and likely metadata changes; **Confirm** is required for bulk actions.
4. Each item is revalidated immediately before mutation. Successful matches are marked applied; failures and conflicts are reported individually and do not abort unrelated items.
5. The report remains available to review outcomes, retry failures or export results. `Apply all` never silently selects an ambiguous candidate.

### Journey D — automatic application (opt-in in v1.0)

1. Admin enables **Auto-apply high-confidence matches** for selected libraries. This is an explicit persisted opt-in, not the installation default.
2. New scan events and reconciliation jobs use the same matching and validation pipeline as the review UI.
3. Only matches with unique, sufficiently supported identity, no conflicting IDs/locks, and a verified safe refresh policy apply unattended.
4. Other suggestions remain in **Needs review**; turning off auto-apply prevents further queued automatic commits after in-flight item operations complete.

### Journey E — ambiguity and recovery

`The Office` without a year may return multiple credible shows. The plugin reports alternatives and never automatically applies a guess. If an applied item is incorrect, the admin can inspect its decision record and conditionally roll back plugin-owned fields only if unchanged since application, or re-identify via Jellyfin. Plex/torrent paths remain untouched.

## 7. Functional requirements

### FR-01 — item discovery

- Process items by Jellyfin record type (`Series`, `Movie`) and library membership, not merely by extension.
- Include noisy series directories and film folders/files; skip unrelated folder nodes, seasons, episodes, music and collections.
- Default to only items **without a reliable external provider ID** and/or with demonstrably unrecognized metadata; an apparent raw title is evidence, not proof.
- Do **not** treat missing artwork alone as failed matching; a valid provider ID can coexist with unavailable artwork.
- Skip manually locked items, items explicitly ignored, inaccessible records, and records currently being refreshed.
- Avoid duplicate suggestions for the same item and unchanged normalization signature.
- Process change notifications for new/modified records and a bounded reconciliation scan to catch missed items. Scan events must never synchronously perform remote search or metadata writes.
- Cover records indexed before plugin installation, including repeated audits; display last-audited timestamp and coverage count.

### FR-02 — normalization

- Derive candidates from series-folder basename, movie containing-folder basename (where appropriate), and media filename; retain provenance and confidence of each source.
- Strip recognized website prefixes, including `www.UIndex.org -`, plus configurable prefix patterns anchored at the beginning.
- Separate dots/underscores used as release separators **without** destroying decimal titles, punctuation and significant apostrophes.
- Remove codec/resolution/release-group tokens only in recognizable suffix/context; never globally remove ordinary words such as `Web`, `Complete`, `Theatrical`, `It` or `Up`.
- Extract `S01E01` / `1x02` episode identifiers for context, **not** as part of the series search title.
- Keep recognized production years as separate evidence; do not silently discard them.
- Preserve accented characters; normalize Unicode for comparison but retain display text.
- Present each cleaning operation to the admin, e.g. `removed site-prefix: www.UIndex.org`, `trimmed separator: -`.
- Output must be stable and idempotent: normalizing an already normalized title must not change it further.

### FR-03 — metadata lookup

- Reuse Jellyfin's enabled remote metadata providers, library language and country preferences.
- Search clean title as a `SeriesInfo` or `MovieInfo` lookup, passing year only when trustworthy.
- Deduplicate matching provider IDs; record source provider and query for each candidate.
- Do not send full file paths to external providers when a title-only query is sufficient.
- Honor provider disablement and handle no providers, timeouts, rate limits and 5xx failures.
- Return candidates with accessible provider ID, name, year and explanation; if a provider returns only low-quality information, do not auto-apply.

### FR-04 — scoring and policy

- Normalization quality, title similarity, known year agreement, provider consistency and candidate uniqueness inform ranking.
- A high score is a recommendation, not authorization to overwrite an existing ID.
- Three modes exist in v1.0: **Report only** (default), **Manual/bulk application** on explicit user command, and **Auto-apply high confidence** (opt-in, per-library).
- Automatic approval has stricter requirements than manual ranking. Report-only must never mutate Jellyfin item metadata.
- Never auto-select candidates with a conflicting year beyond configured tolerance, ambiguous franchise/remake titles, missing provider ID, mismatched content type or insufficient margin.
- Do not auto-match an item merely because it is candidate #1.

### FR-05 — applying a match

- Re-read Jellyfin item and check current identity, library scope and metadata locks before writing.
- Capture a bounded before-state of metadata fields the plugin changes and what it expects to see afterward.
- Write only the selected external provider identity and trigger Jellyfin's normal metadata refresh; do not replace descriptions, artwork or episode metadata directly.
- Avoid force-replace, replace-all-image, and destructive refresh flags by default.
- If Jellyfin is configured to save metadata alongside media, explicitly warn the admin that **Jellyfin itself** may write NFO/sidecar files during refresh. The plugin must not promise complete filesystem immutability unless that server configuration is checked and tested. Prevent unattended auto-apply when such write behavior conflicts with read-only guarantees.
- Record partial success and allow retry if ID assignment succeeds but refresh fails.
- Prevent concurrent or repeated applications of the same item.

### FR-06 — review and administration

- Dashboard table columns: Original title, Proposed title, Type, Library, Candidate, Score/Reason, Status, Last checked.
- Row detail must expose the path to admin only, alternatives and normalization trace.
- Filter by type, library, status, confidence; search by title.
- Commands: Audit existing library, Apply one, Apply selected, Apply all eligible, Reject, Ignore, Retry, Cancel run, Enable/disable auto mode.
- Checkbox selection (including select all on current filter) must be distinct from **Apply all eligible**; show the exact execution scope and count before committing.
- Every bulk action requires a preflight preview and confirmation, skips ambiguous/locked/conflicted items, and returns per-item outcomes, not one opaque success/failure result.
- Configuration validation on the server, not only in browser JavaScript.
- All HTTP routes require an authenticated Jellyfin administrator; reject anonymous and regular playback accounts.

### FR-07 — scheduled operations

- Process new/changed media alongside Jellyfin scans through nonblocking event capture and a bounded worker; use item-level refresh completion or safe debounce and a post-scan reconciliation to avoid racing the scanner.
- On first deployment, index **existing** series and movies as a resumable background audit, default report-only. Also expose manual and scheduled full audits.
- Use a persistent work queue keyed by item ID and fingerprint; do not process one item repeatedly due to `ItemUpdated`/refresh loops.
- Auto-apply runs only when deliberately enabled and policy allows it; disabling it stops new automatic commitments.
- Cancellation safely stops new work and persists completed results; restart rechecks queued work safely rather than blindly applying stale suggestions.

### FR-08 — observability and retention

- Show run IDs, start/end, item totals, matching and apply outcomes, runtime, provider errors and skipped reasons.
- Use structured logs; redact paths or use item IDs in normal-level logs; detailed paths only at controlled debug level.
- Retain last 30 days or 5,000 per-item decisions by default (configurable within reasonable bounds); avoid unbounded data.
- Store only what is needed for review/rollback, not media contents or API credentials.

### FR-09 — packaging and installation

- Publish an AnyCPU, ARM64-compatible managed plugin build for the supported Jellyfin ABI.
- Build/version on every release through GitHub Actions, attach installable ZIP to GitHub Release, publish matching catalogue manifest and checksums.
- Clearly document catalogue installation, restart, update, manual install and downgrade limitations.
- Public `manifest.json` accessible without GitHub auth; GitHub Pages is **optional**, not required.

## 8. Detailed matching policies

### Item identification states

| State | Meaning | Default behaviour |
|---|---|---|
| Already identified | Existing trustworthy provider ID | Skip; do not modify |
| Needs investigation | Unmatched or raw/noisy title | Produce a candidate query |
| Candidate found | One or more provider matches | Review queue |
| Ambiguous | Multiple credible identities | Manual review mandatory |
| No match | Providers returned none | Track + retry on change/manual request |
| Provider unavailable | Request failed | Retry with backoff, never infer a match |
| Ignored | Admin excluded the item | Skip until cleared |
| Applied | A selected match was saved | Refresh and verify |
| Conflict | Item changed before commit | Abort apply, re-audit |

### Conservative auto-match policy (v1.0 opt-in)

All of the following are mandatory: opt-in enabled for that library; item still unmatched; candidate has a nonempty supported provider ID; normalized title equivalent under strict case/punctuation normalization; credible year agreement when a year is available (and year required by default for automation); unique result after ID deduplication; no near-equivalent rival; no conflicting local `.nfo`/metadata lock; no unsupported provider condition. The exact numeric score threshold, margin and uniqueness rules are defined and regression-tested in the technical design. A result failing any condition goes to review.

### Important edge cases

- `www.UIndex.org - Show Name` → `Show Name` (no leading `-`).
- `The.Office.US.S01E01.1080p.WEB-DL.mkv` → search series alias carefully; **do not** assume US edition without sufficient evidence.
- `Web (2013)` → keep `Web`.
- `Complete Unknown (2024)` → keep `Complete Unknown`.
- `Dune (1984)` versus `Dune (2021)` → production year prevents accidental cross-identification.
- `Doctor Who (1963)` versus `Doctor Who (2005)` → never auto-apply without year/context.
- `Dark.S01E01.mkv` → `Dark` (the series) and episode context; do not search as a movie.
- `Movie.Title.2024.1080p.HEVC.mkv` → film title `Movie Title`, year `2024`, codec removed only as suffix context.
- `Mr.Robot.S01E01.mkv`, `Wall-E`, `Spider-Man` → retain semantic punctuation/hyphenation for display.
- Media filename with an existing provider identifier marker `[tmdbid-…]` → preserve identifier and defer to Jellyfin's native recognition.
- Unicode titles, multiple languages, remakes, sequels, alternative cuts and unparseable names → send to review rather than guess.

## 9. UX specification

### Dashboard overview

- Header: **Smart Match**, installed version, compatibility status and mode (`Report only` by default).
- Status indicators for **Monitoring new scans**, **Existing-library audit coverage**, **Background work queue**, **Last reconciliation**, and **Auto-apply enabled/disabled**.
- Summary tiles: `Not checked`, `Proposed`, `Need review`, `Applied`, `Ignored`, `Errors`.
- Primary action: **View findings**. Secondary: **Audit existing library**, **Apply selected**, **Apply all eligible**, **Settings**.
- Compact description: `Matches films and series with messy filenames. Never renames or moves files.`
- Safety indication when a library uses NFO/sidecar saving or source-path writes.

### Review list

- Default sort: `Needs review` first, then most recent.
- Each row has selection checkbox, normalized title, original title, strongest candidate, score/explanation, origin (new scan/existing audit), and status. Click to reveal path and details.
- Toolbar distinguishes **Apply selected (N)** from **Apply all eligible (N)**, with explicit filter/scope confirmation. Display exclusions and results.
- Never auto-apply merely because an admin clicked a candidate for inspection.
- Empty states distinguish `No unmatched media` from `Providers unavailable` and `No libraries selected`.

### Settings

- Enabled media libraries (explicit allowlist).
- Mode: `Report only` (default) / `Auto-apply high-confidence` (explicit opt-in); individual/selected/all-eligible manual actions available in either mode.
- New-scan monitoring enabled by default; initial existing-library audit enabled by default in report-only mode, nonblocking and resumable.
- Scope controls for `Apply all eligible`: filtered results vs entire eligible report, always explicit.
- Preferred providers: default `use each library's enabled providers`.
- Prefix rules: built-in recognized sites plus user-editable anchored rules with preview input/output.
- Rate limit defaults: 1 active provider search, configurable minimum delay 500 ms; conservative retries.
- Logs and retention setting; export from admin UI only.
- An **Explain safe operation** panel outlining no path changes and Jellyfin sidecar caveat.

## 10. Non-functional requirements

**Safety:** No mutation during preview; never rename/move/delete; all mutating admin actions audit-logged; provider IDs and metadata locks respected; rollback never overwrites independent user edits.

**Performance:** Bounded/constrained concurrency (default one remote search at a time), pagination of items, no video probing, no full file reads. The Pi 400 remains usable for streaming while an audit runs. Suggested benchmark: 1,000 library records, no more than 100 MB steady plugin-owned memory growth during audit (measure, don't assume); CPU idle between requests.

**Reliability:** Graceful cancellations; retryable provider errors; finite timeouts, exponential backoff + jitter, no infinite loops; single-run lock; avoid reentrant item update event storms.

**Security:** Admin-only routes and UI; no arbitrary path reading from user input; no remote code execution; no raw filename leakage to third-party search when a clean query suffices; no hardcoded secrets; protect cross-site write endpoints using Jellyfin's established authorization/origin conventions; do not expose the dashboard outside Jellyfin authentication.

**Compatibility:** Jellyfin 12.x, .NET 10, Linux ARM64 and x64, native Docker installs; test against exact released Jellyfin ABI in CI. No 10.11 promise in v1. Plugin must fail closed on unknown ABI.

**Privacy:** Only title/optional year are passed to configured metadata providers through Jellyfin. Audit data remains local. No analytics or telemetry to the plugin author.

**Accessibility:** Keyboard operable, labeled status text, usable on mobile/tablet Jellyfin admin UI, sufficient contrast and explicit confirmations.

## 11. Acceptance tests: end-to-end

| ID | Test | Expected |
|---|---|---|
| AT-01 | Install from custom repository on supported Jellyfin 12 | ZIP installs, restart activates plugin, configuration page appears |
| AT-02 | Scan `www.UIndex.org - The Fall and Rise of Reggie Dinkins` | Query cleans to title without URL/hyphen; candidate displays correct series if provider available |
| AT-03 | Run audit without applying | Files, Jellyfin provider IDs and Jellyfin metadata remain unchanged |
| AT-04 | Approve candidate | Selected ID persisted; metadata refreshed; original media path unchanged |
| AT-05 | Correctly identified item in library | Skipped, unchanged |
| AT-06 | Ambiguous `The Office` | Manual review required; no automatic apply |
| AT-07 | Existing conflicting provider ID appears between preview and approve | Abort safely with conflict status |
| AT-08 | Provider timeout/rate-limit | Run reports retriable error without modifying item |
| AT-09 | Cancel halfway through scan | Stops safely; prior audit results remain; no runaway tasks |
| AT-10 | Restart after write | Applied outcome and audit log persist; next run does not reapply |
| AT-11 | Non-admin calls plugin API | 401/403, no path leakage or mutation |
| AT-12 | NFO-writing library configuration | Warning shown; unattended automation blocked until explicitly reconciled |
| AT-13 | Movie release filename with tags and year | Preserves real title and year; ignores codec suffix |
| AT-14 | Library with Plex/qBittorrent referencing same paths | Neither media paths nor Plex/qBittorrent data touched |
| AT-15 | Update plugin from published repository | Version updates successfully; settings and decisions preserved |
| AT-16 | Fresh ARM64 Docker Jellyfin smoke test | Plugin starts, audits, and refreshes one synthetic fixture correctly |
| AT-17 | New prefixed series during ordinary Jellyfin library scan | Item is enqueued automatically, report entry appears without manual audit and scanner is not blocked |
| AT-18 | Pre-existing incorrectly named items before plugin install | First background/reconciliation audit discovers them, reports persist across restart |
| AT-19 | Apply selected with two valid and one ambiguous item | Only selected valid items applied; ambiguous excluded with reason; per-item outcomes visible |
| AT-20 | Apply all eligible across current filter | Preflight shows exact scope, count and exclusions; confirmation required; only eligible records change |
| AT-21 | Auto-apply off by default | Scan and background audit populate report without mutating Jellyfin metadata |
| AT-22 | Enable auto-apply for one library | High-confidence eligible item applies and refreshes; other libraries and ambiguous titles remain report-only |
| AT-23 | Disable auto-apply while work queued | No queued item begins a new auto-apply commit after disable; current operation finishes consistently |
| AT-24 | Repeated `ItemUpdated` and refresh events | One effective lookup and decision per unchanged item; no refresh loop |
| AT-25 | Provider unavailable during Jellyfin scan | Scanning continues; plugin reports/retries error independently |
| AT-26 | Bulk action interrupted after partial completion | Applied items remain applied, pending items retry safely, report records exact outcomes |
| AT-27 | Autonomous Codex build from PRD + design | Completes M0–M4 without interactive milestone sign-offs; source, tests, workflows, manifest generator and release-ready ZIP produced, with evidence and blockers documented |

## 12. Delivery plan and definition of done

| Milestone | Deliverables | Exit gate |
|---|---|---|
| M0 — API spike | Pin Jellyfin release, compile minimal plugin, prove provider search, event/post-scan observation and metadata application on disposable data | Integration evidence for the exact 12.x API and lifecycle |
| M1 — Scan monitoring + existing audit | Normalizer, event-driven background queue, post-scan reconciliation, startup/full audit, report persistence and unit/integration tests | A newly scanned and a pre-existing problematic title both appear in report without blocking scanning |
| M2 — Apply controls | One/selected/all eligible, preflight, partial outcomes, conflicts, post-refresh verification, admin UI and auth | Batch and single-apply end-to-end acceptance tests pass |
| M3 — Opt-in automatic mode | Per-library settings, strict gates, idempotency, retry, disable semantics and lifecycle safety | Auto on/off, ambiguity and race tests pass; zero wrong automatic matches in labeled corpus |
| M4 — Release-ready distribution | GitHub Actions CI/release, installable ZIP, manifest generator, docs, security/ARM64 smoke and update tests | Plugin can be installed from generated catalogue and all v1.0 acceptance tests pass |

**Definition of done for v1.0:** M0–M4 all pass. The plugin observes new Jellyfin scan results without blocking the scanner, audits already indexed media, produces persistent findings, supports applying one/some/all eligible findings, and offers a tested opt-in high-confidence auto-apply setting. Distribution artefacts, docs, tests and reproducible build commands are present. No mandatory seven-day manual approval/soak phase. The Codex build must **not** stop after M0 for a plan approval.

## 13. Decisions and implementation autonomy

1. **Name:** `Smart Match` unless an actual collision requires a new name.
2. **Apply behavior:** Report-only by default. Selective, bulk and per-library opt-in auto-apply all ship in v1.0.
3. **Monitoring:** Process new scan events via a nonblocking queue; reconcile after scans; audit already-indexed media on startup/first install and on demand.
4. **Provider preference:** Use Jellyfin library-enabled metadata providers; no mandatory custom TMDb key.
5. **Rollback:** Conditional restoration of plugin-owned fields where the currently observed state still equals the plugin-written state.
6. **Distribution:** GitHub release ZIP and raw-hosted manifest; GitHub Pages not required.
7. **Codex autonomy:** Read both documents; investigate exact Jellyfin APIs, implement M0–M4, test and fix issues, update docs, and deliver a complete release-ready repository in one assignment. Make routine engineering decisions independently and document them as ADRs. Do not stop for milestone approvals or ask the user to babysit the build. Stop only at genuine external blockers such as absent credentials/permissions, inaccessible target environment, or an action requiring owner authorization (e.g., publishing to the user's GitHub account or deploying to production). For a blocker, implement everything else, explain the blocker and supply an exact follow-up command or action.

## 14. Sources and reference implementations

- [Jellyfin plugin installation and repositories](https://jellyfin.org/docs/general/server/plugins/index.html)
- [Jellyfin plugin template](https://github.com/jellyfin/jellyfin-plugin-template) — useful scaffolding, but verify target framework and dependencies for Jellyfin 12.
- [Jellyfin 12.0 release notes](https://jellyfin.org/posts/jellyfin-release-12.0/) — plugin framework is .NET 10 and APIs changed.
- [Jellyfin metadata providers](https://jellyfin.org/docs/general/server/metadata/)
- [Jellyfin provider identifiers](https://jellyfin.org/docs/general/server/metadata/identifiers/)
- [Original Filename Title plugin](https://github.com/adielsa/jellyfin-filename-title) and [12.1 compatibility PR](https://github.com/adielsa/jellyfin-filename-title/pull/2) — prior art only; do not copy blindly.
- [Jellyfin plugin repositories release mechanics](https://jellyfin.org/posts/plugin-updates/)

---

**Codex contract:** Implement the **whole v1.0 plugin** in one autonomous pass, following M0–M4 without asking for milestone approval. Report findings and generate working source, tests, installable ZIP, manifest and release workflows. Automatic mass metadata application must remain disabled by default in the **plugin runtime**, but the full opt-in feature and selected/all-eligible report actions must be implemented and tested.
