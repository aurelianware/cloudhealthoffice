using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Deterministic 835 → transmission matching. Never fuzzy-matches on name,
/// DOB, provider name, or dollar amount.
/// </summary>
internal static class RemittanceMatcher
{
    public sealed record Result(
        ClaimTransmissionRecord? Transmission,
        bool Ambiguous,
        string? Reason);

    public static async Task<Result> MatchClaimAsync(
        RemittedClaim claim,
        string gatewayName,
        string? explicitTransmissionId,
        IClaimTransmissionStore store,
        CancellationToken ct,
        string? restrictToTenantId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitTransmissionId) ||
            !string.IsNullOrWhiteSpace(claim.TransmissionId))
        {
            var id = FirstNonBlank(explicitTransmissionId, claim.TransmissionId)!;
            var byId = await store.GetByIdAsync(id, ct).ConfigureAwait(false);
            if (byId is not null && !InTenant(byId, restrictToTenantId))
            {
                byId = null;
            }
            return byId is null
                ? new Result(null, false, "explicit-transmission-not-found")
                : new Result(byId, false, "transmission-id");
        }

        if (!string.IsNullOrWhiteSpace(claim.PayerClaimControlNumber))
        {
            var byPayer = await store
                .FindByPayerClaimControlNumberAsync(gatewayName, claim.PayerClaimControlNumber, ct)
                .ConfigureAwait(false);
            var decided = Decide(InTenant(byPayer, restrictToTenantId), "payer-claim-control-number");
            if (decided.Ambiguous || decided.Transmission is not null)
            {
                return decided;
            }
        }

        if (!string.IsNullOrWhiteSpace(claim.PatientControlNumber))
        {
            var byPatient = await store
                .FindByPatientControlNumberAsync(gatewayName, claim.PatientControlNumber, ct)
                .ConfigureAwait(false);
            var decided = Decide(InTenant(byPatient, restrictToTenantId), "patient-control-number");
            if (decided.Ambiguous || decided.Transmission is not null)
            {
                return decided;
            }
        }

        // ClaimId is filled after a successful match from the transmission.
        // Never treat it as a patient-control-number lookup key.
        return new Result(null, false, "no-deterministic-identifier");
    }

    private static Result Decide(IReadOnlyList<ClaimTransmissionRecord> found, string reason)
    {
        if (found.Count == 0)
        {
            return new Result(null, false, reason);
        }

        var distinct = found.Select(r => r.TransmissionId).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
        {
            return new Result(null, true, "ambiguous-" + reason);
        }

        return new Result(found[0], false, reason);
    }

    private static bool InTenant(ClaimTransmissionRecord record, string? tenantId) =>
        tenantId is null || string.Equals(record.TenantId, tenantId, StringComparison.Ordinal);

    private static IReadOnlyList<ClaimTransmissionRecord> InTenant(
        IReadOnlyList<ClaimTransmissionRecord> found, string? tenantId) =>
        tenantId is null ? found : found.Where(r => InTenant(r, tenantId)).ToList();

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
