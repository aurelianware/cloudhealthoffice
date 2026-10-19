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
/// The line of business and the providers are the ones the controller mapped
/// from the request and stores with the record (<see cref="PasDecisionBasis"/>);
/// nothing here re-derives or defaults them. The service state is the one the
/// request names (<c>Claim.item.locationAddress.state</c>); a request naming
/// none is never decided by a state-specific rule.
///
///   Rule 0: StateEnrollmentGate        — provider must be enrolled in the state
///                                        program for the request's LOB (Medicaid,
///                                        Medicare, Marketplace); not run for others
///   Rule 1: AutoDenyList               — non-covered service types (config)
///   Rule 2: AutoApproveList            — service type allowlist (config)
///   Rule 3: GoldCard (approval rate)   — existing authorization-service check
///   Rule 4: DollarThreshold            — cost-based auto-approve (config)
///   Rule 5: PriorAuthRuleEngine        — state-specific clinical/regulatory rules;
///                                        PEND (StateUndetermined) when no state
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

    /// <summary>The rule name the NoOp rule engine reports; it evaluates nothing.</summary>
    public const string NoOpRuleName = "NoOp";

    /// <summary>Recorded when the request names no service state for the state-specific rules.</summary>
    public const string StateUndeterminedPendReason =
        "The service state could not be determined from the request (Claim.item.locationAddress.state), "
        + "so no state-specific rule was applied. Pended for review.";

    public async Task<PasDecisionResult> TryDecideAsync(
        Claim claim,
        Bundle context,
        string tenantId,
        PasDecisionBasis basis,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var stateCode = ExtractStateCode(claim);
        var consumed = new Consumed();
        var result = await DecideAsync(claim, tenantId, basis, stateCode, consumed, timeoutMs, ct);

        // The record states exactly what the deciding path used, and nothing
        // it did not: a LOB only once rules ran on it, a state only when the
        // enrollment gate ran or the state rule engine was reached.
        result.LineOfBusiness = consumed.LineOfBusiness ? basis.LineOfBusiness.ToString() : null;
        result.StateCode = consumed.State ? stateCode : null;
        return result;
    }

    /// <summary>Which of the decision's inputs the deciding path actually read.</summary>
    private sealed class Consumed
    {
        public bool LineOfBusiness { get; set; }
        public bool State { get; set; }
    }

    private async Task<PasDecisionResult> DecideAsync(
        Claim claim,
        string tenantId,
        PasDecisionBasis basis,
        string? stateCode,
        Consumed consumed,
        int timeoutMs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (!_config.Enabled)
            return Pend(sw, "ConfigDisabled");   // nothing evaluated

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        // The mapped values the record stores, never re-read from the Claim.
        var procedureCodes = basis.ProcedureCodes.Select(c => c.Code).ToList();
        var providerNpi    = basis.RequestingProviderNpi;
        var taxonomy       = ExtractTaxonomy(claim);
        var serviceDate    = basis.ServiceDate;
        var lob            = basis.LineOfBusiness;
        var enrollmentLob  = MapToPaEnrollmentLob(lob);

        // From here every path has consulted the line of business (if only
        // to decide that the enrollment gate does not apply to it).
        consumed.LineOfBusiness = true;

        try
        {
            // ── Rule 0: State program enrollment gate ─────────────────────
            // Provider must be actively enrolled in the state program for the
            // request's line of business before any clinical rules are
            // evaluated. Only for the lines of business the enrollment service
            // knows: a Commercial, TRICARE or VA request is never gated on
            // state Medicaid enrollment.
            if (enrollmentLob != CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness.None
                && !string.IsNullOrEmpty(providerNpi) && !string.IsNullOrEmpty(stateCode))
            {
                consumed.State = true;
                var enrollmentResult = await _enrollmentGate.EvaluateAsync(
                    npi:         providerNpi,
                    taxonomy:    taxonomy ?? string.Empty,
                    stateCode:   stateCode,
                    serviceDate: serviceDate,
                    lob:         enrollmentLob,
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
            // Evaluates clinical criteria, quantity limits, regulatory exemptions
            // and diagnosis requirements by (StateCode, LOB, Program). Only on
            // the state the request names: it used to default to TX, so a
            // request naming no state was decided on Texas rules.
            if (string.IsNullOrEmpty(stateCode))
            {
                var undetermined = Pend(sw, "StateUndetermined");
                undetermined.PendReason = StateUndeterminedPendReason;
                return undetermined;
            }

            consumed.State = true;
            var ruleContext = new PaRuleContext
            {
                // The authenticated tenant, passed in by the controller. The
                // request bundle's meta.tag is NOT consulted (D-12): it is
                // caller-controlled and once let tenant A select tenant B's rules.
                TenantId              = tenantId,
                StateCode             = stateCode,
                Lob                   = lob,
                Program               = ExtractProgram(claim),
                RequestingProviderNpi = providerNpi ?? string.Empty,
                ServicingProviderNpi  = basis.ServicingProviderNpi ?? providerNpi ?? string.Empty,
                ServicingProviderTaxonomy = taxonomy,
                MemberId              = basis.MemberId ?? string.Empty,
                ServiceDate           = serviceDate,
                ProcedureCodes        = procedureCodes,
                DiagnosisCodes        = basis.DiagnosisCodes,
                PlaceOfServiceCode    = basis.PlaceOfServiceCode,
                EstimatedCost         = estimatedCost ?? 0m,
                MemberDateOfBirth     = basis.PatientDateOfBirth
                // ProviderHistory and MemberHistory pre-fetched inside the engine
            };

            var ruleDecision = await _ruleEngine.EvaluateAsync(ruleContext, cts.Token);

            // A NoOp engine (a test double; fhir-service never registers one) evaluates nothing:
            // like ConfigDisabled, no basis is claimed for its pend.
            // Recognised by its rule id, the contract (as
            // PaRuleDecisionExtensions.IsPriorAuthRequired does); the name is
            // display text. Recorded under that id, the rule
            // authorization-service binds to a no-evaluation pend.
            if (ruleDecision.FiringRuleId == NoOpRuleName)
            {
                consumed.LineOfBusiness = false;
                consumed.State = false;
                return Pend(sw, NoOpRuleName);
            }

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
            // No rule reached a conclusion: nothing is claimed as its basis.
            consumed.LineOfBusiness = false;
            consumed.State = false;
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

    private static string? ExtractTaxonomy(Claim claim) =>
        // FHIR R4: Provider taxonomy carried in Claim.Provider qualification extension
        // or CareTeam role. ResourceReference.Type is a string, not CodeableConcept.
        claim.CareTeam?.FirstOrDefault()?.Qualification?.Coding?.FirstOrDefault()?.Code;

    /// <summary>
    /// The service state the request names: the first item's
    /// <c>locationAddress.state</c>, as a two-letter USPS code. Null when absent
    /// or not a state code; never defaulted.
    /// </summary>
    private static string? ExtractStateCode(Claim claim)
    {
        var state = (claim.Item?.FirstOrDefault()?.Location as Address)?.State?.Trim().ToUpperInvariant();
        return state is { Length: 2 } && state.All(c => c is >= 'A' and <= 'Z') ? state : null;
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

    // No approved period here: none of these rules decides one, and the
    // controller approves the period the request asked for (D-03). It used to
    // be hard-coded to today..+1 year whatever was requested.
    private static PasDecisionResult Approve(Stopwatch sw, string ruleName) => new()
    {
        HasDecision         = true,
        Decision            = "approved",
        AuthorizationNumber = $"PAS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
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
