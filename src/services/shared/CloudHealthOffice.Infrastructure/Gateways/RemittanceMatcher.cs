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
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitTransmissionId) ||
            !string.IsNullOrWhiteSpace(claim.TransmissionId))
        {
            var id = FirstNonBlank(explicitTransmissionId, claim.TransmissionId)!;
            var byId = await store.GetByIdAsync(id, ct).ConfigureAwait(false);
            return byId is null
                ? new Result(null, false, "explicit-transmission-not-found")
                : new Result(byId, false, "transmission-id");
        }

        if (!string.IsNullOrWhiteSpace(claim.PayerClaimControlNumber))
        {
            var byPayer = await store
                .FindByPayerClaimControlNumberAsync(gatewayName, claim.PayerClaimControlNumber, ct)
                .ConfigureAwait(false);
            var decided = Decide(byPayer, "payer-claim-control-number");
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
            var decided = Decide(byPatient, "patient-control-number");
            if (decided.Ambiguous || decided.Transmission is not null)
            {
                return decided;
            }
        }

        if (!string.IsNullOrWhiteSpace(claim.ClaimId))
        {
            var byClaim = await store
                .FindByPatientControlNumberAsync(gatewayName, claim.ClaimId, ct)
                .ConfigureAwait(false);
            var decided = Decide(byClaim, "claim-id");
            if (decided.Ambiguous || decided.Transmission is not null)
            {
                return decided;
            }
        }

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

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
