using FluentAssertions;
using FhirService.Models;
using FhirService.Services;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class PasResponseBuilderTests
{
    [Fact]
    public void InquiryAndSubmit_ProjectCommittedStableLineOutcomesAndReasons()
    {
        var lines = new List<PriorAuthorizationService>
        {
            new() { LineNumber = 7, ProcedureCode = "72148", RequestedUnits = 4, ApprovedUnits = 2,
                ServiceStatus = "A2", DenialReasonCode = "LIMIT", DenialReason = "Only two units meet criteria." },
            new() { LineNumber = 12, ProcedureCode = "72148", RequestedUnits = 2, ApprovedUnits = 0,
                ServiceStatus = "A3", DenialReasonCode = "NOTMEDNEC", DenialReason = "Second service is not medically necessary." },
        };
        var builder = new PasResponseBuilder();
        var inquiry = builder.BuildInquiryResponse(new PriorAuthorizationRecord
        {
            AuthorizationNumber = "AUTH-mixed", Status = PriorAuthorizationStatus.Modified,
            ReviewDecision = "A2", RequestedServices = lines,
        });
        var submitted = builder.BuildApprovedResponse(new Claim(), new PasDecisionResult(), lines);
        foreach (var bundle in new[] { inquiry, submitted })
        {
            var response = bundle.Entry.Select(e => e.Resource).OfType<ClaimResponse>().Single();
            response.Item.Select(i => i.ItemSequence).Should().Equal(7, 12);
            for (var i = 0; i < lines.Count; i++)
            {
                var outcome = response.Item[i].Adjudication.Single(a => a.Extension.Count > 0);
                var code = (Coding)outcome.Extension.Single().Extension.Single().Value;
                code.Code.Should().Be(lines[i].ServiceStatus);
                outcome.Reason!.Text.Should().Be(lines[i].DenialReason);
                outcome.Reason!.Coding.Single().Code.Should().Be(lines[i].DenialReasonCode);
            }
        }
    }

    private readonly PasResponseBuilder _builder = new();
    private readonly Claim _claim;

    public PasResponseBuilderTests()
    {
        _claim = new Claim
        {
            Id = "test-claim-001",
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Preauthorization,
            Patient = new ResourceReference("Patient/pat-001"),
            Created = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Insurer = new ResourceReference("Organization/cho-payer"),
        };
    }

    [Fact]
    public void BuildApproved_ContainsAuthNumber_InPreAuthRef()
    {
        var decision = new PasDecisionResult
        {
            HasDecision = true,
            Decision = "approved",
            AuthorizationNumber = "PAS-20260331-ABCD1234",
            EffectiveFrom = DateTime.UtcNow.Date,
            EffectiveTo = DateTime.UtcNow.Date.AddYears(1),
            RuleName = "AutoApproveList",
        };

        var bundle = _builder.BuildApprovedResponse(_claim, decision);
        var claimResponse = bundle.Entry.First().Resource as ClaimResponse;

        claimResponse.Should().NotBeNull();
        claimResponse!.PreAuthRef.Should().Be("PAS-20260331-ABCD1234");
        claimResponse.Disposition.Should().Be("approved");
        claimResponse.Outcome.Should().Be(ClaimProcessingCodes.Complete);
    }

    [Fact]
    public void BuildDenied_ContainsDenialReason_InError()
    {
        var decision = new PasDecisionResult
        {
            HasDecision = true,
            Decision = "denied",
            DenialReasonCode = "NOT_COVERED",
            DenialReason = "Service V2020 is not a covered benefit",
            RuleName = "AutoDenyList",
        };

        var bundle = _builder.BuildDeniedResponse(_claim, decision);
        var claimResponse = bundle.Entry.First().Resource as ClaimResponse;

        claimResponse.Should().NotBeNull();
        claimResponse!.Disposition.Should().Be("denied");
        claimResponse.Outcome.Should().Be(ClaimProcessingCodes.Complete);
        // The denial is coded as the certification action A3 and explained in
        // prose. It is NOT a ClaimResponse.error: that element binds required to
        // X12 list 901 and means the request was rejected, not that the service
        // was denied. See PasResponseBuilder.DenialNote.
        claimResponse.Error.Should().BeEmpty();
        claimResponse.Extension.Should().ContainSingle(e =>
            e.Url == "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction")
            .Which.Extension.Should().ContainSingle().Which.Value.Should().BeOfType<Coding>()
            .Which.Code.Should().Be("A3");
        claimResponse.ProcessNote.Should().ContainSingle()
            .Which.Text.Should().Contain("NOT_COVERED")
            .And.Contain("Service V2020 is not a covered benefit");
    }

    [Fact]
    public void BuildPended_ContainsReviewActionCode_A4()
    {
        var bundle = _builder.BuildPendedResponse(_claim);
        var claimResponse = bundle.Entry.First().Resource as ClaimResponse;

        claimResponse.Should().NotBeNull();
        claimResponse!.Outcome.Should().Be(ClaimProcessingCodes.Queued);
        claimResponse.Disposition.Should().Be("pended");

        // Check for reviewAction extension with A4 code
        var reviewAction = claimResponse.Extension
            .FirstOrDefault(e => e.Url.Contains("reviewAction"));
        reviewAction.Should().NotBeNull();

        var codeExt = reviewAction!.Extension
            .FirstOrDefault(e => e.Url.Contains("reviewActionCode"));
        codeExt.Should().NotBeNull();

        var coding = codeExt!.Value as Coding;
        coding.Should().NotBeNull();
        coding!.Code.Should().Be("A4");
    }

    [Fact]
    public void AllResponses_ArePasConformantBundles()
    {
        var approveDecision = new PasDecisionResult
        {
            HasDecision = true,
            Decision = "approved",
            AuthorizationNumber = "AUTH-001",
        };
        var denyDecision = new PasDecisionResult
        {
            HasDecision = true,
            Decision = "denied",
            DenialReasonCode = "DENIED",
            DenialReason = "Denied",
        };

        var bundles = new[]
        {
            _builder.BuildApprovedResponse(_claim, approveDecision),
            _builder.BuildDeniedResponse(_claim, denyDecision),
            _builder.BuildPendedResponse(_claim),
        };

        foreach (var bundle in bundles)
        {
            bundle.Type.Should().Be(Bundle.BundleType.Collection);
            bundle.Entry.Should().HaveCount(1);
            bundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>();

            var cr = (ClaimResponse)bundle.Entry[0].Resource;
            cr.Meta.Profile.Should().Contain(
                "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-claimresponse");
            cr.Use.Should().Be(ClaimUseCode.Preauthorization);
            cr.Status.Should().Be(FinancialResourceStatusCodes.Active);
            cr.Patient.Should().NotBeNull();
        }
    }
}
