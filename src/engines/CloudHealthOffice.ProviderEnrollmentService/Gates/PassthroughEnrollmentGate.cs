using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;

namespace CloudHealthOffice.ProviderEnrollmentService.Gates;

/// <summary>
/// No-op gate — always passes. Used as a fallback when the full
/// ProviderEnrollmentService is not configured (e.g. test environments,
/// non-Medicaid deployments). Registered by TryAddScoped so it is only
/// used if no other IEnrollmentDecisionGate has been registered.
/// </summary>
public sealed class PassthroughEnrollmentGate : IEnrollmentDecisionGate
{
    public Task<GateResult> EvaluateAsync(
        string npi,
        string taxonomy,
        string stateCode,
        DateOnly serviceDate,
        LineOfBusiness lob,
        CancellationToken ct = default)
        => Task.FromResult(GateResult.Pass());
}
