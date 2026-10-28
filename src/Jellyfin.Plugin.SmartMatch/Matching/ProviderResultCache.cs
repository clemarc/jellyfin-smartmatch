using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartMatch.Matching;

/// <summary>
/// In-memory cache of complete provider searches, keyed by the query fingerprint (search title,
/// year, rules, providers, language, country and media kind). Many items often clean to the same
/// title, e.g. one release folder per episode of a series; within a day they share one search.
/// Searches with any provider failure are never cached, forced requests (Retry analysis, forced
/// audit) bypass it, and it is empty after a restart.
/// </summary>
public sealed class ProviderResultCache
{
    /// <summary>How long a search result is reused.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    /// <summary>Maximum cached searches; the oldest is evicted first.</summary>
    public const int Capacity = 1000;

    private readonly Dictionary<string, (DateTime StoredUtc, IReadOnlyList<ProviderCandidate> Results)> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly object _sync = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderResultCache"/> class.
    /// </summary>
    /// <param name="time">Clock.</param>
    public ProviderResultCache(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Returns the results of a search made less than <see cref="Lifetime"/> ago.
    /// </summary>
    /// <param name="key">Query fingerprint.</param>
    /// <param name="results">Cached results.</param>
    /// <returns>True when a fresh entry exists.</returns>
    public bool TryGet(string key, out IReadOnlyList<ProviderCandidate> results)
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (_entries.TryGetValue(key, out var entry) && now - entry.StoredUtc < Lifetime)
            {
                results = entry.Results;
                return true;
            }

            _entries.Remove(key);
            results = Array.Empty<ProviderCandidate>();
            return false;
        }
    }

    /// <summary>
    /// Stores the results of a search in which every provider answered.
    /// </summary>
    /// <param name="key">Query fingerprint.</param>
    /// <param name="outcome">Search outcome; ignored when any provider failed.</param>
    public void Store(string key, ProviderSearchOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Failures.Count > 0 || outcome.ProvidersQueried == 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_entries.ContainsKey(key) && _entries.Count >= Capacity)
            {
                _entries.Remove(_entries.MinBy(e => e.Value.StoredUtc).Key);
            }

            _entries[key] = (_time.GetUtcNow().UtcDateTime, outcome.Results.ToList());
        }
    }
}
