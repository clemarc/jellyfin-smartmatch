# ADR 0002: Call each enabled remote search provider directly

**Status:** accepted · **Date:** 2026-10-28

## Context

The design proposed `IProviderManager.GetRemoteSearchResults<TItem, TLookup>`. Reading the 12.0 source (`ProviderManager.GetRemoteSearchResults`) shows it catches and logs every provider exception and returns an empty list. The plugin could not tell "no match" from "TMDb is down / rate-limited", which the PRD requires (statuses `NoMatch` vs `ProviderError`, retries with back-off, AT-08, AT-25).

## Decision

`JellyfinProviderSearchGateway` asks Jellyfin for the item's **library-enabled** providers with `IProviderManager.GetMetadataProviders<Series|Movie>(item, libraryOptions)` (the same call Jellyfin's own refresh uses; disabled providers are excluded), keeps those implementing `IRemoteSearchProvider<SeriesInfo|MovieInfo>`, and calls `GetSearchResults` on each through `ProviderSearchExecutor`:

- one request at a time across the server, minimum 500 ms apart (configurable);
- a timeout enforced by the plugin, not trusted to the provider;
- up to three attempts with exponential back-off and jitter for 429, 5xx, timeouts and network errors; 4xx is not retried;
- the lookup carries only the cleaned title, trusted year, library language and country. `Path` is never set.

Results from several providers are de-duplicated by any shared provider id, the same way Jellyfin merges them.

## Consequences

Provider order, language and enablement still come from Jellyfin's library settings. If a future Jellyfin version changes `GetMetadataProviders`, the integration tests (stub provider enabled, real providers disabled) catch it.
