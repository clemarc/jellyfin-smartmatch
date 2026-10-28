# ADR 0009: Cross-provider merge, offline re-rank and a one-day search cache

**Status:** accepted · **Date:** 2026-10-28

## Context

On a live 12.4 server, TheMovieDb returned *Chicago Fire (2012)* with only a Tmdb id and OMDb returned it with only an Imdb id. Results were merged only on a shared id, so the same show became two equal candidates that blocked each other as near-equivalent rivals. Separately, a library that keeps one release folder per episode makes Jellyfin create one series per folder, so the same cleaned title was searched once per folder.

## Decision

- **Merge by title and year.** After the id-based merge, results combine into one candidate when the comparison key and year are identical, no id key carries two values (writable or not), and no provider returned two results with that title and year. Year tolerance does not apply.
- **Keep the evidence.** Each saved candidate stores every id the providers reported (`ReportedProviderIds`), not only the ids Smart Match may write, so a later re-rank sees what the search saw.
- **Re-rank offline after updates.** `MatchCandidateRanker.Revision` is stored on each finding. At startup, Proposed and Needs review findings from an older revision are classified again from their saved candidates, without a provider search or an automatic apply. Findings saved before reported ids existed are re-ranked without the title-and-year merge. When they hold same-titled candidates, their saved query fingerprint is cleared and they are queued for one fresh, unforced analysis (which can share a cached search) rather than promoted on incomplete evidence.
- **Cache searches for a day.** Complete searches (every provider answered) are kept in memory for 24 hours, keyed by the query fingerprint (title, year, rules, providers, language, country, media kind), at most 1,000 entries. Forced requests (Retry analysis, a forced audit) bypass the cache. A restart empties it.

## Consequences

Bump `Revision` whenever ranking could classify the same results differently. Provider-side corrections can take up to a day to appear for a title searched recently; Retry analysis gets them immediately.
