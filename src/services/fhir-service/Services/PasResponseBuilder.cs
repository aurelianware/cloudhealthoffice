using Hl7.Fhir.Model;
using FhirService.Models;

namespace FhirService.Services;

/// <summary>
/// Builds FHIR R4 Bundles conforming to the Da Vinci PAS ClaimResponse profile.
/// </summary>
public class PasResponseBuilder
{
    private const string PasClaimResponseProfile =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse";

    private const string ReviewActionExtensionUrl =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction";

    private const string ReviewActionCodeUrl =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode";

    /// <summary>
    /// Builds an approved ClaimResponse bundle.
    /// </summary>
    public Bundle BuildApprovedResponse(Claim claim, PasDecisionResult decision)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Complete;
        claimResponse.Disposition = "approved";
        claimResponse.PreAuthRef = decision.AuthorizationNumber;

        if (decision.EffectiveFrom.HasValue)
        {
            claimResponse.PreAuthPeriod = new Period
            {
                Start = decision.EffectiveFrom.Value.ToString("yyyy-MM-dd"),
                End = decision.EffectiveTo?.ToString("yyyy-MM-dd"),
            };
        }

        return WrapInBundle(claimResponse);
    }

    /// <summary>
    /// Builds a denied ClaimResponse bundle.
    /// </summary>
    public Bundle BuildDeniedResponse(Claim claim, PasDecisionResult decision)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Complete;
        claimResponse.Disposition = "denied";

        claimResponse.Error = new List<ClaimResponse.ErrorComponent>
        {
            new()
            {
                Code = new CodeableConcept("http://terminology.hl7.org/CodeSystem/adjudication-error",
                    decision.DenialReasonCode ?? "denied",
                    decision.DenialReason ?? "Request denied"),
            }
        };

        return WrapInBundle(claimResponse);
    }

    /// <summary>
    /// Builds a pended ClaimResponse bundle with X12 review action code A4.
    /// </summary>
    public Bundle BuildPendedResponse(Claim claim)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Queued;
        claimResponse.Disposition = "pended";

        // Add Da Vinci PAS reviewAction extension with A4 (pended) code
        claimResponse.Extension.Add(new Extension
        {
            Url = ReviewActionExtensionUrl,
            Extension = new List<Extension>
            {
                new()
                {
                    Url = ReviewActionCodeUrl,
                    Value = new Coding(
                        "https://codesystem.x12.org/005010/306",
                        "A4",
                        "Pending"),
                }
            }
        });

        return WrapInBundle(claimResponse);
    }

    private static ClaimResponse BuildBase(Claim claim)
    {
        return new ClaimResponse
        {
            Id = Guid.NewGuid().ToString(),
            Meta = new Meta
            {
                Profile = new[] { PasClaimResponseProfile },
                LastUpdated = DateTimeOffset.UtcNow,
            },
            Status = FinancialResourceStatusCodes.Active,
            Type = claim.Type ?? new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = claim.Patient,
            Created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Insurer = claim.Insurer ?? new ResourceReference { Display = "CHO Payer" },
            Request = new ResourceReference($"Claim/{claim.Id}"),
        };
    }

    private static Bundle WrapInBundle(ClaimResponse claimResponse)
    {
        return new Bundle
        {
            Id = Guid.NewGuid().ToString(),
            Type = Bundle.BundleType.Collection,
            Timestamp = DateTimeOffset.UtcNow,
            Entry = new List<Bundle.EntryComponent>
            {
                new()
                {
                    FullUrl = $"urn:uuid:{claimResponse.Id}",
                    Resource = claimResponse,
                }
            },
        };
    }
}
