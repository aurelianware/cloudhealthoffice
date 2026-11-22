using Hl7.Fhir.Model;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// The X12 306 certification action codes a ClaimResponse states. PAS 2.2.1
/// allows the reviewAction extension only on an adjudication, with its code
/// as a CodeableConcept, so a decision is read from each line's adjudication.
/// </summary>
public static class PasReviewActions
{
    public const string X12CertificationActionSystem = "https://codesystem.x12.org/005010/306";

    private const string ReviewActionUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction";

    public static List<Coding> OnLines(ClaimResponse claimResponse)
        => claimResponse.Item
            .SelectMany(i => i.Adjudication)
            .SelectMany(a => a.Extension)
            .Where(e => e.Url == ReviewActionUrl)
            .SelectMany(e => e.Extension)
            .Select(e => e.Value)
            .OfType<CodeableConcept>()
            .SelectMany(c => c.Coding)
            .ToList();
}
