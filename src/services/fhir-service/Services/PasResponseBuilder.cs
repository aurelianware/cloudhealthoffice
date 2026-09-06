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
        // A denial is still an authorization on record and still inquirable, so
        // it carries the same tracking handle an approval does.
        claimResponse.PreAuthRef = decision.AuthorizationNumber;

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
    public Bundle BuildPendedResponse(Claim claim, string? authorizationNumber = null)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Queued;
        claimResponse.Disposition = "pended";
        // Without this a pended decision had no tracking handle at all, so the
        // submitter could never inquire about the one outcome that most needs
        // following up. PAS-04 depends on it.
        claimResponse.PreAuthRef = authorizationNumber;

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


    // ── PAS $inquire projection ───────────────────────────────────────────────

    /// <summary>
    /// Projects the CURRENT authoritative authorization state onto a PAS
    /// ClaimResponse bundle, for <c>Claim/$inquire</c>.
    ///
    /// The mapping from Cloud Health Office's authorization status onto the
    /// standards representation, which is deterministic and total:
    ///
    /// <code>
    /// Status      X12  ClaimResponse.status  outcome   disposition                       reviewAction
    /// Submitted   —    active                queued    "pending"                         —
    /// InReview    —    active                queued    "pending"                         —
    /// Pended      A4   active                queued    "pended-additional-information"   A4
    /// Approved    A1   active                complete  "approved"                        A1
    /// Modified    A2   active                partial   "modified"                        A2
    /// Denied      A3   active                complete  "denied"                          A3
    /// Expired     —    active                complete  "expired"                         —
    /// Cancelled   —    cancelled             complete  "cancelled"                       —
    /// </code>
    ///
    /// <c>outcome</c> carries the coarse machine answer (still working / decided
    /// / partially decided) and <c>disposition</c> the specific one, so a caller
    /// can tell "pending" from "pended for additional information" from
    /// "approved" from "denied" without CHO inventing states it does not hold.
    /// The A4 reviewAction says a decision is outstanding pending information —
    /// it is NOT a CDex exchange, which CHO does not implement.
    /// </summary>
    public Bundle BuildInquiryResponse(PriorAuthorizationRecord authorization)
    {
        var (status, outcome, disposition, reviewActionCode, reviewActionDisplay) =
            MapStatus(authorization.Status, authorization.ReviewDecision);

        var claimResponse = new ClaimResponse
        {
            Id = Guid.NewGuid().ToString(),
            Meta = new Meta
            {
                Profile = new[] { PasClaimResponseProfile },
                // The record's own last-updated instant, so a caller can see the
                // state is current rather than a submission-time snapshot.
                LastUpdated = new DateTimeOffset(
                    DateTime.SpecifyKind(authorization.LastUpdatedDate, DateTimeKind.Utc)),
            },
            Status = status,
            Type = new CodeableConcept(
                "http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference($"Patient/{StripPatientPrefix(authorization.MemberId)}"),
            Created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Insurer = new ResourceReference { Display = "CHO Payer" },
            Outcome = outcome,
            Disposition = disposition,
            // The tracking handle the caller inquired with, echoed back.
            PreAuthRef = authorization.AuthorizationNumber,
        };

        claimResponse.Identifier.Add(new Identifier
        {
            System = "http://cloudhealthoffice.com/prior-authorization",
            Value = authorization.AuthorizationNumber,
        });

        if (authorization.ApprovedServiceDateFrom.HasValue
            || authorization.ApprovedServiceDateTo.HasValue
            || authorization.ExpirationDate.HasValue)
        {
            claimResponse.PreAuthPeriod = new Period
            {
                Start = authorization.ApprovedServiceDateFrom?.ToString("yyyy-MM-dd"),
                End = (authorization.ApprovedServiceDateTo ?? authorization.ExpirationDate)
                    ?.ToString("yyyy-MM-dd"),
            };
        }

        if (reviewActionCode is not null)
        {
            claimResponse.Extension.Add(new Extension
            {
                Url = ReviewActionExtensionUrl,
                Extension = new List<Extension>
                {
                    new(ReviewActionCodeUrl,
                        new Coding("https://codesystem.x12.org/005010/306",
                            reviewActionCode, reviewActionDisplay)),
                }
            });
        }

        // A denial says why, in the same coded shape $submit uses.
        if (!string.IsNullOrWhiteSpace(authorization.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(authorization.DenialReason))
        {
            claimResponse.Error.Add(new ClaimResponse.ErrorComponent
            {
                Code = new CodeableConcept(
                    "http://terminology.hl7.org/CodeSystem/adjudication-error",
                    authorization.DenialReasonCode ?? "denied",
                    authorization.DenialReason ?? "Request denied"),
            });
        }

        // A pended decision says what is outstanding, when the record holds it.
        var pendNote = authorization.PendReason ?? authorization.FollowUpAction;
        if (!string.IsNullOrWhiteSpace(pendNote))
        {
            claimResponse.ProcessNote.Add(new ClaimResponse.NoteComponent
            {
                Type = Hl7.Fhir.Model.NoteType.Print,
                Text = pendNote,
            });
        }

        AddRequestedServices(claimResponse, authorization);

        return WrapInBundle(claimResponse);
    }

    /// <summary>
    /// Requested and, where decided, approved service lines. Units are reported
    /// only when the record actually carries them — an inquiry does not invent
    /// an approved quantity for a decision that has not been made.
    /// </summary>
    private static void AddRequestedServices(
        ClaimResponse claimResponse, PriorAuthorizationRecord authorization)
    {
        var sequence = 1;
        foreach (var service in authorization.RequestedServices)
        {
            if (string.IsNullOrWhiteSpace(service.ProcedureCode))
                continue;

            var item = new ClaimResponse.ItemComponent { ItemSequence = sequence++ };

            item.Adjudication.Add(new ClaimResponse.AdjudicationComponent
            {
                Category = new CodeableConcept(
                    "http://terminology.hl7.org/CodeSystem/adjudication",
                    "submitted",
                    service.ProcedureCode),
                Value = service.RequestedUnits,
            });

            if (service.ApprovedUnits.HasValue)
            {
                item.Adjudication.Add(new ClaimResponse.AdjudicationComponent
                {
                    Category = new CodeableConcept(
                        "http://terminology.hl7.org/CodeSystem/adjudication",
                        "benefit",
                        service.ProcedureCode),
                    Value = service.ApprovedUnits,
                });
            }

            claimResponse.Item.Add(item);
        }
    }

    /// <summary>The status mapping table above, as code. Total over the enum.</summary>
    private static (FinancialResourceStatusCodes Status,
                    ClaimProcessingCodes Outcome,
                    string Disposition,
                    string? ReviewActionCode,
                    string? ReviewActionDisplay)
        MapStatus(PriorAuthorizationStatus status, string? reviewDecision) => status switch
        {
            PriorAuthorizationStatus.Submitted
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Queued, "pending", null, null),
            PriorAuthorizationStatus.InReview
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Queued, "pending", null, null),
            PriorAuthorizationStatus.Pended
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Queued,
                    "pended-additional-information", "A4", "Pending"),
            PriorAuthorizationStatus.Approved
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete, "approved", "A1", "Certified in total"),
            PriorAuthorizationStatus.Modified
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Partial, "modified", "A2", "Certified partial"),
            PriorAuthorizationStatus.Denied
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete, "denied", "A3", "Not certified"),
            PriorAuthorizationStatus.Expired
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete, "expired", null, null),
            PriorAuthorizationStatus.Cancelled
                => (FinancialResourceStatusCodes.Cancelled, ClaimProcessingCodes.Complete, "cancelled", null, null),

            // An unrecognised or absent status is never reported as a decision:
            // it reads as still in progress rather than as an approval CHO
            // cannot vouch for.
            _ => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Queued, "pending", null, null),
        };

    private static string StripPatientPrefix(string value)
        => value.StartsWith("Patient/", StringComparison.Ordinal)
            ? value["Patient/".Length..]
            : value;

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
