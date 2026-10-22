using System.Diagnostics;
using CloudHealthOffice.ClinicalCriteria.Abstractions;
using CloudHealthOffice.ClinicalCriteria.Configuration;
using CloudHealthOffice.ClinicalCriteria.Domain;
using CloudHealthOffice.ClinicalCriteria.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.ClinicalCriteria.Services;

/// <summary>
/// Routes a criteria request to its configured provider and normalizes the
/// outcome.
///
/// The whole class exists to keep one promise: a caller gets an evaluation
/// back, always. A provider that is missing, unconfigured, slow or broken
/// produces an inconclusive evaluation carrying the reason, and an
/// inconclusive evaluation pends to a human reviewer. Nothing here can turn an
/// infrastructure failure into a denial.
/// </summary>
public sealed class ClinicalCriteriaService : IClinicalCriteriaService
{
    private readonly IReadOnlyDictionary<string, IClinicalCriteriaProvider> _providers;
    private readonly ClinicalCriteriaOptions _options;
    private readonly ILogger<ClinicalCriteriaService> _logger;
    private readonly TimeProvider _time;

    public ClinicalCriteriaService(
        IEnumerable<IClinicalCriteriaProvider> providers,
        IOptions<ClinicalCriteriaOptions> options,
        ILogger<ClinicalCriteriaService> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;

        // Last registration of a provider id wins, so a deployment can replace
        // a shipped provider without removing it from the container.
        var byId = new Dictionary<string, IClinicalCriteriaProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers) byId[provider.ProviderId] = provider;
        _providers = byId;

        _logger.LogInformation(
            "Clinical criteria initialised with {Count} provider(s): {Providers}. Default: {Default}",
            _providers.Count,
            string.Join(", ", _providers.Values.Select(p => $"{p.ProviderId}{(p.IsConfigured ? "" : " (not configured)")}")),
            _options.DefaultProvider);
    }

    public IReadOnlyList<CriteriaProviderStatus> GetProviderStatus() =>
        _providers.Values
            .OrderBy(p => p.ProviderId, StringComparer.OrdinalIgnoreCase)
            .Select(p => new CriteriaProviderStatus
            {
                ProviderId   = p.ProviderId,
                DisplayName  = p.DisplayName,
                IsConfigured = p.IsConfigured,
                IsDefault    = string.Equals(p.ProviderId, _options.DefaultProvider, StringComparison.OrdinalIgnoreCase),
            })
            .ToList();

    public async Task<CriteriaEvaluation> EvaluateAsync(CriteriaRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var providerId = ResolveProviderId(request);

        if (!_providers.TryGetValue(providerId, out var provider))
        {
            _logger.LogError(
                "Clinical criteria provider '{ProviderId}' is routed to but not registered — pending to review",
                providerId);
            return Stamp(CriteriaEvaluation.Inconclusive(providerId, "provider-not-registered"), 0);
        }

        if (!provider.IsConfigured)
        {
            _logger.LogWarning(
                "Clinical criteria provider '{ProviderId}' is not configured — pending to review",
                providerId);
            return Stamp(CriteriaEvaluation.Inconclusive(providerId, "not-configured", provider.DisplayName), 0);
        }

        var sw = Stopwatch.StartNew();

        using var timeout = new CancellationTokenSource(_options.EvaluationTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            var evaluation = await provider.EvaluateAsync(request, linked.Token).ConfigureAwait(false);
            sw.Stop();

            if (evaluation is null)
            {
                _logger.LogError(
                    "Clinical criteria provider '{ProviderId}' returned no evaluation — pending to review", providerId);
                return Stamp(CriteriaEvaluation.Inconclusive(providerId, "provider-returned-null", provider.DisplayName), sw.ElapsedMilliseconds);
            }

            return Stamp(Normalize(evaluation, provider), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up, not the provider. Let that propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _logger.LogError(
                "Clinical criteria provider '{ProviderId}' did not answer within {Timeout} — pending to review",
                providerId, _options.EvaluationTimeout);
            return Stamp(CriteriaEvaluation.Inconclusive(providerId, "timeout", provider.DisplayName), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "Clinical criteria provider '{ProviderId}' failed — pending to review", providerId);
            return Stamp(
                CriteriaEvaluation.Inconclusive(providerId, $"provider-error:{ex.GetType().Name}", provider.DisplayName),
                sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// The most specific matching routing rule, else the configured default.
    /// A rule whose every set field equals the request's matches it.
    /// </summary>
    private string ResolveProviderId(CriteriaRequest request)
    {
        var match = (_options.Routing ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.Provider) && Matches(r, request))
            .OrderByDescending(r => r.Specificity)
            .FirstOrDefault();

        return match?.Provider ?? _options.DefaultProvider;
    }

    private static bool Matches(CriteriaRoutingRule rule, CriteriaRequest request) =>
        (rule.TenantId   is null || string.Equals(rule.TenantId, request.TenantId, StringComparison.OrdinalIgnoreCase)) &&
        (rule.StateCode  is null || string.Equals(rule.StateCode, request.StateCode, StringComparison.OrdinalIgnoreCase)) &&
        (rule.Lob        is null || rule.Lob == request.Lob) &&
        (rule.Program    is null || string.Equals(rule.Program, request.Program, StringComparison.OrdinalIgnoreCase)) &&
        (rule.ReviewType is null || rule.ReviewType == request.ReviewType);

    /// <summary>
    /// Holds the invariants a provider is not trusted to hold itself: the
    /// evaluation names the provider that produced it, an adverse finding
    /// always carries physician review, and only an inconclusive evaluation
    /// carries a failure reason.
    /// </summary>
    private static CriteriaEvaluation Normalize(CriteriaEvaluation evaluation, IClinicalCriteriaProvider provider) =>
        evaluation with
        {
            ProviderId = provider.ProviderId,
            ProviderDisplayName = evaluation.ProviderDisplayName ?? provider.DisplayName,
            RequiresPhysicianReview = evaluation.RequiresPhysicianReview
                || evaluation.Recommendation == CriteriaRecommendation.DoesNotMeetCriteria,
            FailureReason = evaluation.Recommendation == CriteriaRecommendation.Indeterminate
                ? evaluation.FailureReason
                : null,
        };

    private CriteriaEvaluation Stamp(CriteriaEvaluation evaluation, long elapsedMs) =>
        evaluation with { EvaluatedAtUtc = _time.GetUtcNow(), ElapsedMs = elapsedMs };
}
