using Hl7.Fhir.Model;

namespace FhirService.Services;

/// <summary>
/// CMS-0057-F Compliance Checker
///
/// Validates FHIR resources against CMS-0057-F Prior Authorization Rule requirements:
/// - Data class validation (USCDI v1/v2)
/// - Timeline requirements (response times)
/// - Da Vinci IG conformance (PDex, CRD, DTR, PAS)
/// - US Core IG conformance
///
/// References:
/// - CMS-0057-F: Advancing Interoperability and Improving Prior Authorization Processes (March 2023)
/// - USCDI v1 &amp; v2: United States Core Data for Interoperability
/// - Da Vinci PDex: Payer Data Exchange Implementation Guide
/// - Da Vinci PAS: Prior Authorization Support Implementation Guide
/// - Da Vinci CRD: Coverage Requirements Discovery
/// - Da Vinci DTR: Documentation Templates and Rules
/// - US Core IG v3.1.1+
/// </summary>
public interface ICms0057ComplianceChecker
{
    IReadOnlyList<string> SupportedResourceTypes { get; }
    ComplianceResult ValidateCompliance(Resource resource);
    IReadOnlyList<ComplianceResult> ValidateBatchCompliance(IEnumerable<Resource> resources);
    string GenerateComplianceReport(IReadOnlyList<ComplianceResult> results);
}

public class Cms0057ComplianceChecker : ICms0057ComplianceChecker
{
    public IReadOnlyList<string> SupportedResourceTypes { get; } =
    [
        "ServiceRequest", "ExplanationOfBenefit", "Claim", "Patient",
        // PR 3 — appeal-projection resources. Validation is intentionally
        // lightweight: structural checks (required fields, profile
        // declaration) rather than deep HL7 semantic validation, which is
        // deferred to a future PR.
        "Task", "Communication", "DocumentReference", "ClaimResponse"
    ];

    /// <summary>
    /// Main compliance validation — dispatches to resource-specific validators.
    /// </summary>
    public ComplianceResult ValidateCompliance(Resource resource)
    {
        return resource switch
        {
            ServiceRequest sr => ValidateServiceRequest(sr),
            ExplanationOfBenefit eob => ValidateExplanationOfBenefit(eob),
            Claim claim => ValidateClaim(claim),
            Patient patient => ValidatePatient(patient),
            Hl7.Fhir.Model.Task task => ValidateAppealTask(task),
            Communication communication => ValidateAppealCommunication(communication),
            DocumentReference documentReference => ValidateAppealDocumentReference(documentReference),
            ClaimResponse claimResponse => ValidateAppealClaimResponse(claimResponse),
            _ => BuildUnsupportedResult(resource)
        };
    }

    // ── Appeal projections (PR 3) ────────────────────────────────────────
    // Lightweight structural validation — verify required elements and
    // the cho-appeal-* profile declaration. Deep validation of bindings
    // and slicing against the StructureDefinition differential is a
    // future PR's work.

    private const string AppealTaskProfile =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-task";
    private const string AppealCommunicationProfile =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-communication";
    private const string AppealDocumentReferenceProfile =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-document-reference";
    private const string AppealClaimResponseProfile =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-claim-response";

    private static ComplianceResult ValidateAppealTask(Hl7.Fhir.Model.Task resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var required = 0;
        const int total = 6;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "Task.status is required", Requirement: "CHO Appeal Task"));
        else required++;

        if (resource.Intent != Hl7.Fhir.Model.Task.TaskIntent.Order)
            issues.Add(new("error", "WRONG_INTENT", "cho-appeal-task requires Task.intent = 'order'", Requirement: "CHO Appeal Task"));
        else required++;

        if (resource.For is null)
            issues.Add(new("error", "MISSING_FOR", "Task.for (Patient reference) is required", Requirement: "CHO Appeal Task"));
        else required++;

        if (resource.Focus is null)
            issues.Add(new("error", "MISSING_FOCUS", "Task.focus (Claim reference) is required", Requirement: "CHO Appeal Task"));
        else required++;

        if (resource.Requester is null)
            issues.Add(new("error", "MISSING_REQUESTER", "Task.requester is required", Requirement: "CHO Appeal Task"));
        else required++;

        if (HasProfile(resource.Meta, AppealTaskProfile)) required++;
        else warnings.Add(new("MISSING_PROFILE",
            $"Task should declare profile {AppealTaskProfile}",
            $"Add {AppealTaskProfile} to Meta.profile"));

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues, Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "Task", RequiredElementsPresent: required,
                TotalRequiredElements: total,
                UsCoreSections: ["Appeal Task"],
                DaVinciProfiles: ["CHO Appeal Task"],
                UscdiDataClasses: [],
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    private static ComplianceResult ValidateAppealCommunication(Communication resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var required = 0;
        const int total = 4;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "Communication.status is required", Requirement: "CHO Appeal Communication"));
        else required++;

        if (resource.Subject is null)
            issues.Add(new("error", "MISSING_SUBJECT", "Communication.subject (Patient) is required", Requirement: "CHO Appeal Communication"));
        else required++;

        if (resource.About is null || resource.About.Count == 0)
            issues.Add(new("error", "MISSING_ABOUT", "Communication.about (Task back-reference) is required", Requirement: "CHO Appeal Communication"));
        else required++;

        if (HasProfile(resource.Meta, AppealCommunicationProfile)) required++;
        else warnings.Add(new("MISSING_PROFILE",
            $"Communication should declare profile {AppealCommunicationProfile}",
            $"Add {AppealCommunicationProfile} to Meta.profile"));

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues, Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "Communication", RequiredElementsPresent: required,
                TotalRequiredElements: total,
                UsCoreSections: ["Appeal Communication"],
                DaVinciProfiles: ["CHO Appeal Communication"],
                UscdiDataClasses: [],
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    private static ComplianceResult ValidateAppealDocumentReference(DocumentReference resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var required = 0;
        const int total = 4;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "DocumentReference.status is required", Requirement: "CHO Appeal DocumentReference"));
        else required++;

        if (resource.Subject is null)
            issues.Add(new("error", "MISSING_SUBJECT", "DocumentReference.subject (Patient) is required", Requirement: "CHO Appeal DocumentReference"));
        else required++;

        if (resource.Content is null || resource.Content.Count == 0)
            issues.Add(new("error", "MISSING_CONTENT", "DocumentReference.content is required", Requirement: "CHO Appeal DocumentReference"));
        else required++;

        if (resource.Context is null || resource.Context.Related is null || resource.Context.Related.Count == 0)
            warnings.Add(new("MISSING_CONTEXT_RELATED",
                "DocumentReference.context.related (Task back-reference) is recommended",
                "Add Task/{appealId} to Context.related"));

        if (HasProfile(resource.Meta, AppealDocumentReferenceProfile)) required++;
        else warnings.Add(new("MISSING_PROFILE",
            $"DocumentReference should declare profile {AppealDocumentReferenceProfile}",
            $"Add {AppealDocumentReferenceProfile} to Meta.profile"));

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues, Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "DocumentReference", RequiredElementsPresent: required,
                TotalRequiredElements: total,
                UsCoreSections: ["Appeal DocumentReference"],
                DaVinciProfiles: ["CHO Appeal DocumentReference"],
                UscdiDataClasses: [],
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    private static ComplianceResult ValidateAppealClaimResponse(ClaimResponse resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var required = 0;
        const int total = 5;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "ClaimResponse.status is required", Requirement: "CHO Appeal ClaimResponse"));
        else required++;

        if (resource.Patient is null)
            issues.Add(new("error", "MISSING_PATIENT", "ClaimResponse.patient is required", Requirement: "CHO Appeal ClaimResponse"));
        else required++;

        if (resource.Request is null)
            issues.Add(new("error", "MISSING_REQUEST", "ClaimResponse.request (original Claim) is required", Requirement: "CHO Appeal ClaimResponse"));
        else required++;

        if (resource.Outcome is null)
            issues.Add(new("error", "MISSING_OUTCOME", "ClaimResponse.outcome is required", Requirement: "CHO Appeal ClaimResponse"));
        else required++;

        if (HasProfile(resource.Meta, AppealClaimResponseProfile)) required++;
        else warnings.Add(new("MISSING_PROFILE",
            $"ClaimResponse should declare profile {AppealClaimResponseProfile}",
            $"Add {AppealClaimResponseProfile} to Meta.profile"));

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues, Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "ClaimResponse", RequiredElementsPresent: required,
                TotalRequiredElements: total,
                UsCoreSections: ["Appeal ClaimResponse"],
                DaVinciProfiles: ["CHO Appeal ClaimResponse"],
                UscdiDataClasses: ["Financial"],
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    private static bool HasProfile(Meta? meta, string profileUrl) =>
        meta?.Profile?.Any(p => string.Equals(p, profileUrl, StringComparison.Ordinal)) ?? false;

    public IReadOnlyList<ComplianceResult> ValidateBatchCompliance(IEnumerable<Resource> resources)
        => resources.Select(ValidateCompliance).ToList();

    public string GenerateComplianceReport(IReadOnlyList<ComplianceResult> results)
    {
        var totalResources = results.Count;
        var compliantResources = results.Count(r => r.Compliant);
        var totalIssues = results.Sum(r => r.Issues.Count);
        var totalWarnings = results.Sum(r => r.Warnings.Count);
        var compliancePercentage = totalResources > 0
            ? (int)Math.Round(100.0 * compliantResources / totalResources)
            : 0;

        var breakdown = string.Join("", results.Select((r, i) =>
            $"""

              {i + 1}. {r.Summary.ResourceType}
                 - Compliant: {(r.Compliant ? "Yes" : "No")}
                 - Required Elements: {r.Summary.RequiredElementsPresent}/{r.Summary.TotalRequiredElements}
                 - USCDI Data Classes: {(r.Summary.UscdiDataClasses.Count > 0 ? string.Join(", ", r.Summary.UscdiDataClasses) : "None")}
                 - Da Vinci Profiles: {(r.Summary.DaVinciProfiles.Count > 0 ? string.Join(", ", r.Summary.DaVinciProfiles) : "None")}
                 - Issues: {r.Issues.Count}
                 - Warnings: {r.Warnings.Count}
            """));

        return $"""
            CMS-0057-F Compliance Report
            ============================

            Overall Summary:
            - Total Resources Validated: {totalResources}
            - Compliant Resources: {compliantResources} ({compliancePercentage}%)
            - Total Issues: {totalIssues}
            - Total Warnings: {totalWarnings}

            Resource Breakdown:
            {breakdown}

            Recommendations:
            {(totalIssues > 0 ? "- Address critical errors to achieve compliance" : "- No critical issues found")}
            {(totalWarnings > 0 ? $"- Review {totalWarnings} warnings for best practices" : "- No warnings")}
            """;
    }

    // ── ServiceRequest (Da Vinci PAS) ────────────────────────────────────────

    private static ComplianceResult ValidateServiceRequest(ServiceRequest resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var uscdiClasses = new List<string>();
        var requiredPresent = 0;
        const int totalRequired = 10;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "ServiceRequest.status is required", Requirement: "Da Vinci PAS"));
        else
            requiredPresent++;

        if (resource.Intent is null)
            issues.Add(new("error", "MISSING_INTENT", "ServiceRequest.intent is required", Requirement: "Da Vinci PAS"));
        else
            requiredPresent++;

        if (resource.Subject is null)
        {
            issues.Add(new("error", "MISSING_SUBJECT", "ServiceRequest.subject (patient reference) is required", Requirement: "Da Vinci PAS"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Patient Demographics");
        }

        if (resource.AuthoredOn is null)
            warnings.Add(new("MISSING_AUTHORED_ON", "ServiceRequest.authoredOn should be present for timeline tracking", "Add authoredOn for CMS-0057-F timeline compliance tracking"));
        else
            requiredPresent++;

        if (resource.Requester is null)
        {
            issues.Add(new("error", "MISSING_REQUESTER", "ServiceRequest.requester is required", Requirement: "Da Vinci PAS"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Provenance");
        }

        if (resource.Insurance is null || resource.Insurance.Count == 0)
        {
            warnings.Add(new("MISSING_INSURANCE", "ServiceRequest.insurance should reference coverage information", "Add insurance reference for coverage tracking"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Coverage");
        }

        if (resource.Code is null && (resource.OrderDetail is null || resource.OrderDetail.Count == 0))
        {
            issues.Add(new("error", "MISSING_SERVICE_CODE", "ServiceRequest must have either code or orderDetail with procedure codes", Requirement: "Da Vinci PAS"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Procedures");
        }

        if (resource.ReasonCode is null or { Count: 0 } && resource.ReasonReference is null or { Count: 0 })
        {
            warnings.Add(new("MISSING_REASON", "ServiceRequest should include reasonCode (diagnosis) for clinical context", "Add ICD-10 diagnosis codes in reasonCode"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Problems");
        }

        if (resource.Occurrence is null)
        {
            warnings.Add(new("MISSING_TIMING", "ServiceRequest should include occurrence timing", "Add occurrencePeriod for date range"));
        }
        else
        {
            requiredPresent++;
        }

        if (resource.Priority == RequestPriority.Urgent)
        {
            uscdiClasses.Add("Clinical Notes");
            requiredPresent++;
        }

        var isUrgent = resource.Priority == RequestPriority.Urgent || resource.Priority == RequestPriority.Stat;
        var timelineCompliance = CheckPriorAuthTimeline(resource.AuthoredOn, isUrgent);

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues,
            Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "ServiceRequest",
                RequiredElementsPresent: requiredPresent,
                TotalRequiredElements: totalRequired,
                UsCoreSections: ["ServiceRequest"],
                DaVinciProfiles: ["PAS ServiceRequest"],
                UscdiDataClasses: uscdiClasses.Distinct().ToList(),
                TimelineCompliance: timelineCompliance));
    }

    // ── ExplanationOfBenefit ─────────────────────────────────────────────────

    private static ComplianceResult ValidateExplanationOfBenefit(ExplanationOfBenefit resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var uscdiClasses = new List<string>();
        var requiredPresent = 0;
        const int totalRequired = 12;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "ExplanationOfBenefit.status is required", Requirement: "FHIR R4"));
        else
            requiredPresent++;

        if (resource.Type is null)
            issues.Add(new("error", "MISSING_TYPE", "ExplanationOfBenefit.type is required", Requirement: "FHIR R4"));
        else
            requiredPresent++;

        if (resource.Use is null)
            issues.Add(new("error", "MISSING_USE", "ExplanationOfBenefit.use is required", Requirement: "FHIR R4"));
        else
            requiredPresent++;

        if (resource.Patient is null)
        {
            issues.Add(new("error", "MISSING_PATIENT", "ExplanationOfBenefit.patient is required", Requirement: "US Core"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Patient Demographics");
        }

        if (resource.Created is null)
            warnings.Add(new("MISSING_CREATED", "ExplanationOfBenefit.created should be present", "Add created date for CMS-0057-F tracking"));
        else
            requiredPresent++;

        if (resource.Insurer is null)
        {
            issues.Add(new("error", "MISSING_INSURER", "ExplanationOfBenefit.insurer is required", Requirement: "FHIR R4"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Coverage");
        }

        if (resource.Provider is null)
        {
            issues.Add(new("error", "MISSING_PROVIDER", "ExplanationOfBenefit.provider is required", Requirement: "FHIR R4"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Provenance");
        }

        if (resource.Outcome is null)
            warnings.Add(new("MISSING_OUTCOME", "ExplanationOfBenefit.outcome should be present", "Add outcome for US Core compliance"));
        else
            requiredPresent++;

        if (resource.Item is null || resource.Item.Count == 0)
        {
            issues.Add(new("error", "MISSING_ITEMS", "ExplanationOfBenefit must have at least one item", Requirement: "US Core"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Procedures");

            var itemsWithAdjudication = resource.Item.Count(item => item.Adjudication is { Count: > 0 });
            if (itemsWithAdjudication == 0)
            {
                warnings.Add(new("MISSING_ADJUDICATION", "Items should include adjudication details", "Add adjudication with submitted, eligible, and benefit amounts"));
            }
            else
            {
                requiredPresent++;
                uscdiClasses.Add("Financial");
            }
        }

        if (resource.Payment is null)
        {
            warnings.Add(new("MISSING_PAYMENT", "ExplanationOfBenefit.payment should include payment information", "Add payment details for transparency"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Financial");
        }

        if (resource.Total is null || resource.Total.Count == 0)
        {
            warnings.Add(new("MISSING_TOTAL", "ExplanationOfBenefit.total should include total amounts", "Add total submitted and benefit amounts"));
        }
        else
        {
            requiredPresent++;
        }

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues,
            Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "ExplanationOfBenefit",
                RequiredElementsPresent: requiredPresent,
                TotalRequiredElements: totalRequired,
                UsCoreSections: ["ExplanationOfBenefit"],
                DaVinciProfiles: ["PDex ExplanationOfBenefit"],
                UscdiDataClasses: uscdiClasses.Distinct().ToList(),
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    // ── Claim ────────────────────────────────────────────────────────────────

    private static ComplianceResult ValidateClaim(Claim resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var uscdiClasses = new List<string>();
        var requiredPresent = 0;
        const int totalRequired = 10;

        if (resource.Status is null)
            issues.Add(new("error", "MISSING_STATUS", "Claim.status is required", Requirement: "FHIR R4"));
        else
            requiredPresent++;

        if (resource.Type is null)
            issues.Add(new("error", "MISSING_TYPE", "Claim.type is required", Requirement: "FHIR R4"));
        else
            requiredPresent++;

        if (resource.Use is null)
            issues.Add(new("error", "MISSING_USE", "Claim.use is required", Requirement: "FHIR R4"));
        else
            requiredPresent++;

        if (resource.Patient is null)
        {
            issues.Add(new("error", "MISSING_PATIENT", "Claim.patient is required", Requirement: "US Core"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Patient Demographics");
        }

        if (resource.Created is null)
            warnings.Add(new("MISSING_CREATED", "Claim.created should be present", "Add created date for CMS-0057-F tracking"));
        else
            requiredPresent++;

        if (resource.Provider is null)
        {
            issues.Add(new("error", "MISSING_PROVIDER", "Claim.provider is required", Requirement: "FHIR R4"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Provenance");
        }

        if (resource.Priority is null)
        {
            warnings.Add(new("MISSING_PRIORITY", "Claim.priority should be specified", "Add priority code"));
        }
        else
        {
            requiredPresent++;
        }

        if (resource.Insurance is null || resource.Insurance.Count == 0)
        {
            issues.Add(new("error", "MISSING_INSURANCE", "Claim must have at least one insurance", Requirement: "FHIR R4"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Coverage");
        }

        if (resource.Item is null || resource.Item.Count == 0)
        {
            issues.Add(new("error", "MISSING_ITEMS", "Claim must have at least one item", Requirement: "FHIR R4"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Procedures");
        }

        if (resource.Diagnosis is null || resource.Diagnosis.Count == 0)
        {
            warnings.Add(new("MISSING_DIAGNOSIS", "Claim should include diagnosis codes", "Add ICD-10 diagnosis codes"));
        }
        else
        {
            requiredPresent++;
            uscdiClasses.Add("Problems");
        }

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues,
            Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "Claim",
                RequiredElementsPresent: requiredPresent,
                TotalRequiredElements: totalRequired,
                UsCoreSections: ["Claim"],
                DaVinciProfiles: ["PDex Claim"],
                UscdiDataClasses: uscdiClasses.Distinct().ToList(),
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    // ── Patient (US Core) ────────────────────────────────────────────────────

    private static ComplianceResult ValidatePatient(Patient resource)
    {
        var issues = new List<ComplianceIssue>();
        var warnings = new List<ComplianceWarning>();
        var uscdiClasses = new List<string> { "Patient Demographics" };
        var requiredPresent = 0;
        const int totalRequired = 7;

        if (resource.Identifier is null || resource.Identifier.Count == 0)
            issues.Add(new("error", "MISSING_IDENTIFIER", "Patient must have at least one identifier", Requirement: "US Core Patient"));
        else
            requiredPresent++;

        if (resource.Name is null || resource.Name.Count == 0)
            issues.Add(new("error", "MISSING_NAME", "Patient must have at least one name", Requirement: "US Core Patient"));
        else
            requiredPresent++;

        if (resource.Gender is null)
            issues.Add(new("error", "MISSING_GENDER", "Patient.gender is required", Requirement: "US Core Patient"));
        else
            requiredPresent++;

        if (resource.BirthDate is null)
            warnings.Add(new("MISSING_BIRTHDATE", "Patient.birthDate should be present", "Include birth date for demographic matching"));
        else
            requiredPresent++;

        if (resource.Address is null || resource.Address.Count == 0)
            warnings.Add(new("MISSING_ADDRESS", "Patient should have at least one address", "Include address for coordination of care"));
        else
            requiredPresent++;

        if (resource.Telecom is null || resource.Telecom.Count == 0)
            warnings.Add(new("MISSING_TELECOM", "Patient should have contact information", "Include phone or email"));
        else
            requiredPresent++;

        var hasUSCoreProfile = resource.Meta?.Profile?.Any(
            p => p.Contains("us-core-patient", StringComparison.OrdinalIgnoreCase)) ?? false;

        if (!hasUSCoreProfile)
            warnings.Add(new("MISSING_US_CORE_PROFILE", "Patient should declare US Core profile", "Add http://hl7.org/fhir/us/core/StructureDefinition/us-core-patient to meta.profile"));
        else
            requiredPresent++;

        return new ComplianceResult(
            Compliant: issues.All(i => i.Severity != "error"),
            Issues: issues,
            Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: "Patient",
                RequiredElementsPresent: requiredPresent,
                TotalRequiredElements: totalRequired,
                UsCoreSections: ["US Core Patient"],
                DaVinciProfiles: ["PDex Patient"],
                UscdiDataClasses: uscdiClasses,
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }

    // ── Timeline ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Check prior authorization timeline compliance.
    /// CMS-0057-F requires response within specific timeframes:
    /// 72 hours for urgent/stat, 7 calendar days for standard.
    /// Urgency is determined by ServiceRequest.priority, not elapsed time.
    /// </summary>
    private static TimelineCompliance CheckPriorAuthTimeline(string? authoredOn, bool isUrgent)
    {
        if (authoredOn is null || !DateTimeOffset.TryParse(authoredOn, out var authoredDate))
            return new TimelineCompliance(Applicable: false);

        var hoursDiff = (DateTimeOffset.UtcNow - authoredDate).TotalHours;

        var deadline = isUrgent ? "72 hours" : "7 calendar days";
        var maxAllowedHours = isUrgent ? 72.0 : 168.0;
        var compliant = hoursDiff <= maxAllowedHours;

        return new TimelineCompliance(
            Applicable: true,
            Requirement: $"CMS-0057-F: Response within {deadline} for {(isUrgent ? "urgent" : "standard")} requests",
            Deadline: deadline,
            Compliant: compliant);
    }

    // ── Unsupported resource fallback ────────────────────────────────────────

    private static ComplianceResult BuildUnsupportedResult(Resource resource)
    {
        var warnings = new List<ComplianceWarning>
        {
            new("UNSUPPORTED_RESOURCE",
                $"Resource type {resource.TypeName} is not specifically validated for CMS-0057-F",
                "Submit a supported resource type: ServiceRequest, ExplanationOfBenefit, Claim, or Patient")
        };

        return new ComplianceResult(
            Compliant: true,
            Issues: [],
            Warnings: warnings,
            Summary: new ComplianceSummary(
                ResourceType: resource.TypeName ?? "Unknown",
                RequiredElementsPresent: 0,
                TotalRequiredElements: 0,
                UsCoreSections: [],
                DaVinciProfiles: [],
                UscdiDataClasses: [],
                TimelineCompliance: new TimelineCompliance(Applicable: false)));
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record ComplianceResult(
    bool Compliant,
    IReadOnlyList<ComplianceIssue> Issues,
    IReadOnlyList<ComplianceWarning> Warnings,
    ComplianceSummary Summary);

public record ComplianceIssue(
    string Severity,
    string Code,
    string Message,
    string? Location = null,
    string? Requirement = null);

public record ComplianceWarning(
    string Code,
    string Message,
    string? Recommendation = null);

public record ComplianceSummary(
    string ResourceType,
    int RequiredElementsPresent,
    int TotalRequiredElements,
    IReadOnlyList<string> UsCoreSections,
    IReadOnlyList<string> DaVinciProfiles,
    IReadOnlyList<string> UscdiDataClasses,
    TimelineCompliance TimelineCompliance);

public record TimelineCompliance(
    bool Applicable,
    string? Requirement = null,
    string? Deadline = null,
    bool? Compliant = null);
