using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Library;

namespace Jellyfin.Plugin.SmartMatch.Services;

/// <summary>
/// Supplies the current plugin configuration.
/// </summary>
public interface IConfigProvider
{
    /// <summary>Gets the current configuration.</summary>
    PluginConfiguration Current { get; }

    /// <summary>Validates and persists a new configuration.</summary>
    /// <param name="configuration">Configuration.</param>
    void Save(PluginConfiguration configuration);
}

/// <summary>
/// Reads configuration from the Jellyfin plugin instance.
/// </summary>
public sealed class PluginConfigProvider : IConfigProvider
{
    /// <inheritdoc />
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Save(PluginConfiguration configuration)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Smart Match plugin is not initialised.");
        plugin.UpdateConfiguration(configuration);
    }
}

/// <summary>
/// Per-item mutual exclusion shared by analysis and every apply path, so the scan worker,
/// bulk runs, automatic application and the API never operate on the same item concurrently.
/// </summary>
public sealed class ItemLockRegistry
{
    private readonly ConcurrentDictionary<Guid, string> _held = new();

    /// <summary>Tries to take the lock for an item.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="owner">Owner description.</param>
    /// <returns>A releaser, or null when the item is already held.</returns>
    public IDisposable? TryAcquire(Guid itemId, string owner)
        => _held.TryAdd(itemId, owner) ? new Releaser(this, itemId) : null;

    /// <summary>Returns true when the item is held.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when held.</returns>
    public bool IsHeld(Guid itemId) => _held.ContainsKey(itemId);

    private sealed class Releaser : IDisposable
    {
        private readonly ItemLockRegistry _owner;
        private readonly Guid _itemId;
        private bool _disposed;

        public Releaser(ItemLockRegistry owner, Guid itemId)
        {
            _owner = owner;
            _itemId = itemId;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _owner._held.TryRemove(_itemId, out _);
                _disposed = true;
            }
        }
    }
}

/// <summary>
/// Remembers items the plugin itself is writing so the scan observer ignores the resulting
/// ItemUpdated / RefreshCompleted events instead of re-queuing them (no refresh loops).
/// </summary>
public sealed class SelfWriteTracker
{
    /// <summary>How long after a write completes its echo events are ignored.</summary>
    public static readonly TimeSpan EchoWindow = TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<Guid, DateTime> _until = new();
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="SelfWriteTracker"/> class.
    /// </summary>
    /// <param name="time">Clock.</param>
    public SelfWriteTracker(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Marks an item as being written (suppressed until <see cref="Complete"/> + window).</summary>
    /// <param name="itemId">Item id.</param>
    public void Begin(Guid itemId) => _until[itemId] = DateTime.MaxValue;

    /// <summary>Starts the echo window for an item.</summary>
    /// <param name="itemId">Item id.</param>
    public void Complete(Guid itemId) => _until[itemId] = _time.GetUtcNow().UtcDateTime + EchoWindow;

    /// <summary>Returns true when events for the item should be ignored.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>True when suppressed.</returns>
    public bool IsSuppressed(Guid itemId)
    {
        if (!_until.TryGetValue(itemId, out var until))
        {
            return false;
        }

        if (until > _time.GetUtcNow().UtcDateTime)
        {
            return true;
        }

        _until.TryRemove(itemId, out _);
        return false;
    }
}

/// <summary>
/// Fingerprints used for dedupe and stale-state detection.
/// </summary>
public static class Fingerprints
{
    /// <summary>
    /// Fingerprint of the identity-relevant state of an item. Volatile fields (refresh stamps,
    /// artwork) are excluded so a self-generated refresh does not look like a user change.
    /// </summary>
    /// <param name="item">Snapshot.</param>
    /// <returns>Hex fingerprint.</returns>
    public static string Item(ItemSnapshot item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var ids = string.Join(';', item.ProviderIds.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key.ToLowerInvariant() + "=" + kv.Value));
        var raw = string.Join('|', item.Kind, item.Path ?? string.Empty, item.Name, item.LibraryId, ids, item.IsLocked, item.NameLocked);
        return Hash(raw, 16);
    }

    /// <summary>Fingerprint of a search (query + rules + provider configuration).</summary>
    /// <param name="title">Search title.</param>
    /// <param name="year">Trusted year.</param>
    /// <param name="rulesRevision">Rule set revision.</param>
    /// <param name="providerFingerprint">Provider configuration fingerprint.</param>
    /// <returns>Hex fingerprint.</returns>
    public static string Query(string title, int? year, string rulesRevision, string providerFingerprint)
        => Hash(string.Join('|', title, year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, rulesRevision, providerFingerprint), 16);

    /// <summary>Salted hash of a path; the journal never needs the path itself.</summary>
    /// <param name="salt">Installation salt.</param>
    /// <param name="path">Path.</param>
    /// <returns>Hex hash.</returns>
    public static string Path(string salt, string? path) => Hash(salt + "\n" + (path ?? string.Empty), 32);

    /// <summary>SHA-256 truncated hex.</summary>
    /// <param name="value">Value.</param>
    /// <param name="length">Hex length.</param>
    /// <returns>Hex.</returns>
    public static string Hash(string value, int length)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length].ToLowerInvariant();
}
