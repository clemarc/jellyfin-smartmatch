# ADR 0006: Library scope defaults and what auto-apply covers

**Status:** accepted · **Date:** 2026-10-28

## Context

The PRD asks for an explicit library allowlist **and** a report-only audit of existing media that runs by default on first install. An empty allowlist would make that first audit a no-op.

## Decision

- Scope: `IncludeAllLibraries = true` by default (every Movies, Shows and Mixed library). The admin can switch to an explicit list. Scope only controls *reporting*; it never authorises writes.
- Auto-apply has its own explicit, per-library list (`AutoApplyLibraryIds`, empty by default) and requires `Mode = AutoApplyHighConfidence`. Both are re-read immediately before every automatic commit, so switching back to Report only stops queued automatic work.
- When enabled, auto-apply acts on findings produced by scan events, reconciliations and admin-started audits in the opted-in libraries. Findings computed earlier in report-only mode are not applied retroactively; "Apply all eligible" exists for that.
- Libraries with NFO savers or "save artwork into media folders" are blocked for automation until the admin ticks *allow auto-apply anyway* for that library.
