using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Aggregator;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.ProviderEnrollmentService.Gates;

/// <summary>
/// Prior auth enrollment gate — evaluates whether the rendering provider
/// is actively enrolled in the relevant state Medicaid program before
/// the PriorAuthDecisionEngine evaluates its rule sets.
///
/// A failed gate short-circuits the PA decision with a denial code,
/// meaning the 400-600 Texas Medicaid rule set is never evaluated
/// for unenrolled providers — correct and auditable behavior.
///
/// ── Gate evaluation order ─────────────────────────────────────────
///
///   0. Resolve tenant context from HttpContext header "X-Tenant-Id"
///      → no context (batch/test path) → Pass
///   1. Load TenantEnrollmentConfig → null (not configured) → Pass
///   2. ResolveFor(lob) → GateMode
///      → Disabled → Pass
///      → Warn    → evaluate gates, log result, always Pass
///      → Enforce → evaluate gates, deny on failure
///   3. State not in EnabledStateCodes → Pass (state not monitored)
///   4. Provider must be known to state system (PEMS-001)
///   5. Enrollment must be Active (PEMS-003, PEMS-004, PEMS-001)
///   6. Enrollment must be effective on service date (PEMS-001)
///   7. Requested taxonomy must be enrolled (PEMS-002)
///   8. Requested LOB must be supported (PEMS-005)
///
/// Denial codes follow X12 278 AAA segment conventions:
///   PEMS-001  Provider not enrolled in state Medicaid
///   PEMS-002  Provider taxonomy not enrolled under NPI
///   PEMS-003  Provider enrollment suspended / payment hold
///   PEMS-004  Provider revalidation overdue
///   PEMS-005  Provider not enrolled for requested line of business
///
/// Wire-up in PriorAuthDecisionEngine:
///   var gateResult = await _enrollmentGate.EvaluateAsync(npi, taxonomy, "TX", serviceDate, lob);
///   if (!gateResult.Passed) return PaDecision.Deny(gateResult.DenialCode, gateResult.DenialReason);
/// </summary>
public sealed partial class StateEnrollmentGate : IEnrollmentDecisionGate
{
    private readonly MultiStateEnrollmentAggregator _aggregator;
    private readonly ITenantEnrollmentConfigRepository _configRepo;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<StateEnrollmentGate> _logger;

    public StateEnrollmentGate(
        MultiStateEnrollmentAggregator aggregator,
        ITenantEnrollmentConfigRepository configRepo,
        IHttpContextAccessor httpContextAccessor,
        ILogger<StateEnrollmentGate> logger)
    {
        _aggregator          = aggregator;
        _configRepo          = configRepo;
        _httpContextAccessor = httpContextAccessor;
        _logger              = logger;
    }

    public async Task<GateResult> EvaluateAsync(
        string npi,
        string taxonomy,
        string stateCode,
        DateOnly serviceDate,
        LineOfBusiness lob,
        CancellationToken ct = default)
    {
        // Sanitize all user-provided strings up front so every downstream
        // usage (log messages *and* denial-reason strings) is clean.
        // string.Concat breaks the CodeQL taint chain while SanitizeForLog
        // strips CR/LF to prevent log-forging.
        npi       = string.Concat(SanitizeForLog(npi));
        taxonomy  = string.Concat(SanitizeForLog(taxonomy));
        stateCode = string.Concat(SanitizeForLog(stateCode));

        LogEnrollmentGateEntry(_logger, npi, stateCode, taxonomy, lob, serviceDate);

        // ── Step 0: Resolve tenant context ───────────────────────────
        var tenantId = string.Concat(SanitizeForLog(
            _httpContextAccessor.HttpContext?.Items["TenantId"] as string
            ?? _httpContextAccessor.HttpContext?.Request.Headers["X-Tenant-Id"].FirstOrDefault()));

        if (string.IsNullOrEmpty(tenantId))
        {
            _logger.LogDebug("No tenant context — enrollment gate skipped (batch/test path)");
            return GateResult.Pass();
        }

        // ── Step 1: Load tenant config ───────────────────────────────
        var tenantConfig = await _configRepo.GetAsync(tenantId, ct);

        if (tenantConfig is null)
        {
            _logger.LogDebug("No enrollment config for tenant {TenantId} — gate disabled", tenantId);
            return GateResult.Pass();
        }

        // ── Step 2: Resolve gate mode for this LOB ───────────────────
        var resolved = tenantConfig.ResolveFor(lob);

        if (resolved.GateMode == EnrollmentGateMode.Disabled)
        {
            _logger.LogDebug("Enrollment gate disabled for tenant {TenantId} LOB {Lob}", tenantId, lob);
            return GateResult.Pass();
        }

        // ── Step 3: State must be in enabled list ────────────────────
        if (resolved.EnabledStateCodes.Count > 0 &&
            !resolved.EnabledStateCodes.Contains(stateCode, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "State {State} not in enabled list for tenant {TenantId} — gate skipped",
                stateCode, tenantId);
            return GateResult.Pass();
        }

        // ── Steps 4-8: Evaluate enrollment status ────────────────────
        var enrollmentResult = EvaluateEnrollmentStatus(
            await _aggregator.GetEnrollmentForStateAsync(npi, stateCode, ct),
            npi, taxonomy, stateCode, serviceDate, lob);

        // ── Warn mode: log but never deny ────────────────────────────
        if (resolved.GateMode == EnrollmentGateMode.Warn)
        {
            if (!enrollmentResult.Passed)
            {
                _logger.LogWarning(
                    "Enrollment gate warn-only: would deny NPI {Npi} in {State} — " +
                    "Code={Code} Reason={Reason}",
                    npi, stateCode, enrollmentResult.DenialCode, SanitizeForLog(enrollmentResult.DenialReason));
            }

            return GateResult.Pass();
        }

        // ── Enforce mode: return the actual result ───────────────────
        if (enrollmentResult.Passed)
        {
            _logger.LogDebug("Enrollment gate passed for NPI={Npi} State={State}", npi, stateCode);
        }

        return enrollmentResult;
    }

    // ── Pure enrollment-status evaluation ─────────────────────────────

    private static GateResult EvaluateEnrollmentStatus(
        StateEnrollmentRecord? record,
        string npi,
        string taxonomy,
        string stateCode,
        DateOnly serviceDate,
        LineOfBusiness lob)
    {
        // ── Gate 4: Provider must be known to the state system ────
        if (record is null)
        {
            return GateResult.Deny("PEMS-001",
                $"NPI {npi} was not found in {stateCode} Medicaid enrollment. " +
                "Provider must be enrolled before prior authorization can be approved.");
        }

        // ── Gate 5: Enrollment must be Active ─────────────────────
        if (record.Status == EnrollmentStatus.Suspended)
        {
            return GateResult.Deny("PEMS-003",
                $"NPI {npi} has a payment hold or suspension in {stateCode} {record.SourceSystem} " +
                $"effective {record.EffectiveDate:d}. PA cannot be approved while enrollment is suspended.");
        }

        if (record.Status == EnrollmentStatus.RevalidationRequired)
        {
            return GateResult.Deny("PEMS-004",
                $"NPI {npi} revalidation is overdue in {stateCode} {record.SourceSystem}. " +
                "Provider must complete revalidation before PA can be approved.");
        }

        if (record.Status != EnrollmentStatus.Active)
        {
            return GateResult.Deny("PEMS-001",
                $"NPI {npi} enrollment status in {stateCode} {record.SourceSystem} is '{record.Status}'. " +
                "Provider must have Active enrollment for PA approval.");
        }

        // ── Gate 6: Enrollment must be effective on service date ──
        if (record.EffectiveDate > serviceDate)
        {
            return GateResult.Deny("PEMS-001",
                $"NPI {npi} enrollment in {stateCode} is not effective until {record.EffectiveDate:d}. " +
                $"Requested service date {serviceDate:d} precedes enrollment effective date.");
        }

        if (record.TerminationDate.HasValue && record.TerminationDate.Value <= serviceDate)
        {
            return GateResult.Deny("PEMS-001",
                $"NPI {npi} enrollment in {stateCode} terminated on {record.TerminationDate.Value:d}. " +
                $"Requested service date {serviceDate:d} is on or after termination date.");
        }

        // ── Gate 7: Requested taxonomy must be enrolled ───────────
        if (record.EnrolledTaxonomies.Count > 0 &&
            !string.IsNullOrEmpty(taxonomy) &&
            !record.EnrolledTaxonomies.Contains(taxonomy, StringComparer.OrdinalIgnoreCase))
        {
            return GateResult.Deny("PEMS-002",
                $"Taxonomy {taxonomy} is not enrolled under NPI {npi} in {stateCode} {record.SourceSystem}. " +
                $"Enrolled taxonomies: {string.Join(", ", record.EnrolledTaxonomies)}.");
        }

        // ── Gate 8: Requested LOB must be supported ───────────────
        if (lob != LineOfBusiness.None && !record.SupportedLobs.HasFlag(lob))
        {
            return GateResult.Deny("PEMS-005",
                $"NPI {npi} is not enrolled for {lob} in {stateCode} {record.SourceSystem}. " +
                $"Enrolled programs: {record.SupportedLobs}.");
        }

        return GateResult.Pass();
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Enrollment gate: NPI={Npi} State={State} Taxonomy={Taxonomy} LOB={Lob} Date={Date}")]
    private static partial void LogEnrollmentGateEntry(
        ILogger logger, string npi, string state, string taxonomy, LineOfBusiness lob, DateOnly date);
}
