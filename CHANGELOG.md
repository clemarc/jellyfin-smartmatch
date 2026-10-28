# Changelog

## 1.0.1.0 (unreleased)

- The same series or film returned by two providers with different ids (for example a TMDb id from TheMovieDb and an IMDb id from OMDb) is now one candidate carrying both ids, instead of two candidates that block each other as "near-equivalent rivals". Results merge only when the title and year are identical, no id disagrees, and no single provider returned two works with that title and year.
- After an update that changes ranking, open findings (Proposed and Needs review) are re-ranked at startup from their saved candidates. No provider is searched again, nothing is applied automatically, and applied, ignored and rejected findings are left alone. Candidates now keep every id the providers reported, so a re-rank sees the same evidence as the search. Findings saved by 1.0.0.0 lack those ids and are never promoted by the new merge; those with same-titled candidates get one fresh analysis instead.
- Items that clean to the same title, year and provider settings share one provider search for a day (for example one release folder per episode of a series). Searches with a provider failure are not reused, Retry analysis and forced audits always search again, and the cache is cleared on restart.

## 1.0.0.0

Initial release for Jellyfin 12 (target ABI 12.0.0.0, .NET 10).

- Scan monitoring: item and refresh events are queued without blocking the scanner, debounced, and reconciled after each library scan; a daily scheduled reconciliation catches anything missed.
- Existing-library audit on first start (report-only, resumable), manual audits and coverage reporting.
- Name normalization: release-site watermarks (UIndex.org and other known sites), release tags, episode markers, years and separators, with a trace of every step and admin-defined anchored prefix rules.
- Candidate search through each library's enabled metadata providers, one request at a time with timeouts, retries and back-off.
- Persistent findings report with filters, details, alternatives and CSV/JSON export.
- Apply one, Apply selected and Apply all eligible with preview, confirmation, per-item revalidation and outcomes.
- Opt-in, per-library automatic application of high-confidence unique matches.
- Conditional rollback, change history, and crash-safe state.
