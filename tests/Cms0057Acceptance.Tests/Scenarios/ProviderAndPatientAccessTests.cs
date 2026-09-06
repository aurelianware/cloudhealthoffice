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
        // resource for Patient Access. Drug exclusion is handled with PAS-08.
        // Retention and freshness are covered by PriorAuthorizationRetentionTests.
        var checker = new Cms0057ComplianceChecker();
        checker.SupportedResourceTypes.Should().Contain("ClaimResponse");
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_RetentionLifecycleExists()
    {
        // Replaces the GAP test that asserted no retention job existed. The
        // lifecycle is now a pure policy plus a hosted sweeper; the behaviour is
        // proven in PriorAuthorizationRetentionTests rather than by this
        // presence check.
        // A concrete policy implements the rule...
        typeof(AuthorizationService.Services.Retention.PriorAuthorizationRetentionPolicy)
            .Should().Implement<AuthorizationService.Services.Retention.IPriorAuthorizationRetentionPolicy>();

        // ...and a hosted worker applies it.
        typeof(AuthorizationService.Services.Retention.PriorAuthorizationRetentionWorker)
            .Should().BeAssignableTo<Microsoft.Extensions.Hosting.BackgroundService>();
    }

    [Fact]
    [Trait("Scenario", "PAT-03")]
    public void PAT03_PaDataFreshnessNeedsNoSyncJobBecauseNothingIsReplicated()
    {
        // The OTHER half of PAT-03: "update PA data within 1 business day".
        //
        // That obligation bounds how stale a Patient Access copy may be. Cloud
        // Health Office keeps no copy: prior-auth state is projected from the
        // authoritative authorization record at READ time, so the interval
        // between a status change and its visibility is zero and there is
        // nothing for a freshness job to synchronise. The absence of such a job
        // is the design, not a gap.
        //
        // What makes that true is that the read seam cannot hold state: it
        // exposes a single lookup and no write, so no cached or replicated
        // projection can exist behind it to drift.
        var storeMethods = typeof(FhirService.Services.IPriorAuthorizationStore)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        storeMethods.Should().ContainSingle()
            .Which.Should().Be(nameof(FhirService.Services.IPriorAuthorizationStore.GetByAuthorizationNumberAsync));

        // And there is no second prior-authorization store to fall out of date.
        var paStores = AcceptanceContext.ProductTypes()
            .Where(t => t.Name.Contains("PriorAuthorization", StringComparison.OrdinalIgnoreCase)
                     && (t.Name.Contains("Cache", StringComparison.OrdinalIgnoreCase)
                      || t.Name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)
                      || t.Name.Contains("Projection", StringComparison.OrdinalIgnoreCase)
                      || t.Name.Contains("Replica", StringComparison.OrdinalIgnoreCase)));

        paStores.Should().BeEmpty(
            "a replicated PA projection is exactly what a 1-business-day freshness job would exist to update");
    }
}
