using System;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SmartMatch.Configuration;

/// <summary>
/// Apply mode.
/// </summary>
public enum ApplyMode
{
    /// <summary>Scan, audit and report. Never changes Jellyfin metadata on its own (default).</summary>
    ReportOnly,

    /// <summary>Additionally apply high-confidence unique matches in opted-in libraries.</summary>
    AutoApplyHighConfidence,
}

/// <summary>
/// How Jellyfin's refresh treats existing text metadata after an identity is applied.
/// </summary>
public enum RefreshProfile
{
    /// <summary>Fill missing metadata and images only (Jellyfin "Search for missing metadata"). Default.</summary>
    FillMissing,

    /// <summary>Replace unlocked text metadata, keep existing images (Jellyfin Identify without image replacement).</summary>
    ReplaceUnlockedText,
}

/// <summary>
/// A user-defined anchored prefix rule as stored in configuration.
/// </summary>
public sealed class UserPrefixRule
{
    /// <summary>Gets or sets the stable id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the regular expression (anchored at the start automatically).</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the rule is active. Requires a passing example.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets an example input the rule was previewed with.</summary>
    public string ExampleInput { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected remainder for <see cref="ExampleInput"/>.</summary>
    public string ExampleOutput { get; set; } = string.Empty;
}

/// <summary>
/// Plugin settings. Server-side validated by <see cref="ConfigurationValidator"/>; secrets are never stored here.
/// </summary>
[SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "XML serialization by Jellyfin.")]
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the apply mode. Report-only by default.</summary>
    public ApplyMode Mode { get; set; } = ApplyMode.ReportOnly;

    /// <summary>Gets or sets a value indicating whether every Movies/Shows/Mixed library is in scope.</summary>
    public bool IncludeAllLibraries { get; set; } = true;

    /// <summary>Gets or sets the explicit library allowlist used when <see cref="IncludeAllLibraries"/> is false.</summary>
    public string[] IncludedLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the libraries opted into automatic application. Empty by default.</summary>
    public string[] AutoApplyLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets libraries whose NFO/sidecar saving the admin has explicitly accepted for automation.</summary>
    public string[] SidecarAcknowledgedLibraryIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets a value indicating whether new scan events are processed.</summary>
    public bool MonitorNewScans { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the first-run audit of existing media runs.</summary>
    public bool InitialAuditEnabled { get; set; } = true;

    /// <summary>Gets or sets the delay after server start before background work begins, in seconds.</summary>
    public int StartupDelaySeconds { get; set; } = 120;

    /// <summary>Gets or sets how long an item must be quiet after its last event before analysis, in seconds.</summary>
    public int SettleDelaySeconds { get; set; } = 30;

    /// <summary>Gets or sets a value indicating whether remote searches wait while a library scan is running.</summary>
    public bool DeferWhileScanning { get; set; } = true;

    /// <summary>Gets or sets the minimum pause between remote searches, in milliseconds.</summary>
    public int MinSearchIntervalMs { get; set; } = 500;

    /// <summary>Gets or sets the timeout for one provider request, in seconds.</summary>
    public int ProviderTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the maximum attempts per provider request (transient failures only).</summary>
    public int ProviderMaxAttempts { get; set; } = 3;

    /// <summary>Gets or sets the maximum remote searches per audit run.</summary>
    public int MaxSearchesPerRun { get; set; } = 2000;

    /// <summary>Gets or sets the timeout for the post-apply metadata refresh, in seconds.</summary>
    public int RefreshTimeoutSeconds { get; set; } = 600;

    /// <summary>Gets or sets the refresh profile used after applying an identity.</summary>
    public RefreshProfile RefreshProfile { get; set; } = RefreshProfile.FillMissing;

    /// <summary>Gets or sets a value indicating whether the plugin sets the item title to the provider title when the current title is the unedited filename-derived one.</summary>
    public bool WriteCanonicalTitle { get; set; } = true;

    /// <summary>Gets or sets the accepted year difference for partial year credit.</summary>
    public int YearTolerance { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether automation requires a trusted year.</summary>
    public bool AutoRequireYear { get; set; } = true;

    /// <summary>Gets or sets the automatic score threshold.</summary>
    public int AutoMinScore { get; set; } = 95;

    /// <summary>Gets or sets the automatic margin over the runner-up.</summary>
    public int AutoMargin { get; set; } = 10;

    /// <summary>Gets or sets user prefix rules.</summary>
    public UserPrefixRule[] PrefixRules { get; set; } = Array.Empty<UserPrefixRule>();

    /// <summary>Gets or sets extra provider id keys that may be written (e.g. AniDB).</summary>
    public string[] ExtraProviderIdKeys { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets decision retention in days.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Gets or sets the maximum retained decision records.</summary>
    public int RetentionMaxDecisions { get; set; } = 5000;

    /// <summary>Gets or sets a value indicating whether full paths may be written to debug logs.</summary>
    public bool DebugLogPaths { get; set; }
}
