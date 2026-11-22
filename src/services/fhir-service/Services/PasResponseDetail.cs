using Hl7.Fhir.Model;

namespace FhirService.Services;

/// <summary>
/// What a PAS response says about the decision it carries, beyond the line
/// outcomes <see cref="PasResponseBuilder"/> already states.
/// </summary>
/// <param name="AuthorizationNumber">The number CHO issued for the request (REF*BB / REF*NT on the 278).</param>
/// <param name="DecisionCode">The X12 306 certification action for the request as a whole, or null while undecided.</param>
/// <param name="DecidedAt">When that decision was made, or null while undecided.</param>
/// <param name="ApprovedPeriod">The period a certified request is valid for.</param>
/// <param name="HumanReviewed">
/// Whether a person made the decision: false for CHO's automated decisions,
/// true for a utilization reviewer's, null when nobody has decided yet.
/// </param>
public sealed record PasResponseContext(
    string? AuthorizationNumber,
    string? DecisionCode,
    DateTime? DecidedAt,
    Period? ApprovedPeriod,
    bool? HumanReviewed);

/// <summary>
/// THE PAS DATA A RESPONSE CARRIES BACK.
///
/// <para>
/// A PAS ClaimResponse stands in for the X12 278 response, and most of what the
/// 278 response carries is either the submitter's own data handed back so it can
/// be matched to what was sent (trace numbers, the requested dates, the
/// transmission identifiers) or what the UMO decided about each line (the
/// number it issued, the date it issued it, the period it is valid for, the
/// provider it is authorized for and what exactly was authorized). PAS 2.2.1
/// marks all of it must-support on <c>profile-claimresponse</c>.
/// </para>
///
/// <para>
/// Everything here is taken from one of two places and nowhere else: the
/// request Claim (the <c>$submit</c> Claim, or for <c>$inquire</c> the inquiry
/// Claim, which restates the request) and the stored decision
/// (<see cref="PasResponseContext"/>). Nothing is defaulted or invented. An
/// element whose source is absent is left out, which PAS allows: every element
/// set here is 0..1 or 0..*.
/// </para>
///
/// <para>
/// What a line carries depends on its decision, as on the 278:
/// <list type="bullet">
/// <item>every line: its trace numbers (2000F TRN), the dates the submitter
/// asked for, and the diagnoses the request tied to it;</item>
/// <item>a certified line (A1, A2): the authorization number, issue date and
/// period, the provider it is authorized for and the authorized detail;</item>
/// <item>a line that was not certified (A3): the administration reference
/// number, which on the 278 identifies the disallowed outcome.</item>
/// </list>
/// </para>
/// </summary>
public static class PasResponseDetail
{
    private const string PasSd = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/";

    public const string TransmissionIdentifiersUrl = PasSd + "extension-TransmissionIdentifiers";
    public const string ClaimResponseReviewerUrl = PasSd + "extension-claimResponseReviewer";
    public const string AuthorizedProviderUrl = PasSd + "extension-itemAuthorizedProvider";
    public const string AuthorizedDetailUrl = PasSd + "extension-itemAuthorizedDetail";
    public const string IdentifierJurisdictionUrl = PasSd + "extension-identifierJurisdiction";
    public const string IdentifierSubDepartmentUrl = PasSd + "extension-identifierSubDepartment";
    public const string ItemTraceNumberUrl = PasSd + "extension-itemTraceNumber";
    public const string ItemPreAuthIssueDateUrl = PasSd + "extension-itemPreAuthIssueDate";
    public const string ItemPreAuthPeriodUrl = PasSd + "extension-itemPreAuthPeriod";
    public const string AuthorizationNumberUrl = PasSd + "extension-authorizationNumber";
    public const string AdministrationReferenceNumberUrl = PasSd + "extension-administrationReferenceNumber";
    public const string ItemRequestedServiceDateUrl = PasSd + "extension-itemRequestedServiceDate";
    public const string AdmissionDatesUrl = PasSd + "extension-admissionDates";
    public const string DischargeDateUrl = PasSd + "extension-dischargeDate";
    public const string CommunicatedDiagnosisUrl = PasSd + "extension-communicatedDiagnosis";
    public const string CareTeamClaimScopeUrl = PasSd + "extension-careTeamClaimScope";
    public const string ReviewActionUrl = PasSd + "extension-reviewAction";
    public const string ReviewActionCodeUrl = PasSd + "extension-reviewActionCode";

    /// <summary>The system CHO identifies its own prior authorizations under.</summary>
    public const string ChoAuthorizationSystem = "http://cloudhealthoffice.com/prior-authorization";

    private const string PasTempCodes = "http://hl7.org/fhir/us/davinci-pas/CodeSystem/PASTempCodes";
    private const string X12CertificationActionSystem = "https://codesystem.x12.org/005010/306";
    private const string AdjudicationSystem = "http://terminology.hl7.org/CodeSystem/adjudication";

    private static readonly IReadOnlyDictionary<string, string> CertificationActionDisplay =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A1"] = "Certified in total",
            ["A2"] = "Certified - partial",
            ["A3"] = "Not Certified",
            ["A4"] = "Pended",
        };

    /// <summary>
    /// Adds the PAS detail to <paramref name="response"/>. Call before the
    /// response is wrapped in its Bundle, so the providers it names are
    /// included with it.
    /// </summary>
    public static void Apply(
        ClaimResponse response, Claim? requestClaim, Bundle? requestBundle, PasResponseContext context)
    {
        AddIdentifiers(response, requestClaim, context);
        AddTransmissionIdentifiers(response, requestClaim);
        AddReviewer(response, context);
        AddHeaderAdjudication(response, context);

        var certified = context.DecisionCode is "A1" or "A2";
        if (certified && requestClaim is not null)
        {
            foreach (var provider in AuthorizedProviders(requestBundle, requestClaim.CareTeam
                         .Where(c => ClaimScoped(c) == true)))
                response.Extension.Add(provider);
        }

        AddDecisionNote(response, context);

        foreach (var item in response.Item)
        {
            var requested = requestClaim?.Item.FirstOrDefault(i => i.Sequence == item.ItemSequence);
            AddLineDetail(item, requested, requestClaim, requestBundle, LineDecision(item) ?? context.DecisionCode, context);

            // Every note CHO writes is about the decision on the request, so it
            // applies to every line the request decided.
            item.NoteNumber = response.ProcessNote
                .Select(n => n.Number).Where(n => n.HasValue).Select(n => n!.Value)
                .Distinct().Select(n => (int?)n).ToList();
        }
    }

    // ── Header ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The submitter's own identifiers for the request (its patient event
    /// trace number, 2000E TRN), echoed as the 278 response echoes them, with
    /// the jurisdiction and sub-department that qualify them; then CHO's own
    /// identifier for the authorization.
    /// </summary>
    private static void AddIdentifiers(ClaimResponse response, Claim? requestClaim, PasResponseContext context)
    {
        foreach (var identifier in requestClaim?.Identifier ?? [])
        {
            if (string.IsNullOrWhiteSpace(identifier.Value)) continue;
            if (response.Identifier.Any(i => i.System == identifier.System && i.Value == identifier.Value)) continue;

            var echoed = new Identifier(identifier.System, identifier.Value)
            {
                Assigner = (ResourceReference?)identifier.Assigner?.DeepCopy(),
            };
            foreach (var extension in identifier.Extension.Where(e =>
                         e.Url is IdentifierJurisdictionUrl or IdentifierSubDepartmentUrl))
                echoed.Extension.Add((Extension)extension.DeepCopy());
            response.Identifier.Add(echoed);
        }

        if (!string.IsNullOrWhiteSpace(context.AuthorizationNumber)
            && !response.Identifier.Any(i => i.System == ChoAuthorizationSystem))
        {
            response.Identifier.Add(new Identifier(ChoAuthorizationSystem, context.AuthorizationNumber));
        }
    }

    /// <summary>
    /// The X12 envelope identifiers, turned round: the response travels from
    /// the receiver of the request back to its sender (GS02/GS03, ISA06/ISA08).
    /// </summary>
    private static void AddTransmissionIdentifiers(ClaimResponse response, Claim? requestClaim)
    {
        var sent = requestClaim?.Extension.FirstOrDefault(e => e.Url == TransmissionIdentifiersUrl);
        if (sent is null) return;

        string? Part(string name) => (sent.Extension.FirstOrDefault(e => e.Url == name)?.Value as FhirString)?.Value;
        var senderCode = Part("applicationSenderCode");
        var receiverCode = Part("applicationReceiverCode");
        if (string.IsNullOrWhiteSpace(senderCode) || string.IsNullOrWhiteSpace(receiverCode)) return;

        var turned = new Extension { Url = TransmissionIdentifiersUrl };
        turned.Extension.Add(new Extension("applicationSenderCode", new FhirString(receiverCode)));
        turned.Extension.Add(new Extension("applicationReceiverCode", new FhirString(senderCode)));
        if (Part("interchangeReceiverID") is { Length: > 0 } interchangeReceiver)
            turned.Extension.Add(new Extension("interchangeSenderID", new FhirString(interchangeReceiver)));
        if (Part("interchangeSenderID") is { Length: > 0 } interchangeSender)
            turned.Extension.Add(new Extension("interchangeReceiverID", new FhirString(interchangeSender)));
        response.Extension.Add(turned);
    }

    /// <summary>
    /// Whether a person reviewed the request. Stated only once it was decided:
    /// before that there is no review to describe. CHO does not record a
    /// reviewer's NPI or specialty, so neither is sent.
    /// </summary>
    private static void AddReviewer(ClaimResponse response, PasResponseContext context)
    {
        if (context.HumanReviewed is not { } human || context.DecisionCode is not ("A1" or "A2" or "A3"))
            return;

        var reviewer = new Extension { Url = ClaimResponseReviewerUrl };
        reviewer.Extension.Add(new Extension("wasHumanReviewedFlag", new FhirBoolean(human)));
        response.Extension.Add(reviewer);
    }

    /// <summary>The decision on the request as a whole, beside the per-line ones.</summary>
    private static void AddHeaderAdjudication(ClaimResponse response, PasResponseContext context)
    {
        if (context.DecisionCode is not { } code || !CertificationActionDisplay.TryGetValue(code, out var display))
            return;
        if (response.Adjudication.Any(a => a.Extension.Any(e => e.Url == ReviewActionUrl)))
            return;

        // PAS 2.2.1 allows the reviewActionCode only on a line's (or added
        // line's) adjudication, so the header's reviewAction carries the
        // decision's number; the code is on every line.
        if (string.IsNullOrWhiteSpace(context.AuthorizationNumber))
            return;
        response.Adjudication.Add(new ClaimResponse.AdjudicationComponent
        {
            Category = new CodeableConcept(AdjudicationSystem, "submitted"),
            Extension =
            [
                new Extension
                {
                    Url = ReviewActionUrl,
                    Extension = [new Extension("number", new FhirString(context.AuthorizationNumber))],
                },
            ],
        });
    }

    /// <summary>
    /// For a certified request, a note saying what was authorized and for
    /// when, so a human reading the response sees it without decoding the
    /// adjudications. Denial and pend notes are written by the builder.
    /// </summary>
    private static void AddDecisionNote(ClaimResponse response, PasResponseContext context)
    {
        if (context.DecisionCode is not ("A1" or "A2")) return;

        var text = context.DecisionCode == "A1" ? "Certified in total" : "Certified in part";
        if (!string.IsNullOrWhiteSpace(context.AuthorizationNumber))
            text += $" under authorization {context.AuthorizationNumber}";
        if (context.ApprovedPeriod is { Start: { Length: > 0 } start } period)
            text += string.IsNullOrWhiteSpace(period.End)
                ? $", for services from {start}"
                : $", for services from {start} to {period.End}";

        response.ProcessNote.Add(new ClaimResponse.NoteComponent
        {
            Number = response.ProcessNote.Count + 1,
            Type = NoteType.Display,
            Text = text + ".",
        });
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    private static void AddLineDetail(
        ClaimResponse.ItemComponent item, Claim.ItemComponent? requested, Claim? requestClaim,
        Bundle? requestBundle, string? decision, PasResponseContext context)
    {
        if (requested is not null)
        {
            foreach (var trace in requested.Extension.Where(e => e.Url == ItemTraceNumberUrl))
                item.Extension.Add((Extension)trace.DeepCopy());

            DataType? requestedDate = requested.Serviced switch
            {
                Date date when !string.IsNullOrWhiteSpace(date.Value) => new FhirDateTime(date.Value),
                Period period => (Period)period.DeepCopy(),
                _ => null,
            };
            if (requestedDate is not null)
                item.Extension.Add(new Extension(ItemRequestedServiceDateUrl, requestedDate));

            if (requestClaim is not null)
            {
                foreach (var diagnosis in Diagnoses(requestClaim, requested))
                    item.Extension.Add(new Extension(CommunicatedDiagnosisUrl, diagnosis));
            }
        }

        if (decision is "A1" or "A2")
        {
            if (context.DecidedAt is { } decidedAt)
                item.Extension.Add(new Extension(ItemPreAuthIssueDateUrl, new Date(decidedAt.ToString("yyyy-MM-dd"))));
            if (context.ApprovedPeriod is { } period)
                item.Extension.Add(new Extension(ItemPreAuthPeriodUrl, (Period)period.DeepCopy()));
            if (!string.IsNullOrWhiteSpace(context.AuthorizationNumber))
                item.Extension.Add(new Extension(AuthorizationNumberUrl, new FhirString(context.AuthorizationNumber)));

            if (requested is not null && requestClaim is not null)
            {
                var team = requested.CareTeamSequence
                    .Select(s => requestClaim.CareTeam.FirstOrDefault(c => c.Sequence == s))
                    .OfType<Claim.CareTeamComponent>();
                foreach (var provider in AuthorizedProviders(requestBundle, team))
                    item.Extension.Add(provider);

                item.Extension.Add(AuthorizedDetail(item, requested));
                AddInpatientDates(item, requested, requestClaim);
            }
        }
        // Not certified or pended: the 278 tracks it by its administrative
        // reference number (REF*NT), not a certification number.
        else if (decision is "A3" or "A4" && !string.IsNullOrWhiteSpace(context.AuthorizationNumber))
        {
            item.Extension.Add(new Extension(AdministrationReferenceNumberUrl, new FhirString(context.AuthorizationNumber)));
        }
    }

    /// <summary>
    /// What was authorized for the line: the service and modifiers asked for,
    /// at the price asked, in the quantity certified (the requested quantity
    /// where the decision did not reduce it).
    /// </summary>
    private static Extension AuthorizedDetail(ClaimResponse.ItemComponent item, Claim.ItemComponent requested)
    {
        var detail = new Extension { Url = AuthorizedDetailUrl };
        if (requested.ProductOrService is { } service)
            detail.Extension.Add(new Extension("productOrServiceCode", (CodeableConcept)service.DeepCopy()));
        foreach (var modifier in requested.Modifier)
            detail.Extension.Add(new Extension("modifier", (CodeableConcept)modifier.DeepCopy()));
        if (requested.UnitPrice is { } price)
            detail.Extension.Add(new Extension("unitPrice", (Money)price.DeepCopy()));

        var certifiedUnits = item.Adjudication
            .Where(a => a.Extension.Any(e => e.Url == ReviewActionUrl))
            .Select(a => a.Value).FirstOrDefault(v => v.HasValue);
        var quantity = requested.Quantity is { } asked ? (Quantity)asked.DeepCopy() : null;
        if (certifiedUnits is { } units)
        {
            quantity ??= new Quantity();
            quantity.Value = units;
        }
        if (quantity?.Value is not null)
        {
            // A quantity states its unit (prof-2). A count the request gave
            // bare is in units: UCUM's unity.
            if (string.IsNullOrWhiteSpace(quantity.Unit) && string.IsNullOrWhiteSpace(quantity.Code))
            {
                quantity.System = "http://unitsofmeasure.org";
                quantity.Code = "1";
                quantity.Unit = "unit";
            }
            detail.Extension.Add(new Extension("quantity", quantity));
        }

        return detail;
    }

    /// <summary>
    /// The admission and discharge dates the request gave (supportingInfo
    /// admissionDates / dischargeDates), on the line they belong to: a line
    /// names its supportingInfo by informationSequence, and dates the request
    /// gives for the whole admission apply to every line.
    /// </summary>
    private static void AddInpatientDates(ClaimResponse.ItemComponent item, Claim.ItemComponent requested, Claim requestClaim)
    {
        IEnumerable<Claim.SupportingInformationComponent> Of(string code)
        {
            var all = requestClaim.SupportingInfo.Where(s =>
                s.Category?.Coding.Any(c => c.System == PasTempCodes && c.Code == code) == true).ToList();
            var named = all.Where(s => requested.InformationSequence.Contains(s.Sequence)).ToList();
            return named.Count > 0 ? named : all;
        }

        if (Of("admissionDates").Select(s => s.Timing).FirstOrDefault(t => t is Date or Period) is { } admission)
            item.Extension.Add(new Extension(AdmissionDatesUrl, (DataType)admission.DeepCopy()));
        if (Of("dischargeDates").Select(s => s.Timing).FirstOrDefault(t => t is Date) is Date discharge)
            item.Extension.Add(new Extension(DischargeDateUrl, (Date)discharge.DeepCopy()));
    }

    /// <summary>The diagnosis codes the request tied to the line (item.diagnosisSequence).</summary>
    private static IEnumerable<CodeableConcept> Diagnoses(Claim requestClaim, Claim.ItemComponent requested)
        => requested.DiagnosisSequence
            .Select(s => requestClaim.Diagnosis.FirstOrDefault(d => d.Sequence == s)?.Diagnosis)
            .OfType<CodeableConcept>()
            .Select(c => (CodeableConcept)c.DeepCopy());

    /// <summary>
    /// The providers a certified request or line is authorized for: the care
    /// team members the request named for it, each as the Practitioner or
    /// Organization PAS allows here (a PractitionerRole is followed to its
    /// practitioner), with the role and qualification the request gave them.
    /// </summary>
    private static IEnumerable<Extension> AuthorizedProviders(Bundle? requestBundle, IEnumerable<Claim.CareTeamComponent> team)
    {
        foreach (var member in team)
        {
            var reference = member.Provider?.Reference;
            if (string.IsNullOrWhiteSpace(reference)) continue;

            var provider = reference.Contains("PractitionerRole/", StringComparison.Ordinal)
                ? RoleTarget(requestBundle, member.Provider!)
                : reference.Contains("Practitioner/", StringComparison.Ordinal)
                  || reference.Contains("Organization/", StringComparison.Ordinal)
                    ? (ResourceReference)member.Provider!.DeepCopy()
                    : null;
            if (provider is null) continue;

            var extension = new Extension { Url = AuthorizedProviderUrl };
            extension.Extension.Add(new Extension("provider", provider));
            if (member.Role is { } role)
                extension.Extension.Add(new Extension("role", (CodeableConcept)role.DeepCopy()));
            if (member.Qualification is { } qualification)
                extension.Extension.Add(new Extension("qualification", (CodeableConcept)qualification.DeepCopy()));
            yield return extension;
        }
    }

    /// <summary>The PractitionerRole's practitioner (or organization), as the request's own Bundle names it.</summary>
    private static ResourceReference? RoleTarget(Bundle? requestBundle, ResourceReference roleReference)
    {
        if (requestBundle is null) return null;
        var role = Pas.PasAuthorizationMapper.Resolve(requestBundle, roleReference) as PractitionerRole;
        return role?.Practitioner?.Reference is { Length: > 0 }
            ? (ResourceReference)role.Practitioner.DeepCopy()
            : role?.Organization?.Reference is { Length: > 0 }
                ? (ResourceReference)role.Organization.DeepCopy()
                : null;
    }

    private static bool? ClaimScoped(Claim.CareTeamComponent member)
        => (member.Extension.FirstOrDefault(e => e.Url == CareTeamClaimScopeUrl)?.Value as FhirBoolean)?.Value;

    /// <summary>The certification action the line's own adjudication states, if any.</summary>
    private static string? LineDecision(ClaimResponse.ItemComponent item)
        => item.Adjudication
            .SelectMany(a => a.Extension.Where(e => e.Url == ReviewActionUrl))
            .SelectMany(e => e.Extension.Where(x => x.Url == ReviewActionCodeUrl))
            .Select(x => (x.Value as CodeableConcept)?.Coding.FirstOrDefault()?.Code)
            .FirstOrDefault(c => c is not null);

    /// <summary>
    /// The reviewAction extension: the certification action and, where one was
    /// issued, the number it was issued under (PAS reviewAction.number).
    /// </summary>
    public static Extension ReviewAction(string code, string? display, string? number)
    {
        var action = new Extension { Url = ReviewActionUrl };
        if (!string.IsNullOrWhiteSpace(number))
            action.Extension.Add(new Extension("number", new FhirString(number)));
        action.Extension.Add(new Extension(ReviewActionCodeUrl,
            new CodeableConcept(X12CertificationActionSystem, code, display)));
        return action;
    }
}
