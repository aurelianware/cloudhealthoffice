using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-03 (submit), PAS-05 (specific denial reason), PAS-06 (decision
/// timeframe) — Da Vinci PAS Claim/$submit, executed against the REAL
/// PasController + PasResponseBuilder + Cms0057ComplianceChecker in Demo/Cho
/// mode. The auto-adjudicator is mocked so each decision branch is exercised
/// deterministically; the adjudicator itself is covered by
/// tests/CloudHealthOffice.FhirService.Tests/Services/PasAutoAdjudicatorTests.cs.
///
/// Traceability:
///   controller  src/services/fhir-service/Controllers/PasController.cs
///   builder     src/services/fhir-service/Services/PasResponseBuilder.cs
///   compliance  src/services/fhir-service/Services/Cms0057ComplianceChecker.cs
///   service     authorization-service (persistence target; PAS-04 status)
///   qnxt seam   src/services/authorization-service/Adapters/QnxtAuthorizationAdapter.cs (GAP — GapAdapterTests.PAS03_*)
///
/// Varies: YES. Persisting the created authorization into a QNXT system of
/// record is engagement work; that seam is a NotImplementedException stub
/// asserted in GapAdapterTests. In Replace mode the authorization is persisted
/// through authorization-service's internal automated-decision path (D-01,
/// D-02). Here that path runs in process (<see cref="InProcessAuthorizationService"/>:
/// the real AutomatedDecisionsController and ChoAuthorizationBackend over an
/// in-memory repository). It used to be stubbed as unreachable while the
/// controller answered regardless; $submit now fails when the record is not
/// stored, so the tests assert the stored record instead.
/// </summary>
[Trait("Backend", "Replace")]
public class PasSubmitTests
{
    private const string CptSystem = "http://www.ama-assn.org/go/cpt";

    private readonly InProcessAuthorizationService _authorizationService = new();

    private PasController BuildController(Mock<IPasAutoAdjudicator> adjudicator, bool compliant = true)
    {
        var compliance = new Mock<ICms0057ComplianceChecker>();
        // Use the REAL compliance checker semantics by delegating to it, so the
        // controller's compliance gate is genuinely exercised.
        var realChecker = new Cms0057ComplianceChecker();
        compliance.Setup(c => c.ValidateCompliance(It.IsAny<Resource>()))
            .Returns<Resource>(r => compliant
                ? realChecker.ValidateCompliance(r)
                : new ComplianceResult(false,
                    new[] { new ComplianceIssue("error", "MISSING_ELEMENT", "Missing required element") },
                    Array.Empty<ComplianceWarning>(),
                    new ComplianceSummary("Claim", 0, 10, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>(), new TimelineCompliance(false))));

        var config = Options.Create(new PasAutoAdjudicationConfig { Enabled = true, MaxResponseMs = 12000 });

        return new PasController(
            adjudicator.Object,
            new PasResponseBuilder(),
            compliance.Object,
            // These suites exercise $submit; $inquire has its own coverage.
            new Mock<IPriorAuthorizationInquiryService>().Object,
            config,
            // Provider verification: unreachable, so it fails open.
            new StubHttpClientFactory(),
            _authorizationService,
            AcceptanceContext.Logger<PasController>())
            .WithTenant();
    }

    /// <summary>
    /// A stand-in adjudicator that, like the real one, reports the line of
    /// business it decided on (the basis it was given), so the record's
    /// decision basis is accepted by authorization-service.
    /// </summary>
    private static Mock<IPasAutoAdjudicator> Adjudicator(PasDecisionResult decision)
    {
        var m = new Mock<IPasAutoAdjudicator>();
        m.Setup(a => a.TryDecideAsync(
                It.IsAny<Claim>(), It.IsAny<Bundle>(), It.IsAny<string>(), It.IsAny<PasDecisionBasis>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Claim _, Bundle _, string _, PasDecisionBasis basis, int _, CancellationToken _) =>
            {
                decision.LineOfBusiness ??= basis.LineOfBusiness.ToString();
                return decision;
            });
        return m;
    }

    private static Bundle RequestBundle(string cpt = "27447", string priority = "normal")
    {
        var claim = new Claim
        {
            Id = Guid.NewGuid().ToString(),
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference("Patient/pat-001"),
            Created = "2026-09-01", // fixed for deterministic, reproducible bundles
            Insurer = new ResourceReference("Organization/cho-payer"),
            Provider = new ResourceReference("Practitioner/npi-1234567890"),
            Priority = new CodeableConcept("http://terminology.hl7.org/CodeSystem/processpriority", priority),
            BillablePeriod = new Period { Start = "2026-09-10", End = "2026-09-20" }, // requested date span
            Insurance = new List<Claim.InsuranceComponent>
            {
                new() { Sequence = 1, Focal = true, Coverage = new ResourceReference("Coverage/cov-001") }
            },
            Item = new List<Claim.ItemComponent>
            {
                new()
                {
                    Sequence = 1,
                    ProductOrService = new CodeableConcept(CptSystem, cpt),
                    Quantity = new Quantity(2, "units"), // requested units
                }
            },
        };

        // The resources the Claim references. $submit maps the record from
        // them (D-03) and refuses a Claim whose Patient, Coverage or
        // requesting-provider NPI is missing, instead of storing placeholders.
        claim.Provider = new ResourceReference("Practitioner/npi-1234567890");
        var patient = new Patient
        {
            Id = "pat-001",
            Identifier = [new Identifier("http://example.org/member-id", "MBR-pat-001")
            {
                Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/v2-0203", "MB"),
            }],
            Name = [new HumanName { Family = "Synthetic", Given = ["Pat"] }],
            BirthDate = "1980-01-01",
        };
        var coverage = new Coverage
        {
            Id = "cov-001",
            Status = FinancialResourceStatusCodes.Active,
            Beneficiary = new ResourceReference("Patient/pat-001"),
            Payor = [new ResourceReference("Organization/cho-payer")],
            Type = new CodeableConcept("https://codesystem.x12.org/005010/1032", "MC"),
        };
        var practitioner = new Practitioner
        {
            Id = "npi-1234567890",
            Identifier = [new Identifier("http://hl7.org/fhir/sid/us-npi", "1234567890")],
        };

        return new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = new List<Bundle.EntryComponent>
            {
                new() { Resource = claim },
                new() { Resource = patient },
                new() { Resource = coverage },
                new() { Resource = practitioner },
            },
        };
    }

    // ── PAS-03 submit ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_ApprovedRequest_ReturnsClaimResponseWithTrackingId()
    {
        var controller = BuildController(Adjudicator(new PasDecisionResult
        {
            HasDecision = true,
            Decision = "approved",
            AuthorizationNumber = "PAS-ACC-0003",
            EffectiveFrom = new DateTime(2026, 9, 10),
            EffectiveTo = new DateTime(2026, 9, 20),
            RuleName = "AutoApproveList",
        }));

        var result = await controller.ClaimSubmit(RequestBundle());

        var bundle = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<Bundle>().Subject;
        var claimResponse = bundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;

        claimResponse.Disposition.Should().Be("approved");
        claimResponse.PreAuthRef.Should().Be("PAS-ACC-0003");           // tracking id
        claimResponse.Use.Should().Be(ClaimUseCode.Preauthorization);
        claimResponse.PreAuthPeriod!.Start.Should().Be("2026-09-10");   // decided date span
        claimResponse.Meta!.Profile.Should().Contain(
            "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse");

        // The tracking id names a durable record holding the decision (D-01, D-02).
        var stored = await _authorizationService.Repository.GetByAuthorizationNumberAsync("PAS-ACC-0003");
        stored.Should().NotBeNull();
        stored!.TenantId.Should().Be(AcceptanceContext.TenantId);
        stored.Status.Should().Be(AuthorizationService.Models.AuthorizationStatus.Approved);
        stored.ReviewDecision.Should().Be("A1");
        stored.MemberId.Should().Be("MBR-pat-001");
        stored.LineOfBusiness.Should().Be(AuthorizationService.Models.LineOfBusiness.Medicaid);
        stored.RequestingProviderNPI.Should().Be("1234567890");
        stored.CreatedBy.Should().Be("fhir-service");
        stored.StatusHistory.Select(h => h.Status).Should().Equal(
            AuthorizationService.Models.AuthorizationStatus.Submitted,
            AuthorizationService.Models.AuthorizationStatus.Approved);
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_Negative_RecordNotStored_NoClaimResponseAndNoTrackingId()
    {
        _authorizationService.Unavailable = true;
        var controller = BuildController(Adjudicator(new PasDecisionResult
        {
            HasDecision = true, Decision = "approved", AuthorizationNumber = "PAS-ACC-LOST", RuleName = "AutoApproveList",
        }));

        var result = await controller.ClaimSubmit(RequestBundle());

        var failed = result.Should().BeOfType<ObjectResult>().Subject;
        failed.StatusCode.Should().Be(503);
        failed.Value.Should().BeOfType<OperationOutcome>();
        System.Text.Json.JsonSerializer.Serialize(failed.Value).Should().NotContain("PAS-ACC-LOST");
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_Negative_MissingPatient_Returns422_NothingStored()
    {
        var controller = BuildController(Adjudicator(new PasDecisionResult { HasDecision = true, Decision = "approved" }));
        var bundle = RequestBundle();
        bundle.Entry.RemoveAll(e => e.Resource is Patient);

        var result = await controller.ClaimSubmit(bundle);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _authorizationService.Repository.CreateCount.Should().Be(0);
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public async Task PAS04_InquireFindsTheAuthorizationSubmitIssued()
    {
        var submit = BuildController(Adjudicator(new PasDecisionResult
        {
            HasDecision = true, Decision = "denied", DenialReasonCode = "NOT_COVERED",
            DenialReason = "Service 27447 is not a covered benefit", RuleName = "AutoDenyList",
        }));
        var submitted = ((Bundle)((OkObjectResult)await submit.ClaimSubmit(RequestBundle())).Value!)
            .Entry[0].Resource as ClaimResponse;

        var inquire = new PasController(
                new Mock<IPasAutoAdjudicator>().Object,
                new PasResponseBuilder(),
                new Cms0057ComplianceChecker(),
                new PriorAuthorizationInquiryService(_authorizationService),
                Options.Create(new PasAutoAdjudicationConfig()),
                new StubHttpClientFactory(),
                _authorizationService,
                AcceptanceContext.Logger<PasController>())
            .WithTenant();
        var inquiryClaim = new Claim
        {
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference("Patient/pat-001"),
            Identifier = [new Identifier("http://example.org/pas", submitted!.PreAuthRef)],
        };

        var result = await inquire.ClaimInquire(new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = [new() { Resource = inquiryClaim }],
        });

        var answer = ((Bundle)result.Should().BeOfType<OkObjectResult>().Subject.Value!)
            .Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        answer.PreAuthRef.Should().Be(submitted.PreAuthRef);
        answer.Disposition.Should().Be("denied");
        answer.Patient.Reference.Should().Be("Patient/pat-001");
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_ComplexRequest_PendsWithTrackableResponse()
    {
        var controller = BuildController(Adjudicator(new PasDecisionResult
        {
            HasDecision = false, RuleName = "NoRuleMatch",
        }));

        var result = await controller.ClaimSubmit(RequestBundle());

        var claimResponse = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<Bundle>().Subject
            .Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        claimResponse.Disposition.Should().Be("pended");
        claimResponse.Outcome.Should().Be(ClaimProcessingCodes.Queued);
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_Negative_MissingClaim_Returns400()
    {
        var controller = BuildController(Adjudicator(new PasDecisionResult()));

        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = new List<Bundle.EntryComponent> { new() { Resource = new Patient { Id = "pat-001" } } },
        };

        var result = await controller.ClaimSubmit(bundle);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_Negative_IncompleteClaimReferences_Returns400()
    {
        // Missing provider/patient/insurance — attribution/identity plumbing gate.
        var controller = BuildController(Adjudicator(new PasDecisionResult()));

        var claim = new Claim
        {
            Status = FinancialResourceStatusCodes.Active,
            Use = ClaimUseCode.Preauthorization,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
        };
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = new List<Bundle.EntryComponent> { new() { Resource = claim } },
        };

        var result = await controller.ClaimSubmit(bundle);
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    [Trait("Scenario", "PAS-03")]
    public async Task PAS03_Negative_NonCompliantClaim_Returns400()
    {
        var controller = BuildController(Adjudicator(new PasDecisionResult()), compliant: false);

        var result = await controller.ClaimSubmit(RequestBundle());
        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    // ── PAS-05 specific denial reason ───────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-05")]
    public async Task PAS05_DeniedRequest_CarriesCodedReason_NotBareNotMedicallyNecessary()
    {
        var controller = BuildController(Adjudicator(new PasDecisionResult
        {
            HasDecision = true,
            Decision = "denied",
            DenialReasonCode = "X12-A3-278",
            DenialReason = "Requested imaging does not meet ACR appropriateness criteria for this indication.",
            RuleName = "ClinicalCriteria",
        }));

        var result = await controller.ClaimSubmit(RequestBundle());

        var claimResponse = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<Bundle>().Subject
            .Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;

        claimResponse.Disposition.Should().Be("denied");
        var error = claimResponse.Error.Should().ContainSingle().Subject;
        var coding = error.Code!.Coding.Should().ContainSingle().Subject;
        coding.Code.Should().Be("X12-A3-278");
        coding.System.Should().Be("http://terminology.hl7.org/CodeSystem/adjudication-error");
        // The reason must be specific, never a bare "not medically necessary".
        error.Code!.Text.Should().NotBeNullOrWhiteSpace();
        error.Code!.Text!.ToLowerInvariant().Should().NotBe("not medically necessary");
    }

    [Fact]
    [Trait("Scenario", "PAS-05")]
    public async Task PAS05_DenialWithoutExplicitCode_IsNotRecordedOrReturnedAsADecision()
    {
        // A malformed rule decision cannot become a durable, reasonless denial.
        var controller = BuildController(Adjudicator(new PasDecisionResult
        {
            HasDecision = true, Decision = "denied", RuleName = "Fallback",
        }));

        var result = await controller.ClaimSubmit(RequestBundle());

        var failure = result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(422);
        failure.Value.Should().BeOfType<OperationOutcome>();
    }
}
