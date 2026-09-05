using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Services.BenefitExclusion;
using Cms0057Acceptance.Tests.TestSupport;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Options;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-08 — drug exclusion enforced as REAL Cloud Health Office Replace-mode
/// product capability. A request for a drug/service the member's applicable
/// benefit plan explicitly excludes must not follow the ordinary approvable
/// prior-authorization path: the CHO-native workflow records a coded denial and
/// persists it, with an auditable history.
///
/// These scenarios exercise the SAME production classes the running service
/// binds — <see cref="ChoAuthorizationBackend"/>, the configuration-driven
/// <see cref="ConfiguredBenefitExclusionCatalog"/>,
/// <see cref="DrugExclusionEvaluator"/>, and
/// <see cref="AuthorizationExclusionService"/> — over an in-memory repository
/// fixture. Synthetic data only; no PHI, no real formulary.
///
/// Traceability:
///   model      src/services/authorization-service/Models/BenefitExclusion.cs
///   catalog    src/services/authorization-service/Services/BenefitExclusion/BenefitExclusionCatalog.cs
///   evaluator  src/services/authorization-service/Services/BenefitExclusion/DrugExclusionEvaluator.cs
///   backend    src/services/authorization-service/Backends/ChoAuthorizationBackend.cs (CreateAsync)
///   pas map    src/services/fhir-service/Services/PasResponseBuilder.cs (BuildDeniedResponse)
/// </summary>
public class DrugExclusionTests
{
    // Synthetic, obviously-fake identifiers — not a real NDC or member.
    private const string ExcludedNdc = "12345-6789-01";
    private const string NonExcludedNdc = "99999-0000-11";
    private const string ExcludedJCode = "J9999";
    private const string DemoCoverage = "cov-001";

    private static ChoAuthorizationBackend BackendWith(params BenefitPlanExclusionSet[] sets)
    {
        var catalog = new ConfiguredBenefitExclusionCatalog(
            Options.Create(new BenefitExclusionOptions { PlanExclusionSets = sets.ToList() }));
        var service = new AuthorizationExclusionService(catalog, new DrugExclusionEvaluator());
        return new ChoAuthorizationBackend(new InMemoryAuthorizationRepository(), service);
    }

    private static BenefitPlanExclusionSet DemoPlanExcluding(params BenefitExclusion[] exclusions) => new()
    {
        TenantId = AcceptanceContext.TenantId,
        LineOfBusiness = LineOfBusiness.Medicaid,
        CoverageId = DemoCoverage,
        PlanId = "demo-medicaid-plan",
        Exclusions = exclusions.ToList(),
    };

    private static Authorization DrugRequest(
        string number, string code, string? system = "NDC", string serviceTypeCode = "88",
        string? coverageId = DemoCoverage) => new()
    {
        TenantId = AcceptanceContext.TenantId,
        AuthorizationNumber = number,
        Id = Guid.NewGuid().ToString(),
        MemberId = "MBR-pat-001",
        CoverageId = coverageId,
        LineOfBusiness = LineOfBusiness.Medicaid,
        AuthorizationType = AuthorizationType.PreAuthorization,
        ServiceTypeCode = serviceTypeCode,
        RequestingProviderNPI = "1234567890",
        ServicingProviderNPI = "1987654321",
        PatientFirstName = "Pat",
        PatientLastName = "Synthetic",
        PatientDateOfBirth = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateFrom = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
        Status = AuthorizationStatus.Submitted,
        SubmittedDate = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        RequestedServices =
        {
            new RequestedService { ProcedureCode = code, ProductOrServiceSystem = system, RequestedUnits = 1 },
        },
    };

    private static BenefitExclusion NdcExclusion(string ndc) => new()
    {
        CodeSystem = DrugServiceCodeSystem.Ndc,
        Code = ndc,
        Category = ExclusionCategory.NonCoveredService,
        ReasonCode = ExclusionReasonCode.NonCoveredBenefit,
        ReasonText = "Drug is not a covered benefit under the member's plan.",
    };

    // ── Core: excluded drug is denied and persisted with a coded reason ─────────

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_ExcludedDrug_IsDeniedAndPersistedWithCodedReason()
    {
        var backend = BackendWith(DemoPlanExcluding(NdcExclusion(ExcludedNdc)));

        await backend.CreateAsync(DrugRequest("PAS-08-EXCLUDED", ExcludedNdc));

        var persisted = await backend.GetByNumberAsync("PAS-08-EXCLUDED");
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(AuthorizationStatus.Denied, "an excluded drug cannot be approvable");
        persisted.ReviewDecision.Should().Be("A3"); // 278 UM06 Denied
        persisted.DenialReasonCode.Should().Be(ExclusionReasonCode.NonCoveredBenefit);
        persisted.DenialReason.Should().NotBeNullOrWhiteSpace();
        persisted.ReviewedDate.Should().NotBeNull();

        // Auditable trail: received, then denied.
        persisted.StatusHistory.Should().HaveCount(2);
        persisted.StatusHistory[0].Status.Should().Be(AuthorizationStatus.Submitted);
        persisted.StatusHistory[1].Status.Should().Be(AuthorizationStatus.Denied);
        persisted.StatusHistory[1].ReviewDecision.Should().Be("A3");
        persisted.StatusHistory[1].Reason.Should().NotBeNullOrWhiteSpace();
    }

    // ── Comparator: a non-excluded drug is NOT denied (guards "deny all") ───────

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_NonExcludedDrug_FollowsOrdinaryPathNotDenied()
    {
        var backend = BackendWith(DemoPlanExcluding(NdcExclusion(ExcludedNdc)));

        await backend.CreateAsync(DrugRequest("PAS-08-OK", NonExcludedNdc));

        var persisted = await backend.GetByNumberAsync("PAS-08-OK");
        persisted!.Status.Should().Be(AuthorizationStatus.Submitted,
            "the new exclusion logic must not deny drugs the plan does not exclude");
        persisted.DenialReasonCode.Should().BeNull();
        persisted.StatusHistory.Should().ContainSingle().Which.Status.Should().Be(AuthorizationStatus.Submitted);
    }

    // ── Boundary cases ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_NoPlanExclusionsConfigured_NothingIsExcluded()
    {
        var backend = BackendWith(); // empty catalog

        await backend.CreateAsync(DrugRequest("PAS-08-NOCAT", ExcludedNdc));

        (await backend.GetByNumberAsync("PAS-08-NOCAT"))!.Status
            .Should().Be(AuthorizationStatus.Submitted);
    }

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_ExclusionScopedToOtherCoverage_DoesNotApply()
    {
        var backend = BackendWith(DemoPlanExcluding(NdcExclusion(ExcludedNdc)));

        // Request carries no active coverage → the demo plan set (scoped to a
        // coverage) does not apply, so nothing is excluded.
        await backend.CreateAsync(DrugRequest("PAS-08-NOCOV", ExcludedNdc, coverageId: null));

        (await backend.GetByNumberAsync("PAS-08-NOCOV"))!.Status
            .Should().Be(AuthorizationStatus.Submitted);
    }

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_ExcludedNdc_MatchesDespiteHyphenAndCaseFormatting()
    {
        // Exclusion stored unhyphenated + lower request hyphenated: normalization
        // must still match the same drug.
        var backend = BackendWith(DemoPlanExcluding(new BenefitExclusion
        {
            CodeSystem = DrugServiceCodeSystem.Ndc,
            Code = "12345678901", // no hyphens
            ReasonCode = ExclusionReasonCode.NonCoveredBenefit,
        }));

        await backend.CreateAsync(DrugRequest("PAS-08-FMT", "12345-6789-01"));

        (await backend.GetByNumberAsync("PAS-08-FMT"))!.Status
            .Should().Be(AuthorizationStatus.Denied);
    }

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_UnknownCode_IsNotExcluded()
    {
        var backend = BackendWith(DemoPlanExcluding(NdcExclusion(ExcludedNdc)));

        await backend.CreateAsync(DrugRequest("PAS-08-UNK", "not-a-real-code", system: "NDC"));

        (await backend.GetByNumberAsync("PAS-08-UNK"))!.Status
            .Should().Be(AuthorizationStatus.Submitted);
    }

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_MultipleExclusions_MatchTheApplicableOne()
    {
        var backend = BackendWith(DemoPlanExcluding(
            NdcExclusion(ExcludedNdc),
            new BenefitExclusion
            {
                CodeSystem = DrugServiceCodeSystem.Hcpcs, Code = ExcludedJCode,
                ReasonCode = ExclusionReasonCode.NonCoveredBenefit,
            }));

        await backend.CreateAsync(DrugRequest("PAS-08-JCODE", ExcludedJCode, system: "HCPCS"));

        var persisted = await backend.GetByNumberAsync("PAS-08-JCODE");
        persisted!.Status.Should().Be(AuthorizationStatus.Denied);
        persisted.DenialReasonCode.Should().Be(ExclusionReasonCode.NonCoveredBenefit);
    }

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_PharmacyServiceType_ExcludedFromMedicalScope()
    {
        // A plan may exclude the whole pharmacy service type (278 UM03 = 88) from
        // the CMS-0057-F medical PA scope, independent of the specific drug code.
        var backend = BackendWith(DemoPlanExcluding(new BenefitExclusion
        {
            CodeSystem = DrugServiceCodeSystem.ServiceType,
            Code = "88",
            Category = ExclusionCategory.PharmacyDrug,
            ReasonCode = ExclusionReasonCode.DrugExcludedFromMedicalScope,
            ReasonText = "Pharmacy prior authorization is out of the CMS-0057-F medical scope.",
        }));

        await backend.CreateAsync(DrugRequest("PAS-08-RXSCOPE", NonExcludedNdc, serviceTypeCode: "88"));

        var persisted = await backend.GetByNumberAsync("PAS-08-RXSCOPE");
        persisted!.Status.Should().Be(AuthorizationStatus.Denied);
        persisted.DenialReasonCode.Should().Be(ExclusionReasonCode.DrugExcludedFromMedicalScope);
    }

    // ── PAS response mapping: the domain denial maps to the standards response ──

    [Fact]
    [Trait("Scenario", "PAS-08")]
    [Trait("Backend", "Replace")]
    public async Task PAS08_Replace_ExclusionDenial_MapsToPasDeniedClaimResponse()
    {
        var backend = BackendWith(DemoPlanExcluding(NdcExclusion(ExcludedNdc)));
        await backend.CreateAsync(DrugRequest("PAS-08-MAP", ExcludedNdc));
        var denied = await backend.GetByNumberAsync("PAS-08-MAP");

        // The persisted domain decision projects onto the existing PAS response
        // builder (no PAS-controller special-casing).
        var builder = new PasResponseBuilder();
        var claim = new Claim { Id = "c-pas-08", Patient = new ResourceReference("Patient/pat-001") };
        var bundle = builder.BuildDeniedResponse(claim, new PasDecisionResult
        {
            HasDecision = true,
            Decision = "denied",
            DenialReasonCode = denied!.DenialReasonCode,
            DenialReason = denied.DenialReason,
        });

        var claimResponse = bundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        claimResponse.Disposition.Should().Be("denied");
        claimResponse.Error.Should().ContainSingle()
            .Which.Code.Coding.Should().ContainSingle()
            .Which.Code.Should().Be(ExclusionReasonCode.NonCoveredBenefit);
    }
}
