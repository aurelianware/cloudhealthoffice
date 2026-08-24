using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Deterministic 277CA → transmission matching. Never fuzzy-matches and never
/// applies an acknowledgment when more than one transmission qualifies.
/// </summary>
internal static class ClaimAcknowledgmentMatcher
{
    public sealed record Result(
        ClaimTransmissionRecord? Transmission,
        bool Ambiguous,
        string? Reason);

    public static async Task<Result> MatchAsync(
        GatewayClaimAcknowledgment acknowledgment,
        IClaimTransmissionStore store,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(acknowledgment.TransmissionId))
        {
            var byId = await store.GetByIdAsync(acknowledgment.TransmissionId, ct).ConfigureAwait(false);
            return byId is null
                ? new Result(null, false, "explicit-transmission-not-found")
                : new Result(byId, false, "transmission-id");
        }

        var gateway = acknowledgment.Gateway;
        var submissionCandidates = DistinctIdentifiers(
            acknowledgment.OriginalSubmissionId,
            acknowledgment.ClaimLevelResults.Select(r => r.OriginalSubmissionId));

        Result? submissionMatch = null;
        foreach (var id in submissionCandidates)
        {
            var bySubmission = await store.FindBySubmissionIdAsync(gateway, id, ct).ConfigureAwait(false);
            var byExternal = await store.FindByExternalTransactionIdAsync(gateway, id, ct).ConfigureAwait(false);
            var combined = bySubmission.Concat(byExternal).ToList();
            var decided = Decide(combined, "submission-id");
            if (decided.Ambiguous)
            {
                return decided;
            }

            if (decided.Transmission is not null)
            {
                if (submissionMatch?.Transmission is { } existing &&
                    existing.TransmissionId != decided.Transmission.TransmissionId)
                {
                    return new Result(null, true, "ambiguous-submission-id");
                }

                submissionMatch = decided;
            }
        }

        if (submissionMatch?.Transmission is not null)
        {
            return submissionMatch;
        }

        var pcnCandidates = DistinctIdentifiers(
            acknowledgment.PatientControlNumber,
            acknowledgment.ClaimLevelResults.Select(r => r.PatientControlNumber));

        Result? pcnMatch = null;
        foreach (var pcn in pcnCandidates)
        {
            var found = await store.FindByPatientControlNumberAsync(gateway, pcn, ct).ConfigureAwait(false);
            var decided = Decide(found, "patient-control-number");
            if (decided.Ambiguous)
            {
                return decided;
            }

            if (decided.Transmission is not null)
            {
                if (pcnMatch?.Transmission is { } existing &&
                    existing.TransmissionId != decided.Transmission.TransmissionId)
                {
                    return new Result(null, true, "ambiguous-patient-control-number");
                }

                pcnMatch = decided;
            }
        }

        if (pcnMatch?.Transmission is not null)
        {
            return pcnMatch;
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

    private static IEnumerable<string> DistinctIdentifiers(
        string? primary, IEnumerable<string?> extras)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(primary))
        {
            set.Add(primary.Trim());
        }

        foreach (var extra in extras)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                set.Add(extra.Trim());
            }
        }

        return set;
    }
}
