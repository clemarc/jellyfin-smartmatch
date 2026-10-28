using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartMatch.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartMatch.Matching;

/// <summary>
/// One provider failure.
/// </summary>
/// <param name="Provider">Provider name.</param>
/// <param name="Code">TIMEOUT, RATE_LIMITED, PROVIDER_5XX, NETWORK, PROVIDER_ERROR.</param>
/// <param name="Message">Message.</param>
/// <param name="Transient">True when retrying later may help.</param>
public sealed record ProviderFailure(string Provider, string Code, string Message, bool Transient);

/// <summary>
/// Result of searching every enabled provider for one title.
/// </summary>
/// <param name="Results">Raw candidates.</param>
/// <param name="Failures">Failed providers.</param>
/// <param name="ProvidersQueried">Number of providers asked.</param>
public sealed record ProviderSearchOutcome(IReadOnlyList<ProviderCandidate> Results, IReadOnlyList<ProviderFailure> Failures, int ProvidersQueried)
{
    /// <summary>Gets a value indicating whether every provider failed.</summary>
    public bool AllFailed => ProvidersQueried > 0 && Failures.Count == ProvidersQueried;
}

/// <summary>
/// Settings for remote searches.
/// </summary>
/// <param name="MinInterval">Minimum pause between consecutive provider requests.</param>
/// <param name="Timeout">Timeout per provider request.</param>
/// <param name="MaxAttempts">Attempts per provider for transient failures (1-3).</param>
public sealed record SearchBudget(TimeSpan MinInterval, TimeSpan Timeout, int MaxAttempts);

/// <summary>
/// Executes provider searches one at a time with a global minimum interval, a finite timeout per
/// call (enforced here, not trusted to the provider), and bounded exponential backoff with jitter
/// for transient failures. Jellyfin's own aggregate search swallows provider exceptions, so the
/// plugin calls each enabled provider itself to tell "no results" from "provider down".
/// </summary>
public sealed class ProviderSearchExecutor : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<ProviderSearchExecutor> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTime _lastRequestUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderSearchExecutor"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock.</param>
    public ProviderSearchExecutor(ILogger<ProviderSearchExecutor> logger, TimeProvider time)
        : this(logger, time, (d, ct) => Task.Delay(d, time, ct))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderSearchExecutor"/> class with an injectable delay (tests).
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock.</param>
    /// <param name="delay">Delay function.</param>
    public ProviderSearchExecutor(ILogger<ProviderSearchExecutor> logger, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _logger = logger;
        _time = time;
        _delay = delay;
    }

    /// <summary>Gets the total number of provider requests made.</summary>
    public long RequestCount { get; private set; }

    /// <summary>
    /// Searches all providers in the context.
    /// </summary>
    /// <param name="context">Providers.</param>
    /// <param name="title">Clean search title (never a path).</param>
    /// <param name="year">Trusted year.</param>
    /// <param name="budget">Budget.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Outcome.</returns>
    public async Task<ProviderSearchOutcome> SearchAsync(ProviderSearchContext context, string title, int? year, SearchBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);
        var results = new List<ProviderCandidate>();
        var failures = new List<ProviderFailure>();

        foreach (var provider in context.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderFailure? lastFailure = null;
            var attempts = Math.Clamp(budget.MaxAttempts, 1, 3);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    var found = await ThrottledCallAsync(provider, title, year, budget, cancellationToken).ConfigureAwait(false);
                    foreach (var r in found)
                    {
                        results.Add(r with { ProviderName = provider.Name });
                    }

                    lastFailure = null;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = Classify(provider.Name, ex);
                    _logger.LogWarning("Smart Match search on provider {Provider} failed (attempt {Attempt}/{Max}): {Code} {Message}", provider.Name, attempt, attempts, lastFailure.Code, lastFailure.Message);
                    if (!lastFailure.Transient || attempt == attempts)
                    {
                        break;
                    }

                    var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) * (0.5 + Random.Shared.NextDouble());
                    await _delay(backoff, cancellationToken).ConfigureAwait(false);
                }
            }

            if (lastFailure is not null)
            {
                failures.Add(lastFailure);
            }
        }

        return new ProviderSearchOutcome(results, failures, context.Providers.Count);
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Maps an exception to a failure code.
    /// </summary>
    /// <param name="provider">Provider.</param>
    /// <param name="ex">Exception.</param>
    /// <returns>Failure.</returns>
    public static ProviderFailure Classify(string provider, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var http = ex as HttpRequestException ?? ex.InnerException as HttpRequestException;
        if (ex is TimeoutException || ex is OperationCanceledException)
        {
            return new ProviderFailure(provider, "TIMEOUT", "Provider did not answer in time.", true);
        }

        if (http?.StatusCode is HttpStatusCode.TooManyRequests)
        {
            return new ProviderFailure(provider, "RATE_LIMITED", "Provider rate limit reached (429).", true);
        }

        if (http?.StatusCode is { } code && (int)code >= 500)
        {
            return new ProviderFailure(provider, "PROVIDER_5XX", string.Create(CultureInfo.InvariantCulture, $"Provider returned {(int)code}."), true);
        }

        if (http?.StatusCode is { } client && (int)client >= 400)
        {
            return new ProviderFailure(provider, "PROVIDER_4XX", string.Create(CultureInfo.InvariantCulture, $"Provider rejected the request ({(int)client})."), false);
        }

        if (http is not null)
        {
            // DNS, TLS/CA and connection failures surface here; never bypass certificate validation.
            return new ProviderFailure(provider, "NETWORK", "Network error: " + Truncate(http.Message), true);
        }

        return new ProviderFailure(provider, "PROVIDER_ERROR", Truncate(ex.GetType().Name + ": " + ex.Message), false);
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;

    private async Task<IReadOnlyList<ProviderCandidate>> ThrottledCallAsync(SearchProviderHandle provider, string title, int? year, SearchBudget budget, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var wait = _lastRequestUtc + budget.MinInterval - now;
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(budget.Timeout);
            RequestCount++;
            try
            {
                var call = provider.SearchAsync(title, year, timeout.Token);

                // Do not assume the provider honours cancellation: race it against the timeout.
                var finished = await Task.WhenAny(call, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token)).ConfigureAwait(false);
                if (finished != call)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = call.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    throw new TimeoutException("Provider search timed out.");
                }

                return await call.ConfigureAwait(false);
            }
            finally
            {
                _lastRequestUtc = _time.GetUtcNow().UtcDateTime;
                await timeout.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
