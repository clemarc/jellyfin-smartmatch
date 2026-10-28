namespace Jellyfin.Plugin.SmartMatch.Model;

/// <summary>
/// Review state of a finding. Matches the state machine in TECHNICAL_DESIGN.md §8.
/// </summary>
public enum FindingStatus
{
    /// <summary>Waiting in the work queue (shown as "Not checked").</summary>
    Queued,

    /// <summary>One clear candidate; eligible for Apply one / selected / all eligible.</summary>
    Proposed,

    /// <summary>Ambiguous or weak candidates; an admin must pick one individually.</summary>
    NeedsReview,

    /// <summary>Providers answered but returned no usable candidate.</summary>
    NoMatch,

    /// <summary>Every provider request failed (timeout, rate limit, 5xx, no providers). Retried with backoff.</summary>
    ProviderError,

    /// <summary>An application is in progress.</summary>
    Applying,

    /// <summary>Provider identity written, refresh completed and postcondition verified.</summary>
    Applied,

    /// <summary>Identity written but refresh or verification failed; retry repeats only the refresh.</summary>
    Partial,

    /// <summary>The item changed between analysis and application; nothing was written.</summary>
    Conflict,

    /// <summary>Admin excluded the item; durable until cleared.</summary>
    Ignored,

    /// <summary>Admin rejected the candidates; durable until retried.</summary>
    Rejected,

    /// <summary>A plugin application was conditionally rolled back.</summary>
    RolledBack,

    /// <summary>The item became identified outside the plugin (e.g. Jellyfin Identify).</summary>
    Resolved,
}

/// <summary>
/// How a finding entered the report.
/// </summary>
public enum FindingOrigin
{
    /// <summary>Jellyfin scan / item event.</summary>
    NewScan,

    /// <summary>Audit of media indexed before or since installation.</summary>
    ExistingAudit,

    /// <summary>Post-scan or scheduled reconciliation.</summary>
    Reconciliation,

    /// <summary>Admin retry.</summary>
    Manual,
}

/// <summary>
/// Who triggered an application.
/// </summary>
public enum ApplyOrigin
{
    /// <summary>Apply one.</summary>
    Manual,

    /// <summary>Apply selected / apply all eligible.</summary>
    Bulk,

    /// <summary>Opt-in automatic application.</summary>
    Automatic,
}

/// <summary>
/// Outcome of one application attempt.
/// </summary>
public enum ApplyOutcome
{
    /// <summary>Applied and verified.</summary>
    Applied,

    /// <summary>Identity written, refresh/verification failed.</summary>
    Partial,

    /// <summary>Precondition failed; nothing written.</summary>
    Conflict,

    /// <summary>Policy excluded the item; nothing written.</summary>
    Skipped,

    /// <summary>Unexpected failure before any write.</summary>
    Failed,

    /// <summary>Batch cancelled before this item started.</summary>
    Cancelled,

    /// <summary>Conditional rollback restored plugin-owned fields.</summary>
    RolledBack,
}

/// <summary>
/// Lifecycle of an audit or batch run.
/// </summary>
public enum RunStatus
{
    /// <summary>Running.</summary>
    Running,

    /// <summary>All work finished.</summary>
    Completed,

    /// <summary>Cancelled by an admin; completed results kept.</summary>
    Cancelled,

    /// <summary>Server restarted while running; completed results kept.</summary>
    Interrupted,

    /// <summary>Run failed.</summary>
    Failed,
}
