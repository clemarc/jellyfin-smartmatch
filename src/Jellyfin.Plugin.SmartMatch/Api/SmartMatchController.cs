using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Configuration;
using Jellyfin.Plugin.SmartMatch.Discovery;
using Jellyfin.Plugin.SmartMatch.Jobs;
using Jellyfin.Plugin.SmartMatch.Library;
using Jellyfin.Plugin.SmartMatch.Model;
using Jellyfin.Plugin.SmartMatch.Normalization;
using Jellyfin.Plugin.SmartMatch.Services;
using Jellyfin.Plugin.SmartMatch.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartMatch.Api;

/// <summary>
/// Smart Match administration API. Every route requires an authenticated Jellyfin administrator
/// (Jellyfin's RequiresElevation policy); no route accepts a filesystem path from the client.
/// Jellyfin authenticates with a token header rather than ambient cookies, which is what protects
/// these write endpoints from cross-site requests.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("SmartMatch")]
[Produces(MediaTypeNames.Application.Json)]
public class SmartMatchController : ControllerBase
{
    private const int MaxTake = 200;

    private static readonly System.Buffers.SearchValues<char> CsvSpecial = System.Buffers.SearchValues.Create(",\"\n\r");

    private readonly SmartMatchStore _store;
    private readonly ILibraryGateway _library;
    private readonly IConfigProvider _config;
    private readonly IServerInfo _server;
    private readonly MatchApplicationService _apply;
    private readonly BulkApplyCoordinator _bulk;
    private readonly AuditService _audits;
    private readonly IMediaNameNormalizer _normalizer;
    private readonly WorkerStatus _status;
    private readonly SmartMatchWorker _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartMatchController"/> class.
    /// </summary>
    /// <param name="store">Store.</param>
    /// <param name="library">Library gateway.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="server">Server info.</param>
    /// <param name="apply">Apply service.</param>
    /// <param name="bulk">Bulk coordinator.</param>
    /// <param name="audits">Audit service.</param>
    /// <param name="normalizer">Normalizer.</param>
    /// <param name="status">Worker status.</param>
    /// <param name="worker">Worker.</param>
    public SmartMatchController(
        SmartMatchStore store,
        ILibraryGateway library,
        IConfigProvider config,
        IServerInfo server,
        MatchApplicationService apply,
        BulkApplyCoordinator bulk,
        AuditService audits,
        IMediaNameNormalizer normalizer,
        WorkerStatus status,
        SmartMatchWorker worker)
    {
        _store = store;
        _library = library;
        _config = config;
        _server = server;
        _apply = apply;
        _bulk = bulk;
        _audits = audits;
        _normalizer = normalizer;
        _status = status;
        _worker = worker;
    }

    private string Actor => User.FindFirst("Jellyfin-UserId")?.Value ?? User.Identity?.Name ?? "admin";

    /// <summary>Health, version, summary and coverage.</summary>
    /// <returns>Status.</returns>
    [HttpGet("Status")]
    public ActionResult<StatusDto> GetStatus()
    {
        _store.Load();
        var config = _config.Current;
        var libraries = _library.GetLibraries();
        var inScope = LibraryScope.InScope(libraries, config);
        var counts = _store.CountByStatus();
        var state = _store.GetState();
        var runs = _store.GetAuditRuns();
        var (checkedCount, newest) = _store.CoverageStats(inScope);
        var total = SafeCount(inScope);
        var (due, deferred) = _store.QueueDueCounts(DateTime.UtcNow);
        var dto = new StatusDto
        {
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            ServerVersion = _server.ServerVersion.ToString(),
            Compatible = _server.IsSupported,
            Mode = config.Mode.ToString(),
            MonitoringActive = _status.MonitoringActive && config.MonitorNewScans,
            WorkerState = _status.State,
            Fault = _status.Fault,
            QueueLength = _store.QueueCount(),
            QueueDue = due,
            QueueDeferred = deferred,
            QueueByOrigin = _store.QueueCountByOrigin().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            Counts = Enum.GetValues<FindingStatus>().ToDictionary(s => s.ToString(), s => counts.GetValueOrDefault(s)),
            Eligible = _store.QueryFindings(f => f.Status == FindingStatus.Proposed && f.Recommended() is { Applicable: true } && inScope.Contains(f.LibraryId)).Count,
            Libraries = libraries.Select(l => Dto.Library(l, inScope.Contains(l.Id), LibraryScope.AutoApplyEnabled(config, l.Id), LibraryScope.SidecarAcknowledged(config, l.Id))).ToList(),
            Coverage = new CoverageDto
            {
                TotalItems = total,
                CheckedItems = Math.Min(checkedCount, total),
                InitialAuditCompleted = state.InitialAuditCompleted,
                LastAuditCompletedUtc = state.LastAuditCompletedUtc,
                LastCheckedUtc = newest,
            },
            LastReconciliationUtc = state.LastReconciliationUtc,
            ActiveAudit = runs.FirstOrDefault(r => r.Status == RunStatus.Running && r.Kind != RunKinds.Reconciliation),
            LastAudit = runs.FirstOrDefault(r => r.Kind != RunKinds.Reconciliation),
            AutoApplyEnabled = config.Mode == ApplyMode.AutoApplyHighConfidence && config.AutoApplyLibraryIds.Length > 0,
            EventsReceived = _status.EventsReceived,
            LastEventUtc = _status.LastEventUtc,
            DataDirectory = _store.DirectoryPath,
        };
        dto.Warnings.AddRange(_store.LoadWarnings);
        foreach (var lib in libraries.Where(l => l.SidecarRisk && inScope.Contains(l.Id)))
        {
            dto.Warnings.Add($"Library '{lib.Name}': Jellyfin itself may write NFO/artwork files next to the media during a refresh ({lib.SidecarDetail}). Automatic application is blocked there unless acknowledged.");
        }

        if (inScope.Count == 0)
        {
            dto.Warnings.Add("No libraries are selected; Smart Match is not checking anything.");
        }

        return dto;
    }

    /// <summary>Gets settings.</summary>
    /// <returns>Configuration.</returns>
    [HttpGet("Config")]
    public ActionResult<PluginConfiguration> GetConfig() => _config.Current;

    /// <summary>Validates and saves settings.</summary>
    /// <param name="configuration">Configuration.</param>
    /// <returns>Saved configuration, or 400 with validation errors.</returns>
    [HttpPut("Config")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PluginConfiguration> PutConfig([FromBody] PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count > 0)
        {
            return BadRequest(new ErrorDto("INVALID_CONFIG", "The configuration is invalid.", errors));
        }

        _config.Save(configuration);
        _worker.Signal();
        return _config.Current;
    }

    /// <summary>Previews the normalizer (and optionally an unsaved prefix rule) on a sample name.</summary>
    /// <param name="body">Sample.</param>
    /// <returns>Normalization result.</returns>
    [HttpPost("Normalize/Preview")]
    public ActionResult<NormalizationResult> PreviewNormalize([FromBody] NormalizeBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > MediaNameNormalizer.MaxInputLength)
        {
            return BadRequest(new ErrorDto("INVALID_INPUT", "Provide a name of 1-512 characters."));
        }

        var kind = string.Equals(body.MediaType, "Movie", StringComparison.OrdinalIgnoreCase) ? MediaKind.Movie : MediaKind.Series;
        var rules = ConfigurationValidator.BuildRuleSet(_config.Current);
        if (!string.IsNullOrWhiteSpace(body.RulePattern))
        {
            if (!RuleSet.TryCompile(body.RulePattern, out _, out var error))
            {
                return BadRequest(new ErrorDto("INVALID_RULE", error ?? "Invalid pattern."));
            }

            var config = _config.Current;
            rules = RuleSet.Create((config.PrefixRules ?? Array.Empty<UserPrefixRule>())
                .Where(r => r.Enabled)
                .Select(r => new PrefixRule("user." + r.Id, r.Description, r.Pattern, r.ExampleInput, r.ExampleOutput, false))
                .Append(new PrefixRule("preview", "Unsaved rule", body.RulePattern, body.Name, string.Empty, false)));
        }

        var source = body.IsFile ? (kind == MediaKind.Movie ? NameSourceKind.MovieFile : NameSourceKind.EpisodeFile) : NameSourceKind.Preview;
        return _normalizer.Normalize(new MediaNameInput(body.Name, source, kind), rules);
    }

    /// <summary>Starts (or returns the running) existing-library audit.</summary>
    /// <param name="body">Options.</param>
    /// <returns>Run.</returns>
    [HttpPost("Audits")]
    public ActionResult<AuditRun> StartAudit([FromBody] AuditBody? body)
    {
        if (!_server.IsSupported)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorDto("UNSUPPORTED_SERVER", "This Jellyfin version is not supported."));
        }

        return _audits.StartAudit(RunKinds.Audit, Actor, body?.Force ?? false);
    }

    /// <summary>Queues a reconciliation now.</summary>
    /// <returns>Run.</returns>
    [HttpPost("Reconcile")]
    public ActionResult<AuditRun> Reconcile()
    {
        if (!_server.IsSupported)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorDto("UNSUPPORTED_SERVER", "This Jellyfin version is not supported."));
        }

        return _audits.Reconcile(Actor);
    }

    /// <summary>Lists audit runs.</summary>
    /// <returns>Runs.</returns>
    [HttpGet("Audits")]
    public ActionResult<IReadOnlyList<AuditRun>> ListAudits() => _store.GetAuditRuns().Take(50).ToList();

    /// <summary>Gets an audit run.</summary>
    /// <param name="id">Run id.</param>
    /// <returns>Run.</returns>
    [HttpGet("Audits/{id}")]
    public ActionResult<AuditRun> GetAudit([FromRoute] Guid id)
    {
        var run = _store.GetAuditRun(id);
        return run is null ? NotFound(new ErrorDto("NOT_FOUND", "Run not found.")) : run;
    }

    /// <summary>Cancels an audit run's queued work.</summary>
    /// <param name="id">Run id.</param>
    /// <returns>Run.</returns>
    [HttpPost("Audits/{id}/Cancel")]
    public ActionResult<AuditRun> CancelAudit([FromRoute] Guid id)
    {
        var run = _audits.Cancel(id);
        return run is null ? NotFound(new ErrorDto("NOT_FOUND", "Run not found.")) : run;
    }

    /// <summary>Coverage and monitoring status.</summary>
    /// <returns>Coverage.</returns>
    [HttpGet("Coverage")]
    public ActionResult<object> GetCoverage()
    {
        var status = GetStatus().Value!;
        return new { status.Coverage, status.MonitoringActive, status.QueueLength, status.QueueByOrigin, status.LastReconciliationUtc, status.ActiveAudit, status.LastAudit };
    }

    /// <summary>Paginated findings report.</summary>
    /// <param name="status">Comma-separated statuses.</param>
    /// <param name="libraryId">Library.</param>
    /// <param name="mediaType">Series or Movie.</param>
    /// <param name="confidence">auto, high, medium, low.</param>
    /// <param name="search">Title search.</param>
    /// <param name="origin">Origin.</param>
    /// <param name="sort">review (default), recent, title, score.</param>
    /// <param name="skip">Skip.</param>
    /// <param name="take">Take (max 200).</param>
    /// <returns>Page.</returns>
    [HttpGet("Reviews")]
    public ActionResult<PageDto<FindingRowDto>> GetReviews(
        [FromQuery] string? status,
        [FromQuery] Guid? libraryId,
        [FromQuery] string? mediaType,
        [FromQuery] string? confidence,
        [FromQuery] string? search,
        [FromQuery] string? origin,
        [FromQuery] string? sort,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        if (!TryBuildFilter(status, libraryId, mediaType, confidence, search, origin, out var filter, out var error))
        {
            return BadRequest(error);
        }

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);
        Func<IEnumerable<Finding>, IOrderedEnumerable<Finding>> order = (sort ?? "review").ToLowerInvariant() switch
        {
            "recent" => q => q.OrderByDescending(f => f.LastCheckedUtc),
            "title" => q => q.OrderBy(f => f.OriginalName, StringComparer.OrdinalIgnoreCase),
            "score" => q => q.OrderByDescending(f => f.Recommended()?.RankScore ?? -1).ThenByDescending(f => f.LastCheckedUtc),
            _ => q => q.OrderBy(f => StatusRank(f.Status)).ThenByDescending(f => f.LastCheckedUtc),
        };
        var page = _store.QueryFindingsPage(filter!.Matches, order, skip, take, out var total);
        return new PageDto<FindingRowDto>(page.Select(Dto.Row).ToList(), total, skip, take);
    }

    /// <summary>Finding detail with live path (admin only), alternatives, trace and decisions.</summary>
    /// <param name="id">Review id.</param>
    /// <returns>Detail.</returns>
    [HttpGet("Reviews/{id}")]
    public ActionResult<FindingDetailDto> GetReview([FromRoute] Guid id)
    {
        var finding = _store.GetFinding(id);
        if (finding is null)
        {
            return NotFound(new ErrorDto("NOT_FOUND", "Finding not found."));
        }

        var item = _library.GetItem(finding.ItemId);
        var decisions = _store.GetDecisions(id, 0, 50, out _);
        return new FindingDetailDto(finding, item?.Path, item?.Name, item?.ProviderIds, decisions);
    }

    /// <summary>Applies one candidate (Apply one). Requires the version (ETag) the admin saw.</summary>
    /// <param name="id">Review id.</param>
    /// <param name="body">Candidate and version.</param>
    /// <returns>Outcome.</returns>
    [HttpPost("Reviews/{id}/Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ActionResultDto>> Apply([FromRoute] Guid id, [FromBody] ApplyBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Version <= 0)
        {
            return BadRequest(new ErrorDto("VERSION_REQUIRED", "The finding version is required."));
        }

        var result = await _apply.ApplyAsync(new ApplyRequest(id, string.IsNullOrWhiteSpace(body.CandidateId) ? null : body.CandidateId, body.Version, ApplyOrigin.Manual, Actor), CancellationToken.None).ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>Conditionally rolls back a plugin application.</summary>
    /// <param name="id">Review id.</param>
    /// <param name="body">Version.</param>
    /// <returns>Outcome.</returns>
    [HttpPost("Reviews/{id}/Rollback")]
    public async Task<ActionResult<ActionResultDto>> Rollback([FromRoute] Guid id, [FromBody] VersionBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await _apply.RollbackAsync(id, body.Version, Actor, CancellationToken.None).ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>Ignores an item until cleared.</summary>
    /// <param name="id">Review id.</param>
    /// <param name="body">Version and optional reason.</param>
    /// <returns>Updated row.</returns>
    [HttpPost("Reviews/{id}/Ignore")]
    public ActionResult<ActionResultDto> Ignore([FromRoute] Guid id, [FromBody] VersionBody body)
        => SetDurableState(id, body, FindingStatus.Ignored, "Ignore", "IGNORED", "Ignored by an admin.");

    /// <summary>Rejects the candidates of a finding.</summary>
    /// <param name="id">Review id.</param>
    /// <param name="body">Version and optional reason.</param>
    /// <returns>Updated row.</returns>
    [HttpPost("Reviews/{id}/Reject")]
    public ActionResult<ActionResultDto> Reject([FromRoute] Guid id, [FromBody] VersionBody body)
        => SetDurableState(id, body, FindingStatus.Rejected, "Reject", "REJECTED", "Candidates rejected by an admin.");

    /// <summary>Re-analyses an item (also clears Ignore/Reject/Conflict/RolledBack).</summary>
    /// <param name="id">Review id.</param>
    /// <param name="body">Version.</param>
    /// <returns>Updated row.</returns>
    [HttpPost("Reviews/{id}/Retry")]
    public async Task<ActionResult<ActionResultDto>> Retry([FromRoute] Guid id, [FromBody] VersionBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var finding = _store.GetFinding(id);
        if (finding is null)
        {
            return NotFound(new ErrorDto("NOT_FOUND", "Finding not found."));
        }

        if (finding.Status == FindingStatus.Partial)
        {
            // Retry of a partial application repeats only the refresh, after verification.
            var result = await _apply.ApplyAsync(new ApplyRequest(id, null, body.Version, ApplyOrigin.Manual, Actor), CancellationToken.None).ConfigureAwait(false);
            return ToActionResult(result);
        }

        if (finding.Status is FindingStatus.Applying or FindingStatus.Applied)
        {
            return Conflict(new ErrorDto("NOT_RETRYABLE", "Applied findings cannot be re-analysed; roll back first."));
        }

        return SetDurableState(id, body, FindingStatus.Queued, "Retry", "QUEUED", "Queued for re-analysis by an admin.", requeue: true);
    }

    /// <summary>Previews Apply selected / Apply all eligible. No Jellyfin mutation.</summary>
    /// <param name="request">Scope.</param>
    /// <returns>Plan.</returns>
    [HttpPost("ApplyBatch/Preview")]
    public ActionResult<BatchPlan> PreviewBatch([FromBody] BatchPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return _bulk.Preview(request, Actor);
        }
        catch (BatchException ex)
        {
            return BadRequest(new ErrorDto(ex.Code, ex.Message));
        }
    }

    /// <summary>Confirms a previewed plan and starts it.</summary>
    /// <param name="planId">Plan id.</param>
    /// <param name="body">Plan hash and idempotency key.</param>
    /// <returns>Run.</returns>
    [HttpPost("ApplyBatch/{planId}/Commit")]
    public ActionResult<BatchRun> CommitBatch([FromRoute] Guid planId, [FromBody] CommitBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!_server.IsSupported)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorDto("UNSUPPORTED_SERVER", "This Jellyfin version is not supported."));
        }

        try
        {
            return _bulk.Commit(planId, body.PlanHash, body.IdempotencyKey, Actor);
        }
        catch (BatchException ex)
        {
            return ex.Code is "PLAN_EXPIRED" ? StatusCode(StatusCodes.Status410Gone, new ErrorDto(ex.Code, ex.Message)) : Conflict(new ErrorDto(ex.Code, ex.Message));
        }
    }

    /// <summary>Gets batch progress and per-item outcomes.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>Run.</returns>
    [HttpGet("ApplyBatch/{runId}")]
    public ActionResult<BatchRun> GetBatch([FromRoute] Guid runId)
    {
        var run = _store.GetBatchRun(runId);
        return run is null ? NotFound(new ErrorDto("NOT_FOUND", "Run not found.")) : run;
    }

    /// <summary>Lists recent batch runs.</summary>
    /// <returns>Runs.</returns>
    [HttpGet("ApplyBatch")]
    public ActionResult<IReadOnlyList<BatchRun>> ListBatches() => _store.GetBatchRuns().Take(20).ToList();

    /// <summary>Stops scheduling further writes in a batch; completed items stay applied.</summary>
    /// <param name="runId">Run id.</param>
    /// <returns>Run.</returns>
    [HttpPost("ApplyBatch/{runId}/Cancel")]
    public ActionResult<BatchRun> CancelBatch([FromRoute] Guid runId)
    {
        _bulk.Cancel(runId);
        var run = _store.GetBatchRun(runId);
        return run is null ? NotFound(new ErrorDto("NOT_FOUND", "Run not found.")) : run;
    }

    /// <summary>Change history (decision records), newest first.</summary>
    /// <param name="skip">Skip.</param>
    /// <param name="take">Take.</param>
    /// <returns>Page.</returns>
    [HttpGet("Decisions")]
    public ActionResult<PageDto<DecisionRecord>> GetDecisions([FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);
        var items = _store.GetDecisions(null, skip, take, out var total);
        return new PageDto<DecisionRecord>(items, total, skip, take);
    }

    /// <summary>Exports the report (no paths) as JSON or CSV.</summary>
    /// <param name="format">json or csv.</param>
    /// <param name="status">Optional status filter.</param>
    /// <returns>File.</returns>
    [HttpGet("Export")]
    public ActionResult Export([FromQuery] string? format, [FromQuery] string? status)
    {
        if (!TryBuildFilter(status, null, null, null, null, null, out var filter, out var error))
        {
            return BadRequest(error);
        }

        var findings = _store.QueryFindings(filter!.Matches).OrderBy(f => StatusRank(f.Status)).ThenBy(f => f.OriginalName, StringComparer.OrdinalIgnoreCase).ToList();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.AppendLine("ReviewId,Status,MediaType,Library,OriginalName,SearchTitle,Year,Candidate,CandidateYear,CandidateIds,Score,AutoEligible,Reason,Origin,LastCheckedUtc");
            foreach (var f in findings)
            {
                var top = f.Recommended();
                sb.AppendLine(string.Join(',', new[]
                {
                    f.ReviewId.ToString(), f.Status.ToString(), f.MediaType.ToString(), f.LibraryName, f.OriginalName, f.SearchTitle,
                    f.TrustedYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, top?.Name ?? string.Empty,
                    top?.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    top is null ? string.Empty : string.Join(' ', top.ProviderIds.Select(kv => kv.Key + "=" + kv.Value)),
                    top?.RankScore.ToString(CultureInfo.InvariantCulture) ?? string.Empty, (top?.AutoEligible ?? false).ToString(),
                    f.Reason ?? string.Empty, f.Origin.ToString(), f.LastCheckedUtc.ToString("o", CultureInfo.InvariantCulture),
                }.Select(Csv)));
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"smartmatch-report-{stamp}.csv");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(findings, JsonFileStore.Options);
        return File(json, MediaTypeNames.Application.Json, $"smartmatch-report-{stamp}.json");
    }

    private static int StatusRank(FindingStatus s) => s switch
    {
        FindingStatus.NeedsReview => 0,
        FindingStatus.Proposed => 1,
        FindingStatus.Conflict => 2,
        FindingStatus.Partial => 3,
        FindingStatus.ProviderError => 4,
        FindingStatus.Queued => 5,
        FindingStatus.Applying => 6,
        FindingStatus.NoMatch => 7,
        FindingStatus.Applied => 8,
        FindingStatus.RolledBack => 9,
        FindingStatus.Rejected => 10,
        FindingStatus.Ignored => 11,
        _ => 12,
    };

    private static string Csv(string value)
    {
        // Neutralise spreadsheet formula injection from filenames.
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.AsSpan().IndexOfAny(CsvSpecial) >= 0 ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
    }

    private static bool TryBuildFilter(string? status, Guid? libraryId, string? mediaType, string? confidence, string? search, string? origin, out FindingFilter? filter, out ErrorDto? error)
    {
        filter = new FindingFilter { LibraryId = libraryId, Confidence = confidence, Search = search?.Length > 200 ? search[..200] : search };
        error = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            foreach (var part in status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<FindingStatus>(part, true, out var s))
                {
                    error = new ErrorDto("INVALID_FILTER", $"Unknown status '{part}'.");
                    return false;
                }

                filter.Statuses.Add(s);
            }
        }

        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            if (!Enum.TryParse<MediaKind>(mediaType, true, out var mt))
            {
                error = new ErrorDto("INVALID_FILTER", "Unknown media type.");
                return false;
            }

            filter.MediaType = mt;
        }

        if (!string.IsNullOrWhiteSpace(origin))
        {
            if (!Enum.TryParse<FindingOrigin>(origin, true, out var o))
            {
                error = new ErrorDto("INVALID_FILTER", "Unknown origin.");
                return false;
            }

            filter.Origin = o;
        }

        return true;
    }

    private int SafeCount(IReadOnlySet<Guid> libraries)
    {
        try
        {
            return _library.GetCandidateItemIds(libraries.ToList(), null).Count;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private ActionResult<ActionResultDto> ToActionResult(ApplyResult result)
    {
        var dto = new ActionResultDto(result.Outcome.ToString(), result.Code, result.Message, result.Finding is null ? null : Dto.Row(result.Finding));
        return result.Code switch
        {
            "NOT_FOUND" => NotFound(dto),
            "UNSUPPORTED_SERVER" => StatusCode(StatusCodes.Status503ServiceUnavailable, dto),
            _ when result.Outcome == ApplyOutcome.Conflict => Conflict(dto),
            _ => Ok(dto),
        };
    }

    private ActionResult<ActionResultDto> SetDurableState(Guid id, VersionBody body, FindingStatus status, string action, string code, string reason, bool requeue = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        var note = body.Note?.Length > 500 ? body.Note[..500] : body.Note;
        var result = _store.TryUpdateFinding(id, body.Version, f =>
        {
            if (f.Status is FindingStatus.Applying)
            {
                return false;
            }

            if (status != FindingStatus.Queued && f.Status is FindingStatus.Applied or FindingStatus.Partial)
            {
                return false;
            }

            f.Status = status;
            f.ReasonCode = code;
            f.Reason = string.IsNullOrWhiteSpace(note) ? reason : reason + " Note: " + note;
            f.AdminNote = note;
            return true;
        }, out var updated);

        switch (result)
        {
            case UpdateResult.NotFound:
                return NotFound(new ErrorDto("NOT_FOUND", "Finding not found."));
            case UpdateResult.VersionMismatch:
                return Conflict(new ActionResultDto("Conflict", "STALE_VERSION", "The finding changed since it was displayed; reload.", updated is null ? null : Dto.Row(updated)));
            case UpdateResult.Aborted:
                return Conflict(new ActionResultDto("Conflict", "INVALID_STATE", "This action is not allowed for the current status.", updated is null ? null : Dto.Row(updated)));
        }

        _store.AddDecision(
            new DecisionRecord
            {
                DecisionId = Guid.NewGuid(),
                ReviewId = id,
                ItemId = updated!.ItemId,
                ItemName = updated.OriginalName,
                Actor = Actor,
                TimeUtc = DateTime.UtcNow,
                Action = action,
                Outcome = ApplyOutcome.Skipped,
                Message = updated.Reason,
            },
            _config.Current.RetentionDays,
            _config.Current.RetentionMaxDecisions);

        if (requeue)
        {
            _store.Enqueue(updated.ItemId, FindingOrigin.Manual, null, DateTime.UtcNow, force: true);
            _worker.Signal();
        }

        return new ActionResultDto("Updated", code, updated.Reason ?? reason, Dto.Row(updated));
    }
}
