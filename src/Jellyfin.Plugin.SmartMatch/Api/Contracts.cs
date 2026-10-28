using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Model;

namespace Jellyfin.Plugin.SmartMatch.Api;

/// <summary>Library row for the dashboard.</summary>
/// <param name="Id">Library id.</param>
/// <param name="Name">Name.</param>
/// <param name="CollectionType">Type.</param>
/// <param name="InScope">Selected for Smart Match.</param>
/// <param name="AutoApply">Opted into auto-apply.</param>
/// <param name="SidecarRisk">Jellyfin saves metadata next to media.</param>
/// <param name="SidecarDetail">Saver details.</param>
/// <param name="SidecarAcknowledged">Admin accepted sidecar writes for automation.</param>
public sealed record LibraryDto(Guid Id, string Name, string? CollectionType, bool InScope, bool AutoApply, bool SidecarRisk, string SidecarDetail, bool SidecarAcknowledged);

/// <summary>Status / overview.</summary>
public sealed class StatusDto
{
    /// <summary>Gets or sets the plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the server version.</summary>
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the server ABI is supported.</summary>
    public bool Compatible { get; set; }

    /// <summary>Gets or sets the targeted ABI.</summary>
    public string TargetAbi { get; set; } = "12.0.0.0";

    /// <summary>Gets or sets the mode.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether scan monitoring is active.</summary>
    public bool MonitoringActive { get; set; }

    /// <summary>Gets or sets the worker state.</summary>
    public string WorkerState { get; set; } = string.Empty;

    /// <summary>Gets or sets a fault.</summary>
    public string? Fault { get; set; }

    /// <summary>Gets or sets the queue length.</summary>
    public int QueueLength { get; set; }

    /// <summary>Gets or sets the number of queued items due now.</summary>
    public int QueueDue { get; set; }

    /// <summary>Gets or sets the number of queued items waiting for a later retry or settle window.</summary>
    public int QueueDeferred { get; set; }

    /// <summary>Gets or sets queue length by origin.</summary>
    public Dictionary<string, int> QueueByOrigin { get; set; } = new();

    /// <summary>Gets or sets counts by finding status.</summary>
    public Dictionary<string, int> Counts { get; set; } = new();

    /// <summary>Gets or sets the number of findings eligible for bulk apply.</summary>
    public int Eligible { get; set; }

    /// <summary>Gets or sets the libraries.</summary>
    public List<LibraryDto> Libraries { get; set; } = new();

    /// <summary>Gets or sets coverage.</summary>
    public CoverageDto Coverage { get; set; } = new();

    /// <summary>Gets or sets the last reconciliation.</summary>
    public DateTime? LastReconciliationUtc { get; set; }

    /// <summary>Gets or sets the running audit.</summary>
    public AuditRun? ActiveAudit { get; set; }

    /// <summary>Gets or sets the latest audit.</summary>
    public AuditRun? LastAudit { get; set; }

    /// <summary>Gets or sets a value indicating whether auto-apply is enabled for at least one library.</summary>
    public bool AutoApplyEnabled { get; set; }

    /// <summary>Gets or sets warnings (journal recovery, sidecar risk).</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Gets or sets scan events received.</summary>
    public long EventsReceived { get; set; }

    /// <summary>Gets or sets the last scan event.</summary>
    public DateTime? LastEventUtc { get; set; }

    /// <summary>Gets or sets the data directory (admin only).</summary>
    public string DataDirectory { get; set; } = string.Empty;
}

/// <summary>Coverage of existing media.</summary>
public sealed class CoverageDto
{
    /// <summary>Gets or sets the total Series/Movie records in scope.</summary>
    public int TotalItems { get; set; }

    /// <summary>Gets or sets the number audited at least once.</summary>
    public int CheckedItems { get; set; }

    /// <summary>Gets or sets a value indicating whether the initial audit completed.</summary>
    public bool InitialAuditCompleted { get; set; }

    /// <summary>Gets or sets the last audit completion.</summary>
    public DateTime? LastAuditCompletedUtc { get; set; }

    /// <summary>Gets or sets the last check time.</summary>
    public DateTime? LastCheckedUtc { get; set; }
}

/// <summary>Row in the findings list.</summary>
/// <param name="ReviewId">Review id.</param>
/// <param name="Version">ETag.</param>
/// <param name="ItemId">Item id.</param>
/// <param name="OriginalName">Original title.</param>
/// <param name="SearchTitle">Proposed (clean) title.</param>
/// <param name="TrustedYear">Year.</param>
/// <param name="MediaType">Type.</param>
/// <param name="LibraryName">Library.</param>
/// <param name="Status">Status.</param>
/// <param name="Reason">Reason.</param>
/// <param name="Origin">Origin.</param>
/// <param name="CandidateName">Best candidate.</param>
/// <param name="CandidateYear">Best candidate year.</param>
/// <param name="CandidateId">Best candidate id.</param>
/// <param name="Score">Best candidate score.</param>
/// <param name="AutoEligible">Passes the auto gate.</param>
/// <param name="Eligible">Bulk-eligible.</param>
/// <param name="CandidateCount">Number of candidates.</param>
/// <param name="LastCheckedUtc">Last checked.</param>
/// <param name="SidecarRisk">Library saves sidecars.</param>
public sealed record FindingRowDto(
    Guid ReviewId,
    int Version,
    Guid ItemId,
    string OriginalName,
    string SearchTitle,
    int? TrustedYear,
    string MediaType,
    string LibraryName,
    string Status,
    string? Reason,
    string Origin,
    string? CandidateName,
    int? CandidateYear,
    string? CandidateId,
    int? Score,
    bool AutoEligible,
    bool Eligible,
    int CandidateCount,
    DateTime LastCheckedUtc,
    bool SidecarRisk);

/// <summary>Paged result.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="Items">Rows.</param>
/// <param name="Total">Total matching.</param>
/// <param name="Skip">Skip.</param>
/// <param name="Take">Take.</param>
public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

/// <summary>Finding detail.</summary>
/// <param name="Finding">Finding.</param>
/// <param name="Path">Live media path (admin only).</param>
/// <param name="CurrentName">Live item name.</param>
/// <param name="CurrentProviderIds">Live provider ids.</param>
/// <param name="Decisions">Decision history.</param>
public sealed record FindingDetailDto(Finding Finding, string? Path, string? CurrentName, IReadOnlyDictionary<string, string>? CurrentProviderIds, IReadOnlyList<DecisionRecord> Decisions);

/// <summary>Apply request body.</summary>
public sealed class ApplyBody
{
    /// <summary>Gets or sets the candidate id.</summary>
    public string? CandidateId { get; set; }

    /// <summary>Gets or sets the finding version shown to the admin.</summary>
    public int Version { get; set; }
}

/// <summary>Version-only request body.</summary>
public sealed class VersionBody
{
    /// <summary>Gets or sets the finding version.</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets an optional note.</summary>
    public string? Note { get; set; }
}

/// <summary>Batch commit body.</summary>
public sealed class CommitBody
{
    /// <summary>Gets or sets the plan hash returned by preview.</summary>
    public string PlanHash { get; set; } = string.Empty;

    /// <summary>Gets or sets a client-generated idempotency key.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;
}

/// <summary>Audit start body.</summary>
public sealed class AuditBody
{
    /// <summary>Gets or sets a value indicating whether unchanged items are searched again.</summary>
    public bool Force { get; set; }
}

/// <summary>Normalization preview body.</summary>
public sealed class NormalizeBody
{
    /// <summary>Gets or sets the sample name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the media type (Series or Movie).</summary>
    public string MediaType { get; set; } = "Series";

    /// <summary>Gets or sets a value indicating whether the sample is a filename with an extension.</summary>
    public bool IsFile { get; set; }

    /// <summary>Gets or sets an unsaved rule pattern to try.</summary>
    public string? RulePattern { get; set; }
}

/// <summary>Result of an apply/rollback action.</summary>
/// <param name="Outcome">Outcome.</param>
/// <param name="Code">Code.</param>
/// <param name="Message">Message.</param>
/// <param name="Finding">Updated finding row.</param>
public sealed record ActionResultDto(string Outcome, string Code, string Message, FindingRowDto? Finding);

/// <summary>Error body.</summary>
/// <param name="Code">Code.</param>
/// <param name="Message">Message.</param>
/// <param name="Errors">Validation errors.</param>
public sealed record ErrorDto(string Code, string Message, IReadOnlyList<string>? Errors = null);

/// <summary>Helpers to build DTOs.</summary>
public static class Dto
{
    /// <summary>Builds a list row.</summary>
    /// <param name="f">Finding.</param>
    /// <returns>Row.</returns>
    public static FindingRowDto Row(Finding f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var top = f.Recommended() ?? (f.Candidates.Count > 0 ? f.Candidates[0] : null);
        var eligible = f.Status == FindingStatus.Proposed && f.Recommended() is { Applicable: true };
        return new FindingRowDto(
            f.ReviewId,
            f.Version,
            f.ItemId,
            f.OriginalName,
            f.SearchTitle,
            f.TrustedYear,
            f.MediaType.ToString(),
            f.LibraryName,
            f.Status.ToString(),
            f.Reason,
            f.Origin.ToString(),
            top?.Name,
            top?.Year,
            top?.CandidateId,
            top?.RankScore,
            f.Recommended()?.AutoEligible == true,
            eligible,
            f.Candidates.Count,
            f.LastCheckedUtc,
            f.SidecarRisk);
    }

    /// <summary>Builds a library row.</summary>
    /// <param name="l">Library.</param>
    /// <param name="inScope">In scope.</param>
    /// <param name="auto">Auto enabled.</param>
    /// <param name="ack">Sidecar acknowledged.</param>
    /// <returns>Row.</returns>
    public static LibraryDto Library(LibraryInfo l, bool inScope, bool auto, bool ack)
    {
        ArgumentNullException.ThrowIfNull(l);
        return new LibraryDto(l.Id, l.Name, l.CollectionType, inScope, auto, l.SidecarRisk, l.SidecarDetail, ack);
    }
}
