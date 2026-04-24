using System.Diagnostics;
using Hl7.Fhir.Model;
using FhirService.Models;
using Microsoft.Extensions.Options;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;  // ← NEW
using CloudHealthOffice.ProviderEnrollmentService.Models;        // ← NEW
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;        // ← NEW
using CloudHealthOffice.PriorAuthRuleEngine.Domain;              // ← NEW
using CloudHealthOffice.PriorAuthRuleEngine.Models;              // ← NEW

namespace FhirService.Services;

/// <summary>
/// Rules-based auto-adjudicator for Da Vinci PAS $submit.
/// Evaluates rules in order and short-circuits on first match.
/// Respects a time budget — returns PEND if time runs out.
///
/// Rule execution order:
///   Rule 0: StateEnrollmentGate        — provider must be enrolled in state Medicaid
///   Rule 1: AutoDenyList               — non-covered service types (config)
///   Rule 2: AutoApproveList            — service type allowlist (config)
///   Rule 3: GoldCard (approval rate)   — existing authorization-service check
///   Rule 4: DollarThreshold            — cost-based auto-approve (config)
///   Rule 5: PriorAuthRuleEngine        — state-specific clinical/regulatory rules
///   Rule 6: PEND                       — no rule reached a conclusion
/// </summary>
public class PasAutoAdjudicator : IPasAutoAdjudicator
{
    private readonly PasAutoAdjudicationConfig _config;
    private readonly HttpClient _authServiceClient;
    private readonly IEnrollmentDecisionGate _enrollmentGate;    // ← NEW
    private readonly IPriorAuthRuleEngine _ruleEngine;           // ← NEW
    private readonly ILogger<PasAutoAdjudicator> _logger;

    public PasAutoAdjudicator(
        IOptions<PasAutoAdjudicationConfig> config,
        IHttpClientFactory httpClientFactory,
        IEnrollmentDecisionGate enrollmentGate,                  // ← NEW
        IPriorAuthRuleEngine ruleEngine,                         // ← NEW
        ILogger<PasAutoAdjudicator> logger)
    {
        _config          = config.Value;
        _authServiceClient = httpClientFactory.CreateClient("AuthorizationService");
        _enrollmentGate  = enrollmentGate;
        _ruleEngine      = ruleEngine;
        _logger          = logger;
    }

    public async Task<PasDecisionResult> TryDecideAsync(
        Claim claim,
        Bundle context,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (!_config.Enabled)
            return Pend(sw, "ConfigDisabled");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var procedureCodes = ExtractProcedureCodes(claim);
        var providerNpi    = ExtractProviderNpi(claim);
        var taxonomy       = ExtractTaxonomy(claim);
        var serviceDate    = ExtractServiceDate(claim);
        var stateCode      = ExtractStateCode(claim);
        var lob            = ExtractLineOfBusiness(claim);

        try
        {
            // ── Rule 0: State Medicaid enrollment gate ────────────────────
            // Provider must be actively enrolled in state Medicaid before any
            // clinical rules are evaluated. Unenrolled providers exit here
            // without touching the 400+ TX Medicaid pathways.
            if (!string.IsNullOrEmpty(providerNpi) && !string.IsNullOrEmpty(stateCode))
            {
                var enrollmentResult = await _enrollmentGate.EvaluateAsync(
                    npi:         providerNpi,
                    taxonomy:    taxonomy ?? string.Empty,
                    stateCode:   stateCode,
                    serviceDate: serviceDate,
                    lob:         MapToPaEnrollmentLob(lob),
                    ct:          cts.Token);

                if (!enrollmentResult.Passed)
                {
                    _logger.LogInformation(
                        "PA denied by enrollment gate: NPI={Npi} State={State} " +
                        "Code={Code} Reason={Reason}",
                        providerNpi, stateCode,
                        enrollmentResult.DenialCode, enrollmentResult.DenialReason);

                    return new PasDecisionResult
                    {
                        HasDecision      = true,
                        Decision         = "denied",
                        DenialReasonCode = enrollmentResult.DenialCode,
                        DenialReason     = enrollmentResult.DenialReason,
                        RuleName         = "EnrollmentGate",
                        ElapsedMs        = sw.ElapsedMilliseconds
                    };
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            // ── Rule 1: Deny — non-covered service (unchanged) ────────────
            if (_config.AutoDenyServiceTypes.Count > 0)
            {
                var denied = procedureCodes.FirstOrDefault(
                    c => _config.AutoDenyServiceTypes.Contains(c));
                if (denied != null)
                {
                    return new PasDecisionResult
                    {
                        HasDecision      = true,
                        Decision         = "denied",
                        DenialReasonCode = "NOT_COVERED",
                        DenialReason     = $"Service {denied} is not a covered benefit",
                        RuleName         = "AutoDenyList",
                        ElapsedMs        = sw.ElapsedMilliseconds
                    };
                }
            }

            cts.Token.ThrowIfCancellationRequested();

            // ── Rule 2: Auto-approve — service type allowlist (unchanged) ─
            if (_config.AutoApproveServiceTypes.Count > 0 &&
                procedureCodes.Any(c => _config.AutoApproveServiceTypes.Contains(c)))
            {
                return Approve(sw, "AutoApproveList");
            }

            cts.Token.ThrowIfCancellationRequested();

            // ── Rule 3: Auto-approve — gold-card provider (unchanged) ─────
            if (!string.IsNullOrEmpty(providerNpi))
            {
                var isGoldCard = await CheckGoldCardAsync(providerNpi, cts.Token);
                if (isGoldCard)
                    return Approve(sw, "GoldCardProvider");
            }

            cts.Token.ThrowIfCancellationRequested();

            // ── Rule 4: Auto-approve — dollar threshold (unchanged) ───────
            var estimatedCost = ExtractEstimatedCost(claim);
            if (estimatedCost.HasValue && estimatedCost.Value < _config.DollarThreshold)
                return Approve(sw, "DollarThreshold");

            cts.Token.ThrowIfCancellationRequested();

            // ── Rule 5: State-specific PA rule engine ─────────────────────
            // Replaces the previous default PEND.
            // Evaluates clinical criteria, quantity limits, regulatory exemptions
            // and diagnosis requirements by (StateCode, LOB, Program).
            var ruleContext = new PaRuleContext
            {
                TenantId              = ExtractTenantId(context),
                StateCode             = stateCode ?? "TX",
                Lob                   = lob,
                Program               = ExtractProgram(claim),
                RequestingProviderNpi = providerNpi ?? string.Empty,
                ServicingProviderNpi  = ExtractServicingNpi(claim) ?? providerNpi ?? string.Empty,
                ServicingProviderTaxonomy = taxonomy,
                MemberId              = ExtractMemberId(claim),
                ServiceDate           = serviceDate,
                ProcedureCodes        = procedureCodes,
                DiagnosisCodes        = ExtractDiagnosisCodes(claim),
                PlaceOfServiceCode    = ExtractPlaceOfService(claim),
                EstimatedCost         = estimatedCost ?? 0m,
                MemberDateOfBirth     = ExtractMemberDob(claim)
                // ProviderHistory and MemberHistory pre-fetched inside the engine
            };

            var ruleDecision = await _ruleEngine.EvaluateAsync(ruleContext, cts.Token);

            _logger.LogInformation(
                "PA rule engine: {Outcome} via {Rule} for {State}/{Lob}/{Program} in {Ms}ms",
                ruleDecision.Outcome, ruleDecision.FiringRuleName,
                ruleContext.StateCode, ruleContext.Lob, ruleContext.Program ?? "any",
                ruleDecision.ElapsedMs);

            return ruleDecision.Outcome switch
            {
                PaDecisionOutcome.Approve => Approve(sw, ruleDecision.FiringRuleName),
                PaDecisionOutcome.Deny    => new PasDecisionResult
                {
                    HasDecision      = true,
                    Decision         = "denied",
                    DenialReasonCode = ruleDecision.DenialCode,
                    DenialReason     = ruleDecision.DenialReason,
                    RuleName         = ruleDecision.FiringRuleName,
                    ElapsedMs        = sw.ElapsedMilliseconds
                },
                _ => Pend(sw, ruleDecision.FiringRuleName)  // Pend = clinical review queue
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "PAS auto-adjudication timed out after {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return Pend(sw, "TimeBudgetExceeded");
        }
    }

    // ── Existing helpers (unchanged) ──────────────────────────────

    private async Task<bool> CheckGoldCardAsync(string providerNpi, CancellationToken ct)
    {
        try
        {
            var response = await _authServiceClient.GetAsync(
                $"api/authorizations/summary?providerNPI={providerNpi}", ct);
            if (!response.IsSuccessStatusCode) return false;
            var summary = await response.Content.ReadFromJsonAsync<AuthSummaryResponse>(ct);
            if (summary == null || summary.TotalAuthorizations < 20) return false;
            return (double)summary.ApprovalRate / 100.0 >= _config.GoldCardThreshold;
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
        if (claim.Item == null) return codes;
        foreach (var item in claim.Item)
            if (item.ProductOrService?.Coding != null)
                codes.AddRange(item.ProductOrService.Coding
                    .Where(c => !string.IsNullOrEmpty(c.Code))
                    .Select(c => c.Code!));
        return codes;
    }

    private static string? ExtractProviderNpi(Claim claim) =>
        claim.Provider?.Identifier?.Value;

    private static string? ExtractServicingNpi(Claim claim) =>
        // FHIR R4: servicing provider referenced via CareTeam, not Item.Provider
        claim.CareTeam?.FirstOrDefault(ct => ct.Role?.Coding?.Any(
            c => c.Code == "rendering") == true)?.Provider?.Identifier?.Value;

    private static string? ExtractTaxonomy(Claim claim) =>
        // FHIR R4: Provider taxonomy carried in Claim.Provider qualification extension
        // or CareTeam role. ResourceReference.Type is a string, not CodeableConcept.
        claim.CareTeam?.FirstOrDefault()?.Qualification?.Coding?.FirstOrDefault()?.Code;

    private static string? ExtractMemberId(Claim claim) =>
        claim.Patient?.Identifier?.Value ?? string.Empty;

    private static DateOnly ExtractServiceDate(Claim claim)
    {
        if (claim.Item?.FirstOrDefault()?.Serviced is Period p && p.StartElement != null)
            return DateOnly.Parse(p.Start);
        if (claim.Item?.FirstOrDefault()?.Serviced is FhirDateTime dt)
            return DateOnly.Parse(dt.Value);
        return DateOnly.FromDateTime(DateTime.UtcNow);
    }

    private static DateOnly? ExtractMemberDob(Claim claim)
    {
        // Patient DOB is in the Bundle context — best-effort extraction
        var dob = claim.Patient?.Extension
            .FirstOrDefault(e => e.Url.Contains("birthDate"))?.Value as FhirDateTime;
        return dob != null ? DateOnly.Parse(dob.Value) : null;
    }

    private static string? ExtractStateCode(Claim claim) =>
        // CMS-1500/278: servicing facility state from Item.LocationCodeableConcept
        // or from the CRD CoverageRequirements extension
        claim.Item?.FirstOrDefault()?.Location switch
        {
            Address a => a.State,
            _ => null
        };

    private static PaLineOfBusiness ExtractLineOfBusiness(Claim claim)
    {
        // Map FHIR Claim.insurance.coverage plan type to PaLineOfBusiness
        var planType = claim.Insurance?.FirstOrDefault()?.Coverage?.Identifier?.Type?.Text;
        return planType?.ToUpperInvariant() switch
        {
            "MEDICAID" or "STAR" or "STARPLUS" or "STARKIDS" => PaLineOfBusiness.Medicaid,
            "EXCHANGE" or "QHP" or "MARKETPLACE"             => PaLineOfBusiness.Exchange,
            "MEDICARE" or "MA"                               => PaLineOfBusiness.Medicare,
            _                                                => PaLineOfBusiness.Medicaid // default for Texas Medicaid MCO tenants
        };
    }

    private static string? ExtractProgram(Claim claim)
    {
        // STAR, STARPlus, STARKids — carried in CRD extension or plan name
        var planName = claim.Insurance?.FirstOrDefault()?.Coverage?.Identifier?.Type?.Text
            ?.ToUpperInvariant();
        return planName switch
        {
            "STARPLUS" or "STAR+" => "STARPlus",
            "STARKIDS"            => "STARKids",
            "STAR"                => "STAR",
            _                     => null
        };
    }

    private static IReadOnlyList<string> ExtractDiagnosisCodes(Claim claim) =>
        claim.Diagnosis?
            .Select(d => (d.Diagnosis as CodeableConcept)?.Coding?.FirstOrDefault()?.Code)
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>()
            .ToList() ?? [];

    private static string? ExtractPlaceOfService(Claim claim) =>
        (claim.Item?.FirstOrDefault()?.Location as CodeableConcept)
            ?.Coding?.FirstOrDefault()?.Code;

    private static decimal? ExtractEstimatedCost(Claim claim)
    {
        if (claim.Total?.Value != null) return claim.Total.Value;
        if (claim.Item == null) return null;
        decimal total = 0;
        bool hasAny   = false;
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

    private static string ExtractTenantId(Bundle context) =>
        // The CRD CoverageRequirements bundle carries the tenant context header
        context.Meta?.Tag?.FirstOrDefault(t => t.System == "cho:tenant")?.Code ?? string.Empty;

    /// <summary>
    /// Map FHIR-layer PaLineOfBusiness to the enrollment service's LineOfBusiness flags.
    /// The enrollment service uses [Flags] enum; PaLineOfBusiness is a plain enum.
    /// </summary>
    private static CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness
        MapToPaEnrollmentLob(PaLineOfBusiness lob) => lob switch
    {
        PaLineOfBusiness.Medicaid  => CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness.Medicaid,
        PaLineOfBusiness.Exchange  => CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness.Marketplace,
        PaLineOfBusiness.Medicare  => CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness.Medicare,
        _                          => CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness.None
    };

    // ── Decision factories (unchanged) ────────────────────────────

    private static PasDecisionResult Approve(Stopwatch sw, string ruleName) => new()
    {
        HasDecision         = true,
        Decision            = "approved",
        AuthorizationNumber = $"PAS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
        EffectiveFrom       = DateTime.UtcNow.Date,
        EffectiveTo         = DateTime.UtcNow.Date.AddYears(1),
        RuleName            = ruleName,
        ElapsedMs           = sw.ElapsedMilliseconds
    };

    private static PasDecisionResult Pend(Stopwatch sw, string ruleName) => new()
    {
        HasDecision = false,
        RuleName    = ruleName,
        ElapsedMs   = sw.ElapsedMilliseconds
    };

    private sealed class AuthSummaryResponse
    {
        public int TotalAuthorizations { get; set; }
        public decimal ApprovalRate    { get; set; }
    }
}
