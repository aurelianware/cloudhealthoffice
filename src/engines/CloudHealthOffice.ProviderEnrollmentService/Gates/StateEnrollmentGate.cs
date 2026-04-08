using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Aggregator;
using CloudHealthOffice.ProviderEnrollmentService.Models;
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
public sealed class StateEnrollmentGate : IEnrollmentDecisionGate
{
    private readonly MultiStateEnrollmentAggregator _aggregator;
    private readonly ILogger<StateEnrollmentGate> _logger;

    public StateEnrollmentGate(
        MultiStateEnrollmentAggregator aggregator,
        ILogger<StateEnrollmentGate> logger)
    {
        _aggregator = aggregator;
        _logger     = logger;
    }

    public async Task<GateResult> EvaluateAsync(
        string npi,
        string taxonomy,
        string stateCode,
        DateOnly serviceDate,
        LineOfBusiness lob,
        CancellationToken ct = default)
    {
        _logger.LogDebug(
            "Enrollment gate: NPI={Npi} State={State} Taxonomy={Taxonomy} LOB={Lob} Date={Date}",
            npi, stateCode, taxonomy, lob, serviceDate);

        var record = await _aggregator.GetEnrollmentForStateAsync(npi, stateCode, ct);

        // ── Gate 1: Provider must be known to the state system ────
        if (record is null)
        {
            return GateResult.Deny("PEMS-001",
                $"NPI {npi} was not found in {stateCode} Medicaid enrollment. " +
                "Provider must be enrolled before prior authorization can be approved.");
        }

        // ── Gate 2: Enrollment must be Active ─────────────────────
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

        // ── Gate 3: Enrollment must be effective on service date ──
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

        // ── Gate 4: Requested taxonomy must be enrolled ───────────
        if (record.EnrolledTaxonomies.Count > 0 &&
            !string.IsNullOrEmpty(taxonomy) &&
            !record.EnrolledTaxonomies.Contains(taxonomy, StringComparer.OrdinalIgnoreCase))
        {
            return GateResult.Deny("PEMS-002",
                $"Taxonomy {taxonomy} is not enrolled under NPI {npi} in {stateCode} {record.SourceSystem}. " +
                $"Enrolled taxonomies: {string.Join(", ", record.EnrolledTaxonomies)}.");
        }

        // ── Gate 5: Requested LOB must be supported ───────────────
        if (lob != LineOfBusiness.None && !record.SupportedLobs.HasFlag(lob))
        {
            return GateResult.Deny("PEMS-005",
                $"NPI {npi} is not enrolled for {lob} in {stateCode} {record.SourceSystem}. " +
                $"Enrolled programs: {record.SupportedLobs}.");
        }

        _logger.LogDebug("Enrollment gate passed for NPI={Npi} State={State}", npi, stateCode);
        return GateResult.Pass();
    }
}
