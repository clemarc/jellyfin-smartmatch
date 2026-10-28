# ADR 0005: Event capture, debounce, post-scan reconciliation and a fallback task

**Status:** accepted · **Date:** 2026-10-28

## Decision

- `JellyfinItemEventSource` subscribes to `ILibraryManager.ItemAdded`, `ItemUpdated` and `IProviderManager.RefreshCompleted`, ignores everything but non-virtual `Series`/`Movie`, and hands a small `ItemEvent` to `ScanEventObserver`. The handler only coalesces the id into the persistent queue (dictionary insert under a lock) and returns; it never searches, writes or waits.
- Each event pushes the item's `NotBefore` to now + `SettleDelaySeconds` (30 s). The worker also defers items Jellyfin is refreshing (`GetRefreshProgress` / `GetRefreshQueue`) and, by default, waits while a full library scan runs.
- `SmartMatchPostScanTask : ILibraryPostScanTask` runs in Jellyfin's scan finalisation; it only enumerates ids (unchecked items and items saved since the last reconciliation) and enqueues them. Verified on 12.0: it runs as post-scan task 1/9 and completes in under a second.
- A daily `Smart Match: reconcile library` scheduled task repeats that reconciliation in case events or the post-scan hook were missed, and re-queues provider errors whose back-off expired.
- Dedupe: the coverage index stores an item fingerprint (path, name, provider ids, locks) and a query fingerprint (search title, year, rules revision, provider configuration). Unchanged items are never searched twice.
- Loop protection: `SelfWriteTracker` suppresses events for an item while the plugin writes it and for three minutes afterwards; after an application the item carries a provider id and is skipped as identified.

## Consequences

New media appears in the report shortly after the scan finishes rather than mid-scan. On a Raspberry Pi this keeps provider traffic away from the scan's own refreshes.
