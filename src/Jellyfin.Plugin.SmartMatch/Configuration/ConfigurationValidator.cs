using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.SmartMatch.Matching;
using Jellyfin.Plugin.SmartMatch.Normalization;

namespace Jellyfin.Plugin.SmartMatch.Configuration;

/// <summary>
/// Server-side configuration validation. The browser UI validates too, but this is authoritative.
/// </summary>
public static class ConfigurationValidator
{
    /// <summary>
    /// Validates a configuration.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>Errors; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();

        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                errors.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max}."));
            }
        }

        if (!Enum.IsDefined(config.Mode))
        {
            errors.Add("Mode is invalid.");
        }

        if (!Enum.IsDefined(config.RefreshProfile))
        {
            errors.Add("RefreshProfile is invalid.");
        }

        Range(nameof(config.StartupDelaySeconds), config.StartupDelaySeconds, 0, 3600);
        Range(nameof(config.SettleDelaySeconds), config.SettleDelaySeconds, 5, 3600);
        Range(nameof(config.MinSearchIntervalMs), config.MinSearchIntervalMs, 250, 60000);
        Range(nameof(config.ProviderTimeoutSeconds), config.ProviderTimeoutSeconds, 5, 300);
        Range(nameof(config.ProviderMaxAttempts), config.ProviderMaxAttempts, 1, 3);
        Range(nameof(config.MaxSearchesPerRun), config.MaxSearchesPerRun, 10, 100000);
        Range(nameof(config.RefreshTimeoutSeconds), config.RefreshTimeoutSeconds, 30, 7200);
        Range(nameof(config.YearTolerance), config.YearTolerance, 0, 2);
        Range(nameof(config.AutoMinScore), config.AutoMinScore, 90, 100);
        Range(nameof(config.AutoMargin), config.AutoMargin, 5, 50);
        Range(nameof(config.RetentionDays), config.RetentionDays, 1, 365);
        Range(nameof(config.RetentionMaxDecisions), config.RetentionMaxDecisions, 100, 50000);

        foreach (var (name, ids) in new[]
                 {
                     (nameof(config.IncludedLibraryIds), config.IncludedLibraryIds),
                     (nameof(config.AutoApplyLibraryIds), config.AutoApplyLibraryIds),
                     (nameof(config.SidecarAcknowledgedLibraryIds), config.SidecarAcknowledgedLibraryIds),
                 })
        {
            if (ids is null)
            {
                continue;
            }

            if (ids.Length > 500)
            {
                errors.Add($"{name} has too many entries.");
            }

            foreach (var id in ids)
            {
                if (!Guid.TryParse(id, out _))
                {
                    errors.Add($"{name} contains an invalid library id.");
                    break;
                }
            }
        }

        if (config.ExtraProviderIdKeys is { } keys)
        {
            if (keys.Length > 20)
            {
                errors.Add("ExtraProviderIdKeys has too many entries.");
            }

            foreach (var key in keys)
            {
                if (!ProviderIdValidator.IsValidKeyName(key))
                {
                    errors.Add($"ExtraProviderIdKeys contains an invalid key name '{Truncate(key)}'.");
                }
            }
        }

        var rules = config.PrefixRules ?? Array.Empty<UserPrefixRule>();
        if (rules.Length > 50)
        {
            errors.Add("At most 50 prefix rules are allowed.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            var label = string.IsNullOrWhiteSpace(rule.Id) ? "(unnamed rule)" : Truncate(rule.Id);
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || !rule.Id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                errors.Add($"Prefix rule {label}: id must be 1-64 letters, digits, '-', '_' or '.'.");
            }
            else if (!seen.Add(rule.Id))
            {
                errors.Add($"Prefix rule {label}: duplicate id.");
            }

            if (!RuleSet.TryCompile(rule.Pattern, out _, out var error))
            {
                errors.Add($"Prefix rule {label}: {error}");
                continue;
            }

            if (rule.Enabled)
            {
                var preview = PreviewRule(rule.Pattern, rule.ExampleInput);
                if (string.IsNullOrWhiteSpace(rule.ExampleInput))
                {
                    errors.Add($"Prefix rule {label}: an example input is required before the rule can be enabled.");
                }
                else if (preview is null)
                {
                    errors.Add($"Prefix rule {label}: the pattern does not match its example input.");
                }
                else if (!string.Equals(preview, rule.ExampleOutput?.Trim(), StringComparison.Ordinal))
                {
                    errors.Add($"Prefix rule {label}: the example produces '{Truncate(preview)}', not the expected output.");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Repairs a configuration loaded from disk: out-of-range numbers revert to defaults and
    /// invalid ids/rules are dropped, so a hand-edited XML file can never disable safety limits.
    /// </summary>
    /// <param name="config">Configuration to repair in place.</param>
    /// <returns>True when anything was changed.</returns>
    public static bool Sanitize(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var defaults = new PluginConfiguration();
        var changed = false;

        int Fix(int value, int min, int max, int fallback)
        {
            if (value < min || value > max)
            {
                changed = true;
                return fallback;
            }

            return value;
        }

        if (!Enum.IsDefined(config.Mode))
        {
            config.Mode = ApplyMode.ReportOnly;
            changed = true;
        }

        if (!Enum.IsDefined(config.RefreshProfile))
        {
            config.RefreshProfile = RefreshProfile.FillMissing;
            changed = true;
        }

        config.StartupDelaySeconds = Fix(config.StartupDelaySeconds, 0, 3600, defaults.StartupDelaySeconds);
        config.SettleDelaySeconds = Fix(config.SettleDelaySeconds, 5, 3600, defaults.SettleDelaySeconds);
        config.MinSearchIntervalMs = Fix(config.MinSearchIntervalMs, 250, 60000, defaults.MinSearchIntervalMs);
        config.ProviderTimeoutSeconds = Fix(config.ProviderTimeoutSeconds, 5, 300, defaults.ProviderTimeoutSeconds);
        config.ProviderMaxAttempts = Fix(config.ProviderMaxAttempts, 1, 3, defaults.ProviderMaxAttempts);
        config.MaxSearchesPerRun = Fix(config.MaxSearchesPerRun, 10, 100000, defaults.MaxSearchesPerRun);
        config.RefreshTimeoutSeconds = Fix(config.RefreshTimeoutSeconds, 30, 7200, defaults.RefreshTimeoutSeconds);
        config.YearTolerance = Fix(config.YearTolerance, 0, 2, defaults.YearTolerance);
        config.AutoMinScore = Fix(config.AutoMinScore, 90, 100, defaults.AutoMinScore);
        config.AutoMargin = Fix(config.AutoMargin, 5, 50, defaults.AutoMargin);
        config.RetentionDays = Fix(config.RetentionDays, 1, 365, defaults.RetentionDays);
        config.RetentionMaxDecisions = Fix(config.RetentionMaxDecisions, 100, 50000, defaults.RetentionMaxDecisions);

        string[] Ids(string[]? ids)
        {
            var valid = (ids ?? Array.Empty<string>()).Where(i => Guid.TryParse(i, out _)).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
            if (ids is null || valid.Length != ids.Length)
            {
                changed = true;
            }

            return valid;
        }

        config.IncludedLibraryIds = Ids(config.IncludedLibraryIds);
        config.AutoApplyLibraryIds = Ids(config.AutoApplyLibraryIds);
        config.SidecarAcknowledgedLibraryIds = Ids(config.SidecarAcknowledgedLibraryIds);

        var keys = (config.ExtraProviderIdKeys ?? Array.Empty<string>()).Where(ProviderIdValidator.IsValidKeyName).Take(20).ToArray();
        if (config.ExtraProviderIdKeys is null || keys.Length != config.ExtraProviderIdKeys.Length)
        {
            config.ExtraProviderIdKeys = keys;
            changed = true;
        }

        var rules = (config.PrefixRules ?? Array.Empty<UserPrefixRule>()).Where(r => r is not null && RuleSet.TryCompile(r.Pattern, out _, out _)).Take(50).ToArray();
        if (config.PrefixRules is null || rules.Length != config.PrefixRules.Length)
        {
            config.PrefixRules = rules;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies a single user pattern to a sample, the way the normalizer would.
    /// </summary>
    /// <param name="pattern">Pattern.</param>
    /// <param name="sample">Sample input.</param>
    /// <param name="builtIn">True for built-in rules (no user length limit).</param>
    /// <returns>The remainder, or null when the pattern does not match.</returns>
    public static string? PreviewRule(string pattern, string? sample, bool builtIn = false)
    {
        if (string.IsNullOrEmpty(sample) || !RuleSet.TryCompile(pattern, out var regex, out _, enforceLengthLimit: !builtIn))
        {
            return null;
        }

        try
        {
            var m = regex!.Match(sample);
            if (!m.Success || m.Length == 0)
            {
                return null;
            }

            return sample[m.Length..].Trim().TrimStart('-', '_', '.', ':', '|', '~').Trim();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the active rule set from configuration (enabled rules only).
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>Rule set.</returns>
    public static RuleSet BuildRuleSet(PluginConfiguration config)
    {
        var rules = (config.PrefixRules ?? Array.Empty<UserPrefixRule>())
            .Where(r => r.Enabled && PreviewRule(r.Pattern, r.ExampleInput) is { } output && output == r.ExampleOutput?.Trim())
            .Select(r => new PrefixRule("user." + r.Id, r.Description, r.Pattern, r.ExampleInput, r.ExampleOutput, false));
        return RuleSet.Create(rules);
    }

    /// <summary>
    /// Builds match thresholds from configuration.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <returns>Settings.</returns>
    public static MatchSettings BuildMatchSettings(PluginConfiguration config) => new()
    {
        YearTolerance = config.YearTolerance,
        AutoMinScore = config.AutoMinScore,
        AutoMargin = config.AutoMargin,
        AutoRequireYear = config.AutoRequireYear,
    };

    private static string Truncate(string? s) => s is null ? string.Empty : (s.Length > 40 ? s[..40] + "…" : s);
}
