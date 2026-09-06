using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A Da Vinci PAS <c>Claim/$inquire</c> response.
///
/// Shaped unlike the submit response on purpose, and that difference is part of
/// what this scenario proves: <c>$submit</c> answers with one response Bundle,
/// while <c>$inquire</c> answers with <c>Parameters</c> carrying zero or more
/// repeating <c>responseBundle</c> parts — an inquiry is a query, and a query may
/// match nothing, one authorization, or several.
///
/// Zero matches is a legitimate answer, not an error. It is exactly the answer a
/// payer must give when the corroborating context does not entitle the caller to
/// the authorization, so the parser reports an empty result plainly rather than
/// treating it as a failure to parse.
///
/// Each match is read through <see cref="PasResponseBundle"/>, so a ClaimResponse
/// means the same thing whichever operation returned it.
/// </summary>
public sealed class PasInquiryResponse
{
    private PasInquiryResponse(Parameters parameters)
    {
        Parameters = parameters;

        Matches = ParametersExtractor
            .Resources(parameters, PasProtocol.ResponseBundleParameter)
            .OfType<Bundle>()
            .Select(bundle => PasResponseBundle.From(bundle))
            .OfType<PasResponseBundle>()
            .ToList();

        Outcomes = ParametersExtractor.Outcomes(parameters);
    }

    /// <summary>The Parameters resource exactly as received.</summary>
    public Parameters Parameters { get; }

    /// <summary>One entry per <c>responseBundle</c> the payer returned.</summary>
    public IReadOnlyList<PasResponseBundle> Matches { get; }

    /// <summary>Any OperationOutcome the payer attached alongside the result.</summary>
    public IReadOnlyList<OperationOutcome> Outcomes { get; }

    /// <summary>True when the payer matched no authorization at all.</summary>
    public bool IsEmpty => Matches.Count == 0;

    /// <summary>Parses an inquiry response. Null when the payload is not Parameters.</summary>
    public static PasInquiryResponse? From(Resource? resource) =>
        resource is Parameters parameters ? new PasInquiryResponse(parameters) : null;

    /// <summary>
    /// The matches carrying a given payer-issued identity.
    ///
    /// This is how an inquiry result is tied back to the authorization a submit
    /// established: by the identity the payer itself issued, compared as a value,
    /// not by position in the result and not by display text.
    /// </summary>
    public IReadOnlyList<PasResponseBundle> MatchesCarrying(string authorizationIdentity) =>
        Matches
            .Where(match => match.AuthorizationIdentities
                .Any(identity => string.Equals(
                    identity.Value, authorizationIdentity, StringComparison.Ordinal)))
            .ToList();

    /// <summary>
    /// Structural problems with the response as a PAS inquiry answer. An empty
    /// result set is not one of them.
    /// </summary>
    public IReadOnlyList<string> ProtocolViolations()
    {
        var problems = new List<string>();

        foreach (var match in Matches)
        {
            var bundle = match.Bundle;
            var profiles = bundle.Meta?.Profile.ToList() ?? [];

            // PAS defines a distinct profile for the inquiry response bundle.
            // Some servers reuse the submit response bundle profile; that is a
            // difference worth reporting rather than a reason to fail, so both
            // are accepted here and the scenario records which was used.
            if (!profiles.Contains(PasProtocol.InquiryResponseBundleProfile)
                && !profiles.Contains(PasProtocol.ResponseBundleProfile))
            {
                problems.Add(
                    "a responseBundle declares neither the PAS inquiry response bundle profile nor the "
                    + $"PAS response bundle profile; declared: [{string.Join(", ", profiles)}]");
            }

            if (bundle.Type != Bundle.BundleType.Collection)
            {
                problems.Add($"a responseBundle has type '{bundle.Type}', not 'collection'");
            }

            if (match.ClaimResponse is null)
            {
                problems.Add("a responseBundle carries no ClaimResponse");
                continue;
            }

            if (match.ClaimResponse.Use != ClaimUseCode.Preauthorization)
            {
                problems.Add(
                    $"a matched ClaimResponse has use '{match.ClaimResponse.Use}', not 'preauthorization'");
            }
        }

        return problems;
    }

    /// <summary>
    /// The declared profile of each returned bundle, so evidence can state which
    /// profile the payer actually used for an inquiry result.
    /// </summary>
    public IReadOnlyList<string> DeclaredBundleProfiles() =>
        Matches
            .SelectMany(match => match.Bundle.Meta?.Profile ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// PHI-free: how many authorizations matched, their decisions and the
    /// identities they carry. No member, provider or clinical detail.
    /// </summary>
    public string SafeSummary()
    {
        if (IsEmpty)
        {
            var issues = Outcomes.SelectMany(ParametersExtractor.SummarizeIssues).ToList();
            return "0 authorizations matched"
                   + (issues.Count == 0 ? "" : $"; payer reported: {string.Join(" | ", issues)}");
        }

        return $"{Matches.Count} authorization(s) matched: "
               + string.Join(" || ", Matches.Select(match => match.SafeSummary()));
    }
}
