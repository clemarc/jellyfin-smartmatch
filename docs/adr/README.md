# Architecture decision records

| ADR | Decision |
|---|---|
| [0001](0001-pinned-jellyfin-abi.md) | Build against Jellyfin 12.0.0 (`targetAbi 12.0.0.0`), test 12.0 and 12.4, fail closed on other majors |
| [0002](0002-direct-provider-search.md) | Call each library-enabled remote search provider directly so provider failures are visible |
| [0003](0003-identity-write-and-refresh.md) | Write provider ids (+ unedited title), fill-missing refresh by default, opt-in replace of unlocked text |
| [0004](0004-json-journal.md) | Bounded, crash-safe JSON journal under Jellyfin's plugin configuration directory |
| [0005](0005-scan-monitoring.md) | Event capture + debounce + post-scan reconciliation + daily fallback task |
| [0006](0006-library-scope-and-auto-apply.md) | All libraries reported by default; auto-apply is a separate per-library opt-in |
| [0007](0007-admin-ui-module.md) | Dashboard page as HTML view + ES module controller |
| [0008](0008-distribution.md) | Reproducible ZIP with meta.json; GitHub Releases + `catalog` branch manifest |
