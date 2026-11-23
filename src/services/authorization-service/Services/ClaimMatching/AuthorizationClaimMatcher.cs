using AuthorizationService.Backends;
using AuthorizationService.Models;

namespace AuthorizationService.Services.ClaimMatching;

/// <summary>
/// One approved authorization that covers a claim service the claim did not
/// name an authorization number for (<c>GET api/authorizations/match</c>).
/// </summary>
public sealed class AuthorizationMatchCandidate
{
    public string AuthorizationNumber { get; init; } = string.Empty;

    /// <summary>The authorization's service line that covers the procedure; null when the authorization has no service lines (an admission or service-type approval).</summary>
    public int? LineNumber { get; init; }

    public AuthorizationStatus Status { get; init; }
    public DateTime ApprovedServiceDateFrom { get; init; }
    public DateTime? ApprovedServiceDateTo { get; init; }

    /// <summary>The medical policy or clinical criteria the decision applied (<see cref="Authorization.CriteriaReference"/>).</summary>
    public string? CriteriaReference { get; init; }

    /// <summary>What the match rests on: "member", "service date", "procedure", "provider".</summary>
    public IReadOnlyList<string> MatchedOn { get; init; } = [];
}

/// <summary>Body of <c>GET api/authorizations/match</c>.</summary>
public sealed class AuthorizationMatchResponse
{
    /// <summary>Every approved authorization that covers the service; empty when none does. Ordered most specific first.</summary>
    public IReadOnlyList<AuthorizationMatchCandidate> Candidates { get; init; } = [];
}

/// <summary>
/// Finds the plan's approved authorizations that cover a claim service when the
/// claim carries no authorization number: the same member, an approved period
/// that includes the date of service, a service line approving the procedure
/// (when the authorization lists services) and the provider (when the
/// authorization names one). Cores such as QNXT and Facets attach
/// authorizations to claims this way, so a shadow run that only looked up the
/// number on the claim would report authorization denials the core never made.
/// Pure: the caller loads the member's authorizations.
/// </summary>
public static class AuthorizationClaimMatcher
{
    public static IReadOnlyList<AuthorizationMatchCandidate> Match(
        IEnumerable<Authorization> authorizations, string memberId, DateTime serviceDate,
        string? procedureCode, string? providerNpi, DateTime todayUtc)
    {
        ArgumentNullException.ThrowIfNull(authorizations);
        var member = memberId.Trim();
        var date = serviceDate.Date;
        var code = string.IsNullOrWhiteSpace(procedureCode) ? null : procedureCode.Trim();
        var npi = string.IsNullOrWhiteSpace(providerNpi) ? null : providerNpi.Trim();

        var candidates = new List<(AuthorizationMatchCandidate Candidate, int Specificity)>();
        foreach (var auth in authorizations)
        {
            if (!string.Equals(auth.MemberId?.Trim(), member, StringComparison.Ordinal)) continue;
            if (!IsApproval(auth, date, todayUtc)) continue;

            var from = (auth.ApprovedServiceDateFrom ?? auth.RequestedServiceDateFrom).Date;
            var to = (auth.ApprovedServiceDateTo ?? auth.RequestedServiceDateTo)?.Date;
            if (date < from || (to is { } end && date > end)) continue;
            if (auth.ExpirationDate is { } expires && date > expires.Date) continue;

            var matchedOn = new List<string> { "member", "service date" };
            var specificity = 0;

            int? lineNumber = null;
            var services = auth.RequestedServices ?? [];
            if (code is not null && services.Count > 0)
            {
                var approved = services
                    .Select((s, i) => (Service: s, Number: s.LineNumber ?? i + 1))
                    .Where(s => string.Equals(s.Service.ProcedureCode?.Trim(), code, StringComparison.OrdinalIgnoreCase)
                                // A line with no decision of its own (a UM export without
                                // line status) carries the authorization's approval.
                                && (s.Service.ServiceStatus is null or "A1" or "A2"))
                    .ToList();
                if (approved.Count == 0) continue;
                lineNumber = approved[0].Number;
                matchedOn.Add("procedure");
                specificity += 2;
            }

            if (npi is not null)
            {
                var named = new[] { auth.ServicingProviderNPI, auth.RequestingProviderNPI, auth.FacilityNPI }
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!.Trim())
                    .ToList();
                if (named.Count > 0)
                {
                    if (!named.Contains(npi, StringComparer.OrdinalIgnoreCase)) continue;
                    matchedOn.Add("provider");
                    specificity += 1;
                }
            }

            candidates.Add((new AuthorizationMatchCandidate
            {
                AuthorizationNumber = auth.AuthorizationNumber,
                LineNumber = lineNumber,
                Status = auth.Status,
                ApprovedServiceDateFrom = from,
                ApprovedServiceDateTo = to,
                CriteriaReference = string.IsNullOrWhiteSpace(auth.CriteriaReference) ? null : auth.CriteriaReference.Trim(),
                MatchedOn = matchedOn,
            }, specificity));
        }

        return candidates
            .OrderByDescending(c => c.Specificity)
            .ThenByDescending(c => c.Candidate.ApprovedServiceDateFrom)
            .ThenBy(c => c.Candidate.AuthorizationNumber, StringComparer.Ordinal)
            .Select(c => c.Candidate)
            .ToList();
    }

    /// <summary>
    /// Approved or modified, or an approval that has since expired when the
    /// service was before today (expiry ends new coverage, not the right to bill
    /// a service the approval covered): the rule the validate endpoint applies.
    /// </summary>
    private static bool IsApproval(Authorization auth, DateTime serviceDate, DateTime todayUtc)
    {
        if (auth.Status is AuthorizationStatus.Approved or AuthorizationStatus.Modified) return true;
        return auth.Status == AuthorizationStatus.Expired
               && serviceDate < todayUtc.Date
               && AuthorizationLifecyclePolicy.ClinicalDecisionStatus(auth) is AuthorizationStatus.Approved or AuthorizationStatus.Modified;
    }
}
