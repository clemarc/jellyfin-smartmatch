# Changelog

## 1.0.0.0 (unreleased)

Initial release for Jellyfin 12 (target ABI 12.0.0.0, .NET 10).

- Scan monitoring: item and refresh events are queued without blocking the scanner, debounced, and reconciled after each library scan; a daily scheduled reconciliation catches anything missed.
- Existing-library audit on first start (report-only, resumable), manual audits and coverage reporting.
- Name normalization: release-site watermarks (UIndex.org and other known sites), release tags, episode markers, years and separators, with a trace of every step and admin-defined anchored prefix rules.
- Candidate search through each library's enabled metadata providers, one request at a time with timeouts, retries and back-off.
- Persistent findings report with filters, details, alternatives and CSV/JSON export.
- Apply one, Apply selected and Apply all eligible with preview, confirmation, per-item revalidation and outcomes.
- Opt-in, per-library automatic application of high-confidence unique matches.
- Conditional rollback, change history, and crash-safe state.
