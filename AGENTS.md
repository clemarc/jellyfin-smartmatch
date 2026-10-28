# Working on Smart Match

Guidance for humans and coding agents. `PRD.md` and `TECHNICAL_DESIGN.md` are the requirements; `docs/adr/` records where the implementation chose between options.

## Layout

```text
src/Jellyfin.Plugin.SmartMatch/
  Plugin.cs, PluginServiceRegistrator.cs   entry point, DI
  Normalization/   pure name cleaning (no Jellyfin, no IO)
  Matching/        provider-id validation, ranking/policy, throttled provider search
  Discovery/       eligibility, name evidence, library scope
  Services/        ItemAnalyzer, MatchApplicationService (single writer), BulkApplyCoordinator, AuditService
  Jobs/            SmartMatchWorker (hosted service), ScanEventObserver
  Library/         the only code that touches Jellyfin APIs (gateways, events, post-scan task, scheduled tasks)
  Storage/         crash-safe JSON journal
  Api/             admin-only controller and DTOs
  Configuration/   settings, validation, configPage.html + configPage.js (dashboard UI)
tests/SmartMatch.UnitTests/      xUnit: normalizer fixtures + fuzz, ranking, journal, pipeline (fakes), labeled corpus
tests/SmartMatch.TestProvider/   test-only offline metadata provider plugin for the disposable server
tests/integration/               disposable Jellyfin acceptance run (Docker)
scripts/                         package.py, update-manifest.py, verify-release.py
```

## Commands

```bash
dotnet restore --locked-mode          # lock files are committed
dotnet build -c Release               # warnings are errors
dotnet test -c Release                # 247 unit tests, ~10 s
dotnet format --verify-no-changes --severity warn

python3 scripts/package.py            # dist/smartmatch_<version>-jf12.zip (+ SHA256SUMS.txt, .md5)

# Disposable Jellyfin acceptance run (needs Docker; never points at a real server)
dotnet build tests/SmartMatch.TestProvider -c Release
python3 tests/integration/run_integration.py \
  --image jellyfin/jellyfin:12.0.20260908-012347 \
  --zip dist/smartmatch_1.0.0.0-jf12.zip \
  --provider-dll tests/SmartMatch.TestProvider/bin/Release/net10.0/SmartMatch.TestProvider.dll \
  --catalog --update-zip dist-update/smartmatch_1.0.0.1-jf12.zip   # optional extras
# ARM64 under QEMU: add --platform linux/arm64 --smoke --start-timeout 1500 and use the -arm64 image tag
```

Fuzz the normalizer harder locally: `SMARTMATCH_FUZZ_ITERATIONS=200000 SMARTMATCH_FUZZ_SEED=7 dotnet test --filter Random_inputs`.

## Rules that must not regress

- Never rename, move, delete or write media files; never write Jellyfin's database directly. All metadata writes go through `MatchApplicationService` → `ILibraryGateway.WriteIdentityAsync` / `RefreshAsync`.
- Report-only is the default. Bulk and automatic paths only apply `Proposed` findings with their recommended candidate. Ambiguous findings need an explicit admin choice.
- Re-read and revalidate the item immediately before every write; check auto-apply settings immediately before every automatic commit.
- Event handlers only enqueue. No network calls, writes or waits in Jellyfin callbacks.
- Every API route requires `Policies.RequiresElevation`; never accept a filesystem path from a client; render untrusted strings with `textContent`.
- Only the cleaned title and year go to providers; no paths in normal-level logs.
- The plugin GUID `4b7fecfd-bc55-4216-b032-2947ec6c22c9` never changes.

## Adding normalization rules

Add a fixture to `NormalizerTests.Fixtures` first, then change `MediaNameNormalizer` or `BuiltInPrefixRules`. Built-in prefix rules must be anchored, specific (no generic `\w+\.\w+`), and carry an example that `Built_in_rule_examples_hold` verifies. Run the corpus and fuzz tests.

## Releasing (owner only)

1. Bump `<Version>` in `Directory.Build.props` and `version` in `build.yaml` (four-part, e.g. `1.0.1.0`); update `CHANGELOG.md`.
2. Merge to `main`, then create tag `v1.0.1.0` on `main`, either `git tag v1.0.1.0 && git push origin v1.0.1.0`, or on github.com (works from a phone browser): Releases → Draft a new release → type `v1.0.1.0` as a new tag on `main` → Publish. `release.yml` validates the tag, builds, tests, runs the disposable-server gate, creates the GitHub Release (or attaches the ZIP to the one made in the web UI, keeping any notes written there), updates `manifest.json` on the `catalog` branch and verifies the public catalogue. Until the workflow finishes, a release made in the web UI has no ZIP; that is expected.
3. If catalogue publication fails, re-run the workflow with `workflow_dispatch` and the same tag. It never replaces published bytes.

Changing the Jellyfin baseline: update `JellyfinPackageVersion`, `targetAbi`, the guards in `release.yml`, image pins in the workflows and `docs/compatibility.md` together.
