using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;

namespace ClaimsService.Services;

/// <summary>
/// Applies the FL SMMC 3.0 MPIP 106.3% Medicare multiplier to adjudicated
/// claim lines when the tenant has MPIP enabled and the service qualifies.
///
/// Called after the base allowed amount is calculated (from fee schedule / contracted rates)
/// and before the final adjudication result is persisted.
/// </summary>
public interface IMpipAdjudicationEnhancer
{
    /// <summary>
    /// Apply MPIP enhanced rate to claim lines if applicable.
    /// Modifies line-level AllowedAmount and PaidAmount in place.
    /// No-op if MPIP is not enabled for the tenant or member is 21+.
    /// </summary>
    Task ApplyMpipEnhancementAsync(Claim claim, int memberAgeAtServiceDate);
}

public class MpipAdjudicationEnhancer : IMpipAdjudicationEnhancer
{
    private readonly IMpipRateClient _mpipClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MpipAdjudicationEnhancer> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public MpipAdjudicationEnhancer(
        IMpipRateClient mpipClient,
        IHttpClientFactory httpClientFactory,
        ILogger<MpipAdjudicationEnhancer> logger)
    {
        _mpipClient = mpipClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task ApplyMpipEnhancementAsync(Claim claim, int memberAgeAtServiceDate)
    {
        // Step 1: Check if tenant has MPIP enabled
        var mpipEnabled = await IsMpipEnabledAsync(claim.TenantId);
        if (!mpipEnabled)
        {
            return;
        }

        // Step 2: Get the MPIP multiplier
        var multiplier = await _mpipClient.GetMultiplierAsync(
            claim.BillingProviderNPI, claim.TenantId,
            claim.ServiceDateFrom, memberAgeAtServiceDate);

        if (multiplier <= 1.0m)
        {
            return;
        }

        // Step 3: Apply multiplier to each claim line's allowed amount
        foreach (var line in claim.ClaimLines)
        {
            if (line.AdjudicationResult is null) continue;

            var originalAllowed = line.AdjudicationResult.AllowedAmount;
            var enhancedAllowed = Math.Round(originalAllowed * multiplier, 2);
            var difference = enhancedAllowed - originalAllowed;

            line.AdjudicationResult.AllowedAmount = enhancedAllowed;
            line.AdjudicationResult.PaidAmount += difference;
            line.MpipMultiplierApplied = multiplier;

            _logger.LogInformation(
                "MPIP enhanced rate applied: claim {ClaimNumber} line {LineNumber}, " +
                "provider {ProviderId}, memberAge {MemberAge}, multiplier {Multiplier}x, " +
                "allowed {Original:F2} -> {Enhanced:F2} (+{Difference:F2})",
                claim.ClaimNumber, line.LineNumber,
                claim.BillingProviderNPI, memberAgeAtServiceDate, multiplier,
                originalAllowed, enhancedAllowed, difference);
        }

        // Step 4: Recalculate claim-level totals
        if (claim.AdjudicationResult is not null)
        {
            var originalClaimAllowed = claim.AdjudicationResult.AllowedAmount;
            claim.AdjudicationResult.AllowedAmount = claim.ClaimLines
                .Sum(l => l.AdjudicationResult?.AllowedAmount ?? 0);
            claim.AdjudicationResult.PayerPayment = claim.ClaimLines
                .Sum(l => l.AdjudicationResult?.PaidAmount ?? 0);

            _logger.LogInformation(
                "MPIP claim-level totals recalculated: claim {ClaimNumber}, " +
                "allowed {Original:F2} -> {Enhanced:F2}",
                claim.ClaimNumber, originalClaimAllowed, claim.AdjudicationResult.AllowedAmount);
        }
    }

    /// <summary>
    /// Check if MPIP is enabled for the tenant via the compliance config API.
    /// </summary>
    private async Task<bool> IsMpipEnabledAsync(string tenantId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ReferenceDataService");
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/compliance-config/{Uri.EscapeDataString(tenantId)}");
            request.Headers.Add("X-Tenant-ID", tenantId);

            var response = await client.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var config = await response.Content.ReadFromJsonAsync<ComplianceConfigDto>(JsonOptions);
                return config?.MpipEnabled ?? false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to check MPIP enabled status for tenant {TenantId}, defaulting to disabled",
                tenantId);
        }

        return false;
    }
}

internal class ComplianceConfigDto
{
    public bool MpipEnabled { get; set; }
    public string? FmmisSubmitterId { get; set; }
}
