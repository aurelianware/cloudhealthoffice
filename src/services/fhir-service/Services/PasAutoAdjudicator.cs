using System.Diagnostics;
using Hl7.Fhir.Model;
using FhirService.Models;
using Microsoft.Extensions.Options;

namespace FhirService.Services;

/// <summary>
/// Rules-based auto-adjudicator for Da Vinci PAS $submit.
/// Evaluates rules in order and short-circuits on first match.
/// Respects a time budget — returns PEND if time runs out.
/// </summary>
public class PasAutoAdjudicator : IPasAutoAdjudicator
{
    private readonly PasAutoAdjudicationConfig _config;
    private readonly HttpClient _authServiceClient;
    private readonly ILogger<PasAutoAdjudicator> _logger;

    public PasAutoAdjudicator(
        IOptions<PasAutoAdjudicationConfig> config,
        IHttpClientFactory httpClientFactory,
        ILogger<PasAutoAdjudicator> logger)
    {
        _config = config.Value;
        _authServiceClient = httpClientFactory.CreateClient("AuthorizationService");
        _logger = logger;
    }

    public async Task<PasDecisionResult> TryDecideAsync(
        Claim claim,
        Bundle context,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!_config.Enabled)
        {
            return Pend(sw, "ConfigDisabled");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var procedureCodes = ExtractProcedureCodes(claim);
        var providerNpi = ExtractProviderNpi(claim);

        try
        {
            // Rule 1: Deny — non-covered service
            if (_config.AutoDenyServiceTypes.Count > 0)
            {
                var denied = procedureCodes.FirstOrDefault(c => _config.AutoDenyServiceTypes.Contains(c));
                if (denied != null)
                {
                    return new PasDecisionResult
                    {
                        HasDecision = true,
                        Decision = "denied",
                        DenialReasonCode = "NOT_COVERED",
                        DenialReason = $"Service {denied} is not a covered benefit",
                        RuleName = "AutoDenyList",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            // Rule 2: Auto-approve — service type allowlist
            if (_config.AutoApproveServiceTypes.Count > 0 &&
                procedureCodes.Any(c => _config.AutoApproveServiceTypes.Contains(c)))
            {
                return Approve(sw, "AutoApproveList");
            }

            cts.Token.ThrowIfCancellationRequested();

            // Rule 3: Auto-approve — gold-card provider
            if (!string.IsNullOrEmpty(providerNpi))
            {
                var isGoldCard = await CheckGoldCardAsync(providerNpi, cts.Token);
                if (isGoldCard)
                {
                    return Approve(sw, "GoldCardProvider");
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            // Rule 4: Auto-approve — dollar threshold
            var estimatedCost = ExtractEstimatedCost(claim);
            if (estimatedCost.HasValue && estimatedCost.Value < _config.DollarThreshold)
            {
                return Approve(sw, "DollarThreshold");
            }

            // Rule 5: No match — PEND
            return Pend(sw, "NoRuleMatch");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("PAS auto-adjudication timed out after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return Pend(sw, "TimeBudgetExceeded");
        }
    }

    private async Task<bool> CheckGoldCardAsync(string providerNpi, CancellationToken ct)
    {
        try
        {
            var response = await _authServiceClient.GetAsync(
                $"api/authorizations/summary?providerNPI={providerNpi}", ct);

            if (!response.IsSuccessStatusCode)
                return false;

            var summary = await response.Content.ReadFromJsonAsync<AuthSummaryResponse>(ct);
            if (summary == null || summary.TotalAuthorizations < 20)
                return false;

            var approvalRate = (double)summary.ApprovalRate / 100.0;
            return approvalRate >= _config.GoldCardThreshold;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to check gold-card status for provider {NPI}", providerNpi);
            return false;
        }
    }

    private static List<string> ExtractProcedureCodes(Claim claim)
    {
        var codes = new List<string>();
        if (claim.Item != null)
        {
            foreach (var item in claim.Item)
            {
                if (item.ProductOrService?.Coding != null)
                {
                    codes.AddRange(item.ProductOrService.Coding
                        .Where(c => !string.IsNullOrEmpty(c.Code))
                        .Select(c => c.Code));
                }
            }
        }
        return codes;
    }

    private static string? ExtractProviderNpi(Claim claim)
    {
        return claim.Provider?.Identifier?.Value;
    }

    private static decimal? ExtractEstimatedCost(Claim claim)
    {
        if (claim.Total?.Value != null)
            return claim.Total.Value;

        // Sum item-level unit prices
        if (claim.Item == null) return null;
        decimal total = 0;
        bool hasAny = false;
        foreach (var item in claim.Item)
        {
            if (item.UnitPrice?.Value != null)
            {
                total += item.UnitPrice.Value.Value * (item.Quantity?.Value ?? 1);
                hasAny = true;
            }
        }
        return hasAny ? total : null;
    }

    private static PasDecisionResult Approve(Stopwatch sw, string ruleName) => new()
    {
        HasDecision = true,
        Decision = "approved",
        AuthorizationNumber = $"PAS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        EffectiveFrom = DateTime.UtcNow.Date,
        EffectiveTo = DateTime.UtcNow.Date.AddYears(1),
        RuleName = ruleName,
        ElapsedMs = sw.ElapsedMilliseconds,
    };

    private static PasDecisionResult Pend(Stopwatch sw, string ruleName) => new()
    {
        HasDecision = false,
        RuleName = ruleName,
        ElapsedMs = sw.ElapsedMilliseconds,
    };

    /// <summary>Lightweight DTO for deserializing authorization-service summary response.</summary>
    private class AuthSummaryResponse
    {
        public int TotalAuthorizations { get; set; }
        public decimal ApprovalRate { get; set; }
    }
}
