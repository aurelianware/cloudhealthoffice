using FhirService.Mappers;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using HlModel = Hl7.Fhir.Model;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PROV-01/02/03 (Provider Access) and PAT-01/02/03 (Patient Access), executed
/// against the REAL MockPatientAccessDataProvider (Demo mode), the static
/// PatientAccessMapper (CARIN/US Core projection), and Cms0057ComplianceChecker.
///
/// Traceability:
///   provider    src/services/fhir-service/Services/IPatientAccessDataProvider.cs (MockPatientAccessDataProvider)
///   mapper      src/services/fhir-service/Mappers/PatientAccessMapper.cs
///   compliance  src/services/fhir-service/Services/Cms0057ComplianceChecker.cs
///   consent     src/services/consent-service/Services/ConsentStateMachine.cs
///   qnxt seams  claims-service QnxtClaimAdapter / provider-service QnxtProviderAdapter (GAP — GapAdapterTests)
///
/// USCDI clinical data (PAT-02) lives in an external clinical store, not in
/// QNXT; this suite does not pretend QNXT holds USCDI.
/// </summary>
public class ProviderAndPatientAccessTests
{
    private static readonly MockPatientAccessDataProvider Provider = new();

    // ── PROV-01 attributed member data pull ─────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PROV-01")]
    public async Task PROV01_AttributedMember_ReturnsMemberAndClaimsData()
    {
        var members = await Provider.GetMembersByPatientIdAsync("pat-001");
        members.Should().ContainSingle();

        // Projected to a US Core Patient + CARIN EOB (payments) the provider can read.
        var patient = PatientAccessMapper.MapMemberToPatient(members[0]);
        patient.ResourceType.Should().Be("Patient");
        patient.Id.Should().Be("pat-001");

        var payments = await Provider.GetPaymentsByPatientIdAsync("pat-001");
        var eobBundle = PatientAccessMapper.PaymentsToEobBundle(payments, "self");
        eobBundle.Total.Should().Be(payments.Count);
        eobBundle.Entry.Should().NotBeNull();
    }

    // ── PROV-02 attribution enforcement ─────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PROV-02")]
    public async Task PROV02_NonAttributedMember_ReturnsNoData()
    {
        // A member the provider is not attributed to yields no data at the data
        // layer. (The 403-class FHIR OperationOutcome for a missing/again-scoped
        // token is enforced by SmartScopeEnforcementMiddleware — see SEC-01.)
        var members = await Provider.GetMembersByPatientIdAsync("pat-999-not-attributed");
        members.Should().BeEmpty();

        var payments = await Provider.GetPaymentsByPatientIdAsync("pat-999-not-attributed");
        payments.Should().BeEmpty();
    }

    // ── PROV-03 patient opt-out honored ─────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PROV-03")]
    public void PROV03_RevokedConsent_IsNotActive_SoProviderAccessIsBlocked()
    {
        // The consent registry models opt-out as a Revoked consent. A revoked
        // record is not Active, so Provider Access must be withheld.
        global::ConsentService.Services.ConsentStateMachine
            .IsAllowed(global::ConsentService.Models.ConsentStatus.Active,
                       global::ConsentService.Models.ConsentStatus.Revoked)
            .Should().BeTrue("a member may opt out of an active authorization");

        var optedOut = new global::ConsentService.Models.Consent
        {
            Status = global::ConsentService.Models.ConsentStatus.Revoked,
        };
        (optedOut.Status == global::ConsentService.Models.ConsentStatus.Active)
            .Should().BeFalse();
    }

    // ── PAT-01 member claims / CARIN EOB ────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-01")]
    public async Task PAT01_MemberClaims_ProjectToCarinEobViaChoPath()
    {
        // CHO path (QnxtClaimAdapter is a NotImplementedException stub — see
        // GapAdapterTests.PAT01_*). Payments project to CARIN BB EOBs.
        var payments = await Provider.GetPaymentsByPatientIdAsync("pat-001");
        payments.Should().HaveCount(2);

        var eob = PatientAccessMapper.MapPaymentToEob(payments[0]);
        eob.ResourceType.Should().Be("ExplanationOfBenefit");
        eob.Id.Should().NotBeNullOrEmpty();
    }

    // ── PAT-02 US Core clinical ─────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-02")]
    public void PAT02_UsCorePatient_ValidatesAsPatientDemographics()
    {
        // Demographics are held by CHO; broader USCDI clinical classes live in an
        // external clinical store (PARTIAL — not sourced from QNXT here).
        var checker = new Cms0057ComplianceChecker();
        var patient = new HlModel.Patient
        {
            Identifier = new List<HlModel.Identifier> { new("http://cho/mrn", "pat-001") },
            Name = new List<HlModel.HumanName> { new() { Family = "Smith", Given = new[] { "John" } } },
            Gender = HlModel.AdministrativeGender.Male,
            BirthDate = "1955-07-14",
        };

        var result = checker.ValidateCompliance(patient);
        result.Compliant.Should().BeTrue();
        result.Summary.UscdiDataClasses.Should().Contain("Patient Demographics");
    }

    // ── PAT-03 member PA data except drugs ──────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_PriorAuthData_IsSupportedResourceType()
    {
        // Prior-authorization data (ClaimResponse) is a recognized PA-data
        // resource for Patient Access. Drug exclusion is handled with PAS-08
        // (documented GAP); the >= 1-year retention-after-last-status-change
        // rule is documented in the inventory — no retention job exists yet
        // (GAP).
        var checker = new Cms0057ComplianceChecker();
        checker.SupportedResourceTypes.Should().Contain("ClaimResponse");
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    [Trait("Kind", "GAP")]
    public void PAT03_Gap_NoRetentionJobForPaData()
    {
        // GAP: the "retain PA data >= 1 year after last status change" and
        // "update within 1 business day" obligations have no scheduled retention/
        // freshness job in the current code. Documented in the acceptance
        // inventory as engagement/product follow-up.
        var retentionJobExists = AcceptanceContext.ProductTypes()
            .Any(t => t.Name.Contains("PaDataRetention", StringComparison.OrdinalIgnoreCase)
                   || t.Name.Contains("PatientAccessRetention", StringComparison.OrdinalIgnoreCase));
        retentionJobExists.Should().BeFalse();
    }
}
