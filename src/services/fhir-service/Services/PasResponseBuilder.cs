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

    /// <summary>
    /// The reviewAction extension carrying one X12 certification action code.
    /// PAS 2.2.1 types its code as a CodeableConcept and allows the extension
    /// only on an adjudication (<c>ClaimResponse.item.adjudication</c>, and the
    /// header and added-item adjudications), never on the ClaimResponse itself.
    /// </summary>
    private static Extension ReviewAction(string actionCode, string? display = null)
        => new()
        {
            Url = ReviewActionExtensionUrl,
            Extension = new List<Extension>
            {
                new(ReviewActionCodeUrl, new CodeableConcept(X12CertificationActionSystem, actionCode, display)),
            },
        };

    /// <summary>The adjudication category a decision on a line is stated under.</summary>
    private static CodeableConcept DecisionCategory()
        => new("http://terminology.hl7.org/CodeSystem/adjudication", "submitted");

    /// <summary>A process note, numbered as PAS requires (<c>processNote.number</c> 1..1).</summary>
    private static void AddNote(ClaimResponse claimResponse, ClaimResponse.NoteComponent note)
    {
        note.Number = claimResponse.ProcessNote.Count + 1;
        claimResponse.ProcessNote.Add(note);
    }

    /// <summary>
    /// Builds an approved ClaimResponse bundle.
    /// </summary>
    public Bundle BuildApprovedResponse(
        Claim claim, PasDecisionResult decision, List<PriorAuthorizationService>? services = null,
        Bundle? request = null, string? fhirBaseUrl = null)
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

        AddRequestedServices(claimResponse, new PriorAuthorizationRecord { RequestedServices = services ?? new() },
            ("A1", "Certified in total"), claim);
        return WrapInBundle(claimResponse, request, fhirBaseUrl);
    }

    /// <summary>
    /// Builds a denied ClaimResponse bundle.
    /// </summary>
    public Bundle BuildDeniedResponse(
        Claim claim, PasDecisionResult decision, List<PriorAuthorizationService>? services = null,
        Bundle? request = null, string? fhirBaseUrl = null)
    {
        var claimResponse = BuildBase(claim);
        claimResponse.Outcome = ClaimProcessingCodes.Complete;
        claimResponse.Disposition = "denied";
        // A denial is still an authorization on record and still inquirable, so
        // it carries the same tracking handle an approval does.
        claimResponse.PreAuthRef = decision.AuthorizationNumber;

        // A3, not certified, on every line: the element the IG defines for a
        // decision is the line's adjudication reviewAction.
        AddNote(claimResponse, DenialNote(decision.DenialReasonCode, decision.DenialReason));

        AddRequestedServices(claimResponse, new PriorAuthorizationRecord { RequestedServices = services ?? new() },
            ("A3", "Not Certified"), claim);
        return WrapInBundle(claimResponse, request, fhirBaseUrl);
    }

    /// <summary>
    /// Builds a pended ClaimResponse bundle with X12 review action code A4.
    /// </summary>
    public Bundle BuildPendedResponse(
        Claim claim, string? authorizationNumber = null, List<PriorAuthorizationService>? services = null,
        Bundle? request = null, string? fhirBaseUrl = null)
    {
        var claimResponse = BuildBase(claim);
        // PAS 2.2.1 binds outcome to complete | error | partial: receiving
        // and recording the request is complete; the pend is the review action.
        claimResponse.Outcome = ClaimProcessingCodes.Complete;
        claimResponse.Disposition = "pended";
        // Without this a pended decision had no tracking handle at all, so the
        // submitter could never inquire about the one outcome that most needs
        // following up. PAS-04 depends on it.
        claimResponse.PreAuthRef = authorizationNumber;

        // A4 (pended) on every line's adjudication reviewAction.
        AddRequestedServices(claimResponse, new PriorAuthorizationRecord { RequestedServices = services ?? new() },
            ("A4", "Pending"), claim);
        return WrapInBundle(claimResponse, request, fhirBaseUrl);
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
    public Bundle BuildInquiryResponse(
        PriorAuthorizationRecord authorization, Bundle? request = null, string? fhirBaseUrl = null)
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
            // The inquiry's own Claim, which PAS requires the response to name.
            Request = new ResourceReference { Display = "Prior authorization inquiry" },
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

        // The inquiring Claim names the patient, insurer and requestor the
        // response describes, as the submitter knows them.
        var inquiryClaim = request?.Entry.Select(e => e.Resource).OfType<Claim>().FirstOrDefault();
        if (inquiryClaim is not null)
        {
            if (inquiryClaim.Insurer?.Reference is not null) claimResponse.Insurer = inquiryClaim.Insurer;
            if (inquiryClaim.Provider?.Reference is not null) claimResponse.Requestor = inquiryClaim.Provider;
            if (inquiryClaim.Id is not null) claimResponse.Request = new ResourceReference($"Claim/{inquiryClaim.Id}");
        }

        // A denial says why, in the same shape $submit uses. See DenialNote.
        if (!string.IsNullOrWhiteSpace(authorization.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(authorization.DenialReason))
        {
            AddNote(claimResponse, DenialNote(authorization.DenialReasonCode, authorization.DenialReason));
        }

        // A pended decision says what is outstanding, when the record holds it.
        var pendNote = authorization.PendReason ?? authorization.FollowUpAction;
        if (!string.IsNullOrWhiteSpace(pendNote))
        {
            AddNote(claimResponse, new ClaimResponse.NoteComponent
            {
                Type = Hl7.Fhir.Model.NoteType.Print,
                Text = pendNote,
            });
        }

        AddRequestedServices(claimResponse, authorization,
            reviewActionCode is null ? null : (reviewActionCode, reviewActionDisplay), inquiryClaim);

        return WrapInBundle(claimResponse, request, fhirBaseUrl);
    }

    /// <summary>
    /// Requested and, where decided, approved service lines. Units are reported
    /// only when the record actually carries them — an inquiry does not invent
    /// an approved quantity for a decision that has not been made.
    /// </summary>
    private static void AddRequestedServices(
        ClaimResponse claimResponse, PriorAuthorizationRecord authorization,
        (string Code, string? Display)? decision = null, Claim? claim = null)
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
                    Category = DecisionCategory(),
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

            // A line with no decision of its own carries the request's.
            if (service.ServiceStatus is not ("A1" or "A2" or "A3" or "A4") && decision is { } overall)
            {
                item.Adjudication.Insert(0, new ClaimResponse.AdjudicationComponent
                {
                    Category = DecisionCategory(),
                    Value = overall.Code is "A1" or "A2" ? service.ApprovedUnits : null,
                    Extension = new List<Extension> { ReviewAction(overall.Code, overall.Display) },
                });
            }
            else if (service.ApprovedUnits.HasValue && service.ServiceStatus is not ("A1" or "A2" or "A3" or "A4"))
            {
                item.Adjudication.Add(new ClaimResponse.AdjudicationComponent
                {
                    // PAS 2.2.1 fixes every line adjudication's category to "submitted".
                    Category = DecisionCategory(),
                    Value = service.ApprovedUnits,
                });
            }

            claimResponse.Item.Add(item);
        }

        // A decision is stated on a line, so a response with none states it on
        // each line of the request: the request's own item sequences.
        if (claimResponse.Item.Count == 0 && decision is { } stated)
        {
            var sequences = claim?.Item.Select(i => i.Sequence).OfType<int>().ToList() is { Count: > 0 } claimed
                ? claimed
                : [1];
            foreach (var itemSequence in sequences)
            {
                claimResponse.Item.Add(new ClaimResponse.ItemComponent
                {
                    ItemSequence = itemSequence,
                    Adjudication = new List<ClaimResponse.AdjudicationComponent>
                    {
                        new()
                        {
                            Category = DecisionCategory(),
                            Extension = new List<Extension> { ReviewAction(stated.Code, stated.Display) },
                        },
                    },
                });
            }
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
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete, "pending", null, null),
            PriorAuthorizationStatus.InReview
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete, "pending", null, null),
            PriorAuthorizationStatus.Pended
                => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete,
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
            _ => (FinancialResourceStatusCodes.Active, ClaimProcessingCodes.Complete, "pending", null, null),
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
            // The party that asked: PAS names it on the response as the request did.
            Requestor = claim.Provider,
            Request = new ResourceReference($"Claim/{claim.Id}"),
        };
    }

    /// <summary>The system CHO identifies its PAS response bundles under (PAS: Bundle.identifier 1..1).</summary>
    public const string ResponseBundleIdentifierSystem = "http://cloudhealthoffice.com/fhir/pas-response";

    /// <summary>
    /// The PAS Response Bundle: the ClaimResponse first, then every resource it
    /// references (patient, insurer, requestor) and, in turn, what those
    /// reference, each exactly once, together with the request's people and
    /// organizations. All are taken from the request as the submitter sent
    /// them; the request Claim itself is not echoed (PAS: ClaimResponse.request
    /// is a back-reference). Entries are addressed under <paramref name="fhirBaseUrl"/>
    /// so a relative reference resolves against the ClaimResponse's own fullUrl.
    /// </summary>
    private static Bundle WrapInBundle(ClaimResponse claimResponse, Bundle? request = null, string? fhirBaseUrl = null)
    {
        var baseUrl = string.IsNullOrWhiteSpace(fhirBaseUrl) ? null : fhirBaseUrl.TrimEnd('/');
        string FullUrl(Resource resource) => baseUrl is null
            ? $"urn:uuid:{Guid.NewGuid()}"
            : $"{baseUrl}/{resource.TypeName}/{resource.Id}";

        var bundleId = Guid.NewGuid().ToString();
        var bundle = new Bundle
        {
            Id = bundleId,
            Meta = new Meta { Profile = new[] { PasResponseBundleProfile } },
            Identifier = new Identifier(ResponseBundleIdentifierSystem, bundleId),
            Type = Bundle.BundleType.Collection,
            Timestamp = DateTimeOffset.UtcNow,
            Entry = new List<Bundle.EntryComponent>
            {
                new()
                {
                    FullUrl = baseUrl is null ? $"urn:uuid:{claimResponse.Id}" : $"{baseUrl}/ClaimResponse/{claimResponse.Id}",
                    Resource = claimResponse,
                }
            },
        };

        if (request is null || baseUrl is null)
            return bundle;

        // The request's resources by Type/id (the form references take).
        // An echoed resource keeps the fullUrl the request gave it (PAS: the
        // same fullUrl, id and identifiers as in the request).
        var byKey = new Dictionary<string, Resource>(StringComparer.Ordinal);
        var requestFullUrl = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in request.Entry)
        {
            var resource = entry.Resource;
            if (resource?.Id is null || resource is Claim) continue;
            var key = $"{resource.TypeName}/{resource.Id}";
            if (byKey.TryAdd(key, resource) && !string.IsNullOrWhiteSpace(entry.FullUrl))
                requestFullUrl[key] = entry.FullUrl;
        }

        var included = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Resource>();
        void Include(Resource resource)
        {
            if (included.Add($"{resource.TypeName}/{resource.Id}")) pending.Enqueue(resource);
        }
        void IncludeReferenced(Base from)
        {
            foreach (var reference in References(from))
            {
                if (KeyOf(reference.Reference) is { } key && byKey.TryGetValue(key, out var target))
                    Include(target);
            }
        }

        // What the ClaimResponse names, except the back-reference to the request.
        var request_ = claimResponse.Request;
        claimResponse.Request = null;
        IncludeReferenced(claimResponse);
        claimResponse.Request = request_;

        // The request's people and organizations, which a PAS consumer reads
        // the response against (beneficiary, insurer, requestor, practitioner).
        foreach (var resource in byKey.Values.Where(r => r is Patient or Organization or Practitioner or PractitionerRole))
            Include(resource);

        while (pending.Count > 0)
        {
            var resource = pending.Dequeue();
            var copy = (Resource)resource.DeepCopy();
            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = requestFullUrl.TryGetValue($"{copy.TypeName}/{copy.Id}", out var echoed) ? echoed : FullUrl(copy),
                Resource = copy,
            });
            IncludeReferenced(copy);
        }

        return bundle;
    }

    private const string PasResponseBundleProfile =
        "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-pas-response-bundle";

    /// <summary><c>Type/id</c> of a relative or absolute literal reference, or null.</summary>
    private static string? KeyOf(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith('#'))
            return null;
        var parts = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var historyAt = Array.IndexOf(parts, "_history");
        if (historyAt >= 0) parts = parts[..historyAt];
        return parts.Length >= 2 ? $"{parts[^2]}/{parts[^1]}" : null;
    }

    /// <summary>Every ResourceReference inside an element, walked over the FHIR model.</summary>
    private static IEnumerable<ResourceReference> References(Base element)
    {
        if (element is ResourceReference reference) yield return reference;
        foreach (var child in element.Children)
        {
            foreach (var nested in References(child))
                yield return nested;
        }
    }
}
