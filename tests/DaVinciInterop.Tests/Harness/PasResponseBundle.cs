using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A Da Vinci PAS response bundle: the Bundle carrying the ClaimResponse that
/// describes one authorization.
///
/// Both PAS operations produce this shape — <c>$submit</c> returns exactly one,
/// and <c>$inquire</c> returns zero or more of them inside Parameters — so it is
/// the single parsing path for a PAS answer in this harness. A second one would
/// let the two scenarios drift into disagreeing about what the same bytes mean.
///
/// It reports; it does not adjudicate. A denial, a pend and an approval are all
/// well-formed answers, and none of them is a harness failure.
/// </summary>
public sealed class PasResponseBundle
{
    private PasResponseBundle(Bundle bundle)
    {
        Bundle = bundle;
        ClaimResponse = bundle.Entry
            .Select(entry => entry.Resource)
            .OfType<ClaimResponse>()
            .FirstOrDefault();
        ReviewActions = PasReviewStatus.From(ClaimResponse);
        AuthorizationIdentities = PasAuthorizationIdentityExtractor.From(ClaimResponse);
    }

    /// <summary>The response bundle exactly as received.</summary>
    public Bundle Bundle { get; }

    /// <summary>The ClaimResponse the bundle carries, or null when it carries none.</summary>
    public ClaimResponse? ClaimResponse { get; }

    /// <summary>Every adjudicated item's review action.</summary>
    public IReadOnlyList<PasReviewAction> ReviewActions { get; }

    /// <summary>Every payer-issued authorization identity in the response.</summary>
    public IReadOnlyList<PasAuthorizationIdentity> AuthorizationIdentities { get; }

    /// <summary>
    /// The payer's own reference back to the request it decided — the server-side
    /// Claim it stored. Part of the authorization's identity, and asserted across
    /// submit and inquiry.
    /// </summary>
    public string? RequestReference => ClaimResponse?.Request?.Reference;

    /// <summary>The server-assigned logical id of the ClaimResponse, when it has one.</summary>
    public string? ClaimResponseId => ClaimResponse?.Id;

    /// <summary>Parses a PAS response bundle. Null when the payload is not a Bundle.</summary>
    public static PasResponseBundle? From(Resource? resource) =>
        resource is Bundle bundle ? new PasResponseBundle(bundle) : null;

    /// <summary>Chooses the single identity an inquiry should quote back to this payer.</summary>
    public PasAuthorizationIdentitySelection SelectAuthorizationIdentity() =>
        PasAuthorizationIdentityExtractor.Select(AuthorizationIdentities);

    /// <summary>
    /// Structural problems with this bundle as a <c>$submit</c> answer. Empty
    /// means the response is well formed; it says nothing about whether the
    /// decision inside it was favourable.
    ///
    /// Named for the submit operation because it asserts the submit response
    /// bundle profile, which an inquiry result does not carry — an inquiry result
    /// declares the PAS INQUIRY response bundle profile and is checked by
    /// <see cref="PasInquiryResponse.ProtocolViolations"/> instead. Everything
    /// else this type exposes is common to both.
    /// </summary>
    public IReadOnlyList<string> SubmitProtocolViolations()
    {
        var problems = new List<string>();

        if (Bundle.Meta?.Profile.Contains(PasProtocol.ResponseBundleProfile) != true)
        {
            problems.Add(
                $"response bundle does not declare {PasProtocol.ResponseBundleProfile}; declared: "
                + $"[{string.Join(", ", Bundle.Meta?.Profile ?? [])}]");
        }

        if (ClaimResponse is null)
        {
            problems.Add("response bundle carries no ClaimResponse");
            return problems;
        }

        if (ClaimResponse.Meta?.Profile.Contains(PasProtocol.ClaimResponseProfile) != true)
        {
            problems.Add(
                $"ClaimResponse does not declare {PasProtocol.ClaimResponseProfile}; declared: "
                + $"[{string.Join(", ", ClaimResponse.Meta?.Profile ?? [])}]");
        }

        if (ClaimResponse.Use != ClaimUseCode.Preauthorization)
        {
            problems.Add($"ClaimResponse.use is '{ClaimResponse.Use}', not 'preauthorization'");
        }

        if (ClaimResponse.Status != FinancialResourceStatusCodes.Active)
        {
            problems.Add($"ClaimResponse.status is '{ClaimResponse.Status}', not 'active'");
        }

        if (ClaimResponse.Outcome is null)
        {
            problems.Add("ClaimResponse.outcome is absent; PAS requires it");
        }

        if (ReviewActions.Count == 0)
        {
            problems.Add("no adjudicated item carries a review action");
        }

        foreach (var action in ReviewActions.Where(action => action.Code is null))
        {
            problems.Add($"item {action.ItemSequence} was adjudicated with no review action code");
        }

        foreach (var action in ReviewActions.Where(
                     action => action.Code is not null && action.System != PasProtocol.X12ReviewActionSystem))
        {
            problems.Add(
                $"item {action.ItemSequence} review action code '{action.Code}' came from "
                + $"'{action.System ?? "(no system)"}', not {PasProtocol.X12ReviewActionSystem}");
        }

        return problems;
    }

    /// <summary>
    /// A PHI-free description: decisions, identity kinds and the payer's own
    /// references. No member demographics, no clinical content, no service codes.
    /// </summary>
    public string SafeSummary()
    {
        var parts = new List<string>
        {
            $"outcome={ClaimResponse?.Outcome?.ToString() ?? "(none)"}",
            $"decisions: {PasReviewStatus.SafeSummary(ReviewActions)}",
            $"identities: {PasAuthorizationIdentityExtractor.SafeSummary(AuthorizationIdentities)}",
        };

        if (RequestReference is not null)
        {
            parts.Add($"request={RequestReference}");
        }

        return string.Join("; ", parts);
    }
}
