using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>Which PAS element carried a payer-issued tracking identity.</summary>
public enum PasAuthorizationIdentityKind
{
    /// <summary>
    /// The authorization number, issued inside the <c>reviewAction</c> extension
    /// on an adjudication as the <c>number</c> sub-extension. A payer issues one
    /// when it certifies an item (A1) or certifies it with modifications (A6).
    /// </summary>
    AuthorizationNumber,

    /// <summary>
    /// The administration reference number, on the response item. Issued where an
    /// authorization number is not — a pended item (A4) has no authorization yet
    /// but still needs a handle the provider can inquire on.
    /// </summary>
    AdministrationReferenceNumber,
}

/// <summary>
/// One payer-issued identity for an authorization, and where it came from.
///
/// This is the thing a PAS inquiry correlates on. It is issued by the payer, not
/// chosen by the submitter: CHO has no say in its value and must not mint one of
/// its own, or the inquiry would prove only that CHO can echo a string it
/// invented.
/// </summary>
/// <param name="Value">The identifier exactly as the payer issued it.</param>
/// <param name="Kind">Which PAS element carried it.</param>
/// <param name="ItemSequence">The response item it belongs to.</param>
/// <param name="SourcePath">
/// The FHIRPath-ish location it was read from, recorded in evidence so a reader
/// can see WHERE the correlation key lived without being handed the raw body.
/// </param>
public sealed record PasAuthorizationIdentity(
    string Value,
    PasAuthorizationIdentityKind Kind,
    int? ItemSequence,
    string SourcePath)
{
    /// <summary>PHI-free description. The value is a synthetic payer-issued handle, never member data.</summary>
    public string SafeSummary() => $"{Kind} '{Value}' from {SourcePath}";
}

/// <summary>
/// The outcome of choosing which payer-issued identity an inquiry should quote.
///
/// A selection either yields exactly one identity or explains why it could not,
/// because both failure modes are real and mean different things: a payer that
/// issued none has not given the provider anything to inquire on, and a payer
/// that issued several conflicting ones has not said which authorization a
/// single inquiry would be about.
/// </summary>
public sealed record PasAuthorizationIdentitySelection(
    PasAuthorizationIdentity? Selected,
    IReadOnlyList<PasAuthorizationIdentity> Candidates,
    string? Problem)
{
    public bool IsResolved => Selected is not null && Problem is null;
}

/// <summary>
/// Extracts the durable identity a payer issued for an authorization, out of the
/// ClaimResponse it returned.
///
/// The asymmetry this class exists to bridge: a payer ISSUES the authorization
/// number nested inside <c>reviewAction</c> on an adjudication, and a later
/// inquiry QUOTES it back on <c>Claim.item</c> under a different extension URL.
/// Both placements are what the PAS IG defines — <c>extension-reviewAction</c>
/// slices a sub-extension whose url is the bare token <c>number</c>, while
/// <c>extension-authorizationNumber</c> is contextualized to <c>Claim.item</c>
/// and <c>ClaimResponse.item</c> — so this is IG-conformant rather than an
/// upstream quirk, and a reader that expects one shape in both places finds
/// nothing.
///
/// Nothing here defaults, invents or normalizes a value. An identity absent from
/// the response is absent, and that is reported.
/// </summary>
public static class PasAuthorizationIdentityExtractor
{
    /// <summary>
    /// Every payer-issued identity on the ClaimResponse, in document order:
    /// authorization numbers first, then administration reference numbers.
    /// </summary>
    public static IReadOnlyList<PasAuthorizationIdentity> From(ClaimResponse? claimResponse)
    {
        if (claimResponse is null)
        {
            return Array.Empty<PasAuthorizationIdentity>();
        }

        var identities = new List<PasAuthorizationIdentity>();

        foreach (var item in claimResponse.Item)
        {
            foreach (var adjudication in item.Adjudication)
            {
                foreach (var reviewAction in adjudication.Extension
                             .Where(extension => extension.Url == PasProtocol.ReviewActionExtension))
                {
                    var number = reviewAction.Extension
                        .Where(extension => extension.Url == PasProtocol.ReviewActionNumberSubExtension)
                        .Select(extension => extension.Value)
                        .OfType<FhirString>()
                        .Select(value => value.Value)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                    if (number is not null)
                    {
                        identities.Add(new PasAuthorizationIdentity(
                            number,
                            PasAuthorizationIdentityKind.AuthorizationNumber,
                            item.ItemSequence,
                            $"ClaimResponse.item[{item.ItemSequence}].adjudication.extension"
                            + "[reviewAction].extension[number].valueString"));
                    }
                }
            }
        }

        foreach (var item in claimResponse.Item)
        {
            var adminRef = item.Extension
                .Where(extension => extension.Url == PasProtocol.AdministrationReferenceNumberExtension)
                .Select(extension => extension.Value)
                .OfType<FhirString>()
                .Select(value => value.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (adminRef is not null)
            {
                identities.Add(new PasAuthorizationIdentity(
                    adminRef,
                    PasAuthorizationIdentityKind.AdministrationReferenceNumber,
                    item.ItemSequence,
                    $"ClaimResponse.item[{item.ItemSequence}].extension"
                    + "[administrationReferenceNumber].valueString"));
            }
        }

        return identities;
    }

    /// <summary>
    /// Chooses the single identity a subsequent inquiry should quote.
    ///
    /// An authorization number wins over an administration reference number when
    /// both are present: the authorization number names a decided authorization,
    /// while the administration reference number is the handle for one the payer
    /// is still working on. A payer that issued both for the same request has
    /// given a decided answer, and the decided answer is the one to inquire
    /// about.
    ///
    /// Duplicates of the SAME value across items are not a conflict — a
    /// multi-item request certified as a whole legitimately repeats one
    /// authorization number. Two DIFFERENT values of the winning kind are a
    /// conflict: the response describes more than one authorization and a single
    /// inquiry cannot be about all of them.
    /// </summary>
    public static PasAuthorizationIdentitySelection Select(
        IReadOnlyList<PasAuthorizationIdentity> candidates)
    {
        if (candidates.Count == 0)
        {
            return new PasAuthorizationIdentitySelection(
                null, candidates,
                "the payer issued no authorization number and no administration reference number, "
                + "so it supplied nothing an inquiry could correlate on");
        }

        var preferredKind = candidates.Any(
            identity => identity.Kind == PasAuthorizationIdentityKind.AuthorizationNumber)
            ? PasAuthorizationIdentityKind.AuthorizationNumber
            : PasAuthorizationIdentityKind.AdministrationReferenceNumber;

        var preferred = candidates.Where(identity => identity.Kind == preferredKind).ToList();

        var distinct = preferred
            .Select(identity => identity.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinct.Count > 1)
        {
            return new PasAuthorizationIdentitySelection(
                null, candidates,
                $"the payer issued {distinct.Count} different {preferredKind} values "
                + $"([{string.Join(", ", distinct)}]), so the response describes more than one "
                + "authorization and a single inquiry cannot name all of them");
        }

        return new PasAuthorizationIdentitySelection(preferred[0], candidates, null);
    }

    /// <summary>Convenience over <see cref="From"/> plus <see cref="Select"/>.</summary>
    public static PasAuthorizationIdentitySelection SelectFrom(ClaimResponse? claimResponse) =>
        Select(From(claimResponse));

    /// <summary>PHI-free inventory of what the payer issued, for evidence.</summary>
    public static string SafeSummary(IReadOnlyList<PasAuthorizationIdentity> identities) =>
        identities.Count == 0
            ? "(no payer-issued authorization identity)"
            : string.Join("; ", identities.Select(identity => identity.SafeSummary()));
}
