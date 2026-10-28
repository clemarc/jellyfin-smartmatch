# ADR 0004: Bounded JSON journal instead of a database

**Status:** accepted · **Date:** 2026-10-28

## Decision

State lives in `<Jellyfin config>/plugins/configurations/SmartMatch/` (survives plugin updates; never in a media folder) as six JSON files: `state`, `findings`, `decisions`, `coverage`, `queue`, `runs`. Each file carries a schema version. A single in-memory store guarded by one lock returns copies to readers; a 2-second flush loop writes dirty files, and every apply checkpoint flushes immediately.

Writes go to `*.tmp` with write-through and `Flush(true)`, the previous file is copied to `*.bak`, then the temp file is renamed over the original. On load, an unreadable file falls back to its backup; if both are unreadable the file is renamed to `*.corrupt-<timestamp>` and the plugin starts with an empty journal and a visible warning instead of failing Jellyfin's startup. A file with a newer schema (downgrade) is treated the same way, never silently parsed.

Retention: decision records older than 30 days or beyond 5,000 (both configurable) are dropped, except the record each applied finding needs for rollback. Findings are capped at 20,000 (oldest resolved/no-match first); runs at 50.

Paths are not stored: findings keep the basename that produced the search title and a salted SHA-256 of the full path. The live path is looked up from Jellyfin only for the admin detail view.

## Consequences

Fine for the 1,000–10,000-item libraries this targets. Should the journal outgrow that, migrate to plugin-local SQLite behind the same `SmartMatchStore` API.
