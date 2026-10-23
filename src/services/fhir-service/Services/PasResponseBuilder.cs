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
    /// X12 code list 306, the certification action: A1 certified in total, A3
    /// not certified, A4 pended. This is the system the reviewAction extension's
    /// action code is bound to, and the one coded statement CHO can make about a
    /// decision today.
    /// </summary>
    private const string X12CertificationActionSystem = "https://codesystem.x12.org/005010/306";

    /// <summary>
    /// WHY A DENIAL REASON IS A NOTE RATHER THAN AN <c>error</c>.
    ///
    /// <para>
    /// CHO stores a denial reason as a free string (<c>Authorization.DenialReasonCode</c>),
    /// and PAS has no element that will take one. <c>ClaimResponse.error.code</c>
    /// binds REQUIRED to <c>X12278RejectReasonCodes</c> — list 901, the 278's
    /// AAA03 — and it means "your request was rejected", a malformed or
    /// unprocessable submission, not "the service was denied". CHO emitted its
    /// internal string there under
    /// <c>http://terminology.hl7.org/CodeSystem/adjudication-error</c>: the wrong
    /// system, a code defined in no system, and the wrong question answered.
    /// </para>
    ///
    /// <para>
    /// The element that does mean "why the review decided as it did" is the
    /// reviewAction extension's <c>reasonCode</c>, bound REQUIRED to X12 external
    /// list 886. X12 licenses that list and the IG package enumerates none of it,
    /// so CHO cannot fill it from anything it holds, and a code invented for a
    /// required binding is worse than an absent one. <c>reasonCode</c> is 0..*,
    /// so leaving it out is conformant; see /mnt/project-files/pa-denial-codes
    /// for the mapping that would fill it.
    /// </para>
    ///
    /// <para>
    /// So the FACT of denial travels coded, as action code A3 on the reviewAction
    /// extension, and the REASON travels as prose a human reads. Nothing is lost
    /// that CHO previously conveyed — the old coding carried CHO's private string,
    /// which no consumer could resolve either.
    /// </para>
    /// </summary>
    private static ClaimResponse.NoteComponent DenialNote(string? reasonCode, string? reason)
        => new()
        {
            Type = Hl7.Fhir.Model.NoteType.Print,
            Text = string.IsNullOrWhiteSpace(reason)
                ? $"Denied ({reasonCode})."
                : string.IsNullOrWhiteSpace(reasonCode) ? reason : $"{reason} ({reasonCode})",
        };

    /// <summary>The reviewAction extension carrying one X12 certification action code.</summary>
    private static Extension ReviewAction(string actionCode, string? display = null)
        => new()
        {
            Url = ReviewActionExtensionUrl,
            Extension = new List<Extension>
            {
                new(ReviewActionCodeUrl, new Coding(X12CertificationActionSystem, actionCode, display)),
            },
        };

    /// <summary>
    /// Builds an approved ClaimResponse bundle.
    /// </summary>
    public Bundle BuildApprovedResponse(Claim claim, PasDecisionResult decision, List<PriorAuthorizationService>? services = null)
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

        AddRequestedServices(claimResponse, new PriorAuthorizationRecord { RequestedServices = services ?? new() });
        return WrapInBundle(claimResponse);
    }

    /// <summary>
    /// Builds a denied ClaimResponse bundle.
    /// </summary>
    public Bundle BuildDeniedResponse(Claim claim, PasDecisionResult decision, List<PriorAuthorizationService>? services = null)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Complete;
        claimResponse.Disposition = "denied";
        // A denial is still an authorization on record and still inquirable, so
        // it carries the same tracking handle an approval does.
        claimResponse.PreAuthRef = decision.AuthorizationNumber;

        // A3, not certified. The denied path previously carried no review action
        // at all, so a denial was the one decision whose outcome a PAS consumer
        // could not read from the element the IG defines for it.
        claimResponse.Extension.Add(ReviewAction("A3", "Not Certified"));
        claimResponse.ProcessNote.Add(DenialNote(decision.DenialReasonCode, decision.DenialReason));

        AddRequestedServices(claimResponse, new PriorAuthorizationRecord { RequestedServices = services ?? new() });
        return WrapInBundle(claimResponse);
    }

    /// <summary>
    /// Builds a pended ClaimResponse bundle with X12 review action code A4.
    /// </summary>
    public Bundle BuildPendedResponse(Claim claim, string? authorizationNumber = null, List<PriorAuthorizationService>? services = null)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Queued;
        claimResponse.Disposition = "pended";
        // Without this a pended decision had no tracking handle at all, so the
        // submitter could never inquire about the one outcome that most needs
        // following up. PAS-04 depends on it.
        claimResponse.PreAuthRef = authorizationNumber;

        // Add Da Vinci PAS reviewAction extension with A4 (pended) code
        claimResponse.Extension.Add(ReviewAction("A4", "Pending"));

        AddRequestedServices(claimResponse, new PriorAuthorizationRecord { RequestedServices = services ?? new() });
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
            // The submitter's own reference when PAS recorded one, so the
            // response names the patient the way the request did.
            Patient = new ResourceReference(
                $"Patient/{StripPatientPrefix(authorization.PatientReference ?? authorization.MemberId)}"),
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
            claimResponse.Extension.Add(ReviewAction(reviewActionCode, reviewActionDisplay));
        }

        // A denial says why, in the same shape $submit uses. See DenialNote.
        if (!string.IsNullOrWhiteSpace(authorization.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(authorization.DenialReason))
        {
            claimResponse.ProcessNote.Add(
                DenialNote(authorization.DenialReasonCode, authorization.DenialReason));
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

            var item = new ClaimResponse.ItemComponent { ItemSequence = service.LineNumber ?? sequence };
            sequence++;
            if (service.ServiceStatus is "A1" or "A2" or "A3" or "A4")
            {
                item.Adjudication.Add(new ClaimResponse.AdjudicationComponent
                {
                    Category = new CodeableConcept("http://terminology.hl7.org/CodeSystem/adjudication", "benefit"),
                    Value = service.ApprovedUnits,
                    Reason = service.ServiceStatus is "A2" or "A3"
                        ? new CodeableConcept { Coding = new List<Coding> { new() { Code = service.DenialReasonCode } }, Text = service.DenialReason }
                        : service.ServiceStatus == "A4" && !string.IsNullOrWhiteSpace(service.PendReason)
                            ? new CodeableConcept { Text = service.PendReason } : null,
                    Extension = new List<Extension>
                    {
                        ReviewAction(service.ServiceStatus),
                    },
                });
            }

            item.Adjudication.Add(new ClaimResponse.AdjudicationComponent
            {
                Category = new CodeableConcept(
                    "http://terminology.hl7.org/CodeSystem/adjudication",
                    "submitted",
                    service.ProcedureCode),
                Value = service.RequestedUnits,
            });

            if (service.ApprovedUnits.HasValue && service.ServiceStatus is not ("A1" or "A2" or "A3" or "A4"))
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
