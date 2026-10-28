# ADR 0003: What an application writes, and how Jellyfin refreshes afterwards

**Status:** accepted · **Date:** 2026-10-28

## Context

Jellyfin's own Identify endpoint sets provider ids and then refreshes with `ReplaceAllMetadata = true`, `ReplaceAllImages = true`, `RemoveOldMetadata = true`. The design forbids replace-all and image replacement by default. Reading `MetadataService` in 12.0: a `FullRefresh` with `ReplaceAllMetadata = false` only fills **empty** fields. An unidentified series is named after its folder (`www.UIndex.org - The Fall and Rise of Reggie Dinkins`), so a fill-missing refresh would keep that noisy name forever.

## Decision

`MatchApplicationService` is the only writer. After revalidation it:

1. writes the candidate's validated provider ids (`Tmdb`, `Tvdb`, `Imdb`, `TvMaze`, plus admin-listed extra keys; unknown namespaces are dropped);
2. replaces the **title** with the provider's title only when the title is not locked and is still exactly the filename-derived name recorded at analysis (`WriteCanonicalTitle`, on by default). The before/after title is recorded and is rolled back with the ids;
3. persists via `ILibraryManager.UpdateItemAsync(..., ItemUpdateType.MetadataDownload)`. `MetadataDownload` is below `MetadataEdit`, so Jellyfin does not rewrite a pre-existing NFO for a "manual edit" when local saving is off;
4. calls `IProviderManager.RefreshFullItem` with `MetadataRefreshMode.FullRefresh`, `ImageRefreshMode.FullRefresh`, `ReplaceAllImages = false`, `RemoveOldMetadata = false`, `SearchResult = <selected candidate>` and `ReplaceAllMetadata` from the refresh profile:
   - **FillMissing** (default): only empty fields and missing images are filled.
   - **ReplaceUnlockedText** (opt-in): unlocked text is replaced; images are still never replaced; locked fields stay locked (Jellyfin honours `LockedFields` in both modes).
5. re-reads the item and requires the written ids to be present before marking `Applied`; otherwise `Partial`.

A checkpoint decision record is flushed before the write and again between the write and the refresh, so a crash leaves `Partial` (ids written, retry repeats only the refresh) or `Conflict` (nothing written).

## Consequences

- With the default profile, **episode titles** derived from filenames stay as they were; overviews, dates and missing artwork are filled. Admins who want canonical episode titles choose `ReplaceUnlockedText`. Documented in the README limitations.
- Descriptions and artwork fetched by Jellyfin's refresh are not plugin-owned; rollback restores ids and title only.
