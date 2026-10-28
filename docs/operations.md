# Operating Smart Match

## Overview page

**Dashboard → Plugins → Smart Match** shows:

- version, server ABI compatibility and the current mode (*Report only* by default);
- **Monitoring new scans**, **Existing-library audit coverage** (checked / total films and series in scope), **Background work queue** (due now / waiting to retry), **Last reconciliation**, **Auto-apply** state and any running audit;
- tiles: *Not checked*, *Proposed*, *Need review*, *Applied*, *Ignored*, *Errors*. Click a tile to filter the report;
- warnings, for example a library whose metadata savers write NFO files next to media.

Buttons: **View findings**, **Audit existing library** (re-checks every item; unchanged items are not searched again), **Reconcile now** (queues only new or changed items), **Cancel run**, **Settings**.

## Statuses

| Status | Meaning | What you can do |
|---|---|---|
| Not checked | Queued for analysis | Wait |
| Proposed | One clear candidate with a supported provider id | Apply one, Apply selected, Apply all eligible |
| Needs review | Ambiguous, weak, year conflict, or no supported id | Open it, pick a specific candidate, or Reject/Ignore |
| No match | Providers answered with nothing usable | Retry after renaming or adding a prefix rule |
| Provider error | Every provider request failed | Retried automatically with back-off (15 min, 30 min, … up to 5 times); Retry manually |
| Applied | Ids written, Jellyfin refreshed, ids verified | Roll back |
| Partially applied | Ids written but the refresh or verification failed | Retry refresh (repeats only the refresh) or Roll back |
| Conflict | The item changed between analysis and apply; nothing was written | Retry analysis |
| Ignored / Rejected | Your durable decision | Retry clears it |
| Rolled back | Plugin-owned fields restored | Retry analysis |
| Resolved | The item got an identity outside Smart Match | Nothing |

## Applying matches

- **Apply one**: open a row, then *Apply recommended* or *Apply this candidate*. Ambiguous findings require choosing a specific candidate. A confirmation shows exactly which ids will be written.
- **Apply selected (N)**: tick rows (or the header checkbox for the current page), then click the button. A preview lists what will be applied and every exclusion with its reason (ambiguous, locked, already identified, changed since analysis). Nothing happens until you confirm.
- **Apply all eligible**: choose *current filter* or *entire report*, click the button, review the preview (scope, count, exclusions, sidecar warnings), confirm. Only *Proposed* findings are ever included.
- Bulk runs apply one item at a time and re-check each item right before writing it. Progress and per-item outcomes appear above the table; **Cancel remaining** stops future writes and keeps completed ones. Previews expire after 15 minutes.

Every application records who did it (manual, bulk or automatic), the ids and title before and after, and the refresh outcome. See **History**.

## Rollback

*Roll back* restores the provider ids and title Smart Match wrote, but only if nobody changed them since; otherwise it refuses and tells you to resolve it in Jellyfin. Descriptions and artwork Jellyfin downloaded during the refresh stay until a Jellyfin refresh replaces them. A rolled-back item is not proposed again automatically.

## Automatic application (opt-in)

Settings → Apply mode → **Auto-apply high-confidence matches**, then tick **Auto-apply** for each library. When enabled, findings from scans, reconciliations and audits in those libraries are applied without asking only if **all** of these hold:

- exact title match after case/punctuation/accents folding;
- a year in the name that equals the candidate's year (configurable, on by default);
- score ≥ 95/100 and at least 10 points ahead of any other candidate;
- no near-equivalent rival (same title within a year), not a known multi-production title without a year;
- the cleaned name produced no warnings, every enabled provider answered, the candidate has a supported id;
- the item is unchanged since analysis, not locked, has no local NFO, and the library does not save sidecar files (or you ticked *allow auto-apply anyway*).

Switching back to **Report only** (or unticking the library) takes effect before the next automatic commit, including work already queued.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Libraries | all Movies/Shows/Mixed | Scope for reporting only |
| Monitor new scans | on | |
| Initial audit after install | on | Report-only |
| Wait for running library scan | on | Keeps provider traffic out of Jellyfin's scan |
| Settle delay | 30 s | After the last event for an item |
| Minimum pause between searches | 500 ms | One search at a time |
| Provider timeout / attempts | 30 s / 3 | Transient failures only |
| Maximum searches per audit | 2,000 | Remaining items wait an hour |
| Refresh profile | Fill missing | *Replace unlocked text* also replaces episode titles; images are never replaced |
| Replace unedited title | on | Only when the title is still the filename-derived one |
| Prefix rules | built-in | Custom rules need a passing example before they can be enabled |
| Retention | 30 days / 5,000 decisions | Records needed for rollback are kept |

## Scheduled tasks

*Dashboard → Scheduled tasks → Smart Match*:

- **Smart Match: reconcile library**: daily by default. Queues unchecked items, items saved since the last run, and provider errors due for retry.
- **Smart Match: audit existing library**: no default trigger; add one if you want periodic full audits. Its progress bar tracks the audit.

## Export

**Export CSV** / **Export JSON** download the report (respecting the status filter). Exports contain no media paths; CSV cells are protected against spreadsheet formula injection.

## Logs and privacy

Normal logs identify items by Jellyfin id, never by path. Only the cleaned title, year, library language and country are sent to metadata providers, through Jellyfin. Nothing is sent to the plugin author.

Items that clean to the same title and year, with the same providers and language, share one search for a day; the log line for the second and later items says the results were reused. **Retry analysis** and a forced audit always search again.

## Data

`/config/plugins/configurations/SmartMatch/` holds `findings.json`, `decisions.json`, `coverage.json`, `queue.json`, `runs.json`, `state.json` and their `.bak` copies. Back them up with the rest of `/config`.
