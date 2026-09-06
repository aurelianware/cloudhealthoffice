using System.Reflection;
using FhirService.Controllers;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace Cms0057Acceptance.Tests.Scenarios;

/// <summary>
/// PAS-04 (inquiry/status), PAS-06 (decision timeframe), PAS-07 (CDex
/// additional-info), PAS-08 (drug exclusion). Executed against the REAL
/// Cms0057ComplianceChecker + PasResponseBuilder in Demo/Cho mode, with honest
/// GAP markers where the corresponding surface is not yet built.
///
/// Traceability:
///   compliance  src/services/fhir-service/Services/Cms0057ComplianceChecker.cs (CheckPriorAuthTimeline)
///   builder     src/services/fhir-service/Services/PasResponseBuilder.cs (BuildPendedResponse — X12 A4)
///   status      src/services/authorization-service/Controllers/AuthorizationsController.cs
///   pas surface src/services/fhir-service/Controllers/PasController.cs (only Claim/$submit)
/// </summary>
public class PasDecisionAndScopeTests
{
    private static ServiceRequest BuildServiceRequest(RequestPriority priority) => new()
    {
        Status = RequestStatus.Active,
        Intent = RequestIntent.Order,
        Subject = new ResourceReference("Patient/pat-001"),
        Requester = new ResourceReference("Practitioner/npi-1234567890"),
        AuthoredOn = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Priority = priority,
        Code = new CodeableConcept("http://www.ama-assn.org/go/cpt", "27447"),
    };

    // ── PAS-06 decision timeframe ───────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-06")]
    public void PAS06_ExpeditedRequest_Tracks72HourClock()
    {
        var checker = new Cms0057ComplianceChecker();

        var result = checker.ValidateCompliance(BuildServiceRequest(RequestPriority.Urgent));

        result.Summary.TimelineCompliance.Applicable.Should().BeTrue();
        result.Summary.TimelineCompliance.Deadline.Should().Be("72 hours");
        result.Summary.TimelineCompliance.Requirement.Should().Contain("urgent");
    }

    [Fact]
    [Trait("Scenario", "PAS-06")]
    public void PAS06_StandardRequest_Tracks7CalendarDayClock()
    {
        var checker = new Cms0057ComplianceChecker();

        var result = checker.ValidateCompliance(BuildServiceRequest(RequestPriority.Routine));

        result.Summary.TimelineCompliance.Applicable.Should().BeTrue();
        result.Summary.TimelineCompliance.Deadline.Should().Be("7 calendar days");
    }

    [Fact]
    [Trait("Scenario", "PAS-06")]
    public void PAS06_ClaimResponse_CarriesExplicitTimezoneTimestamp()
    {
        // Received/decision timestamps must be auditable with an explicit
        // timezone so the 72h / 7-calendar-day clocks are unambiguous.
        var builder = new PasResponseBuilder();
        var claim = new Claim
        {
            Id = "c1",
            Type = new CodeableConcept("http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Patient = new ResourceReference("Patient/pat-001"),
        };

        var bundle = builder.BuildApprovedResponse(claim, new FhirService.Models.PasDecisionResult
        {
            HasDecision = true, Decision = "approved", AuthorizationNumber = "PAS-ACC-06",
        });

        var claimResponse = bundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        claimResponse.Created.Should().EndWith("Z"); // UTC / explicit offset
        claimResponse.Meta!.LastUpdated.Should().NotBeNull();
    }

    // ── PAS-07 CDex / additional information ────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-07")]
    public void PAS07_PendedResponse_SignalsOutstandingReviewViaX12A4()
    {
        // A pended decision carries the Da Vinci PAS reviewAction extension with
        // X12 code A4 (Pending) — the signal that a decision (and, in a full
        // CDex exchange, additional documentation) is still outstanding.
        var builder = new PasResponseBuilder();
        var claim = new Claim { Id = "c1", Patient = new ResourceReference("Patient/pat-001") };

        var bundle = builder.BuildPendedResponse(claim);
        var claimResponse = bundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;

        claimResponse.Disposition.Should().Be("pended");
        var reviewAction = claimResponse.Extension.Should().ContainSingle(e =>
            e.Url == "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction").Subject;
        var code = reviewAction.Extension.Should().ContainSingle().Subject.Value as Coding;
        code!.Code.Should().Be("A4");
    }

    [Fact]
    [Trait("Scenario", "PAS-07")]
    [Trait("Kind", "GAP")]
    public void PAS07_Gap_NoCdexAdditionalInfoRoundTripInPasPath()
    {
        // GAP: there is no Da Vinci CDex additional-information request/response
        // round-trip wired into the fhir-service PAS path. The existing
        // CommunicationController projects APPEAL notes onto FHIR Communication
        // (cho-appeal-communication), not a CDex documentation request on a
        // pended prior-auth. Standing up that exchange is engagement/product
        // follow-up.
        var commType = typeof(CommunicationController);
        var xmlRefsAppeal = commType.GetCustomAttributesData(); // presence check only
        xmlRefsAppeal.Should().NotBeNull();

        // Assert the PAS controller exposes no additional-info INTAKE action.
        // Note $inquire is deliberately not disqualifying here: it REPORTS that a
        // decision is pended awaiting information (X12 A4), which CHO already
        // knows, but it neither requests documentation nor accepts it. That
        // round-trip is what CDex is, and it is still missing.
        var pasActions = typeof(PasController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<RouteAttribute>()
                .Select(a => a.Template)
                .Concat(m.GetCustomAttributes<HttpPostAttribute>().Select(a => a.Template ?? "")))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        pasActions.Should().NotContain(t => t!.Contains("additional", StringComparison.OrdinalIgnoreCase)
                                         || t!.Contains("$cdex", StringComparison.OrdinalIgnoreCase));
    }

    // ── PAS-08 drug exclusion ───────────────────────────────────────────────────
    // Behavioral enforcement now lives in the CHO Replace-mode authorization
    // workflow and is proven in DrugExclusionTests (real ChoAuthorizationBackend +
    // benefit-exclusion catalog/evaluator). The former GAP marker here — which
    // pinned that the FHIR compliance checker did not special-case pharmacy — has
    // been removed now that the capability is implemented and covered.

    // ── PAS-04 inquiry / status ─────────────────────────────────────────────────

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_TrackingIdIssuedAtSubmit_IsInquiryHandle()
    {
        // The PreAuthRef on the ClaimResponse is the tracking id a provider uses
        // to inquire on status later.
        var builder = new PasResponseBuilder();
        var claim = new Claim { Id = "c1", Patient = new ResourceReference("Patient/pat-001") };

        var bundle = builder.BuildApprovedResponse(claim, new FhirService.Models.PasDecisionResult
        {
            HasDecision = true, Decision = "approved", AuthorizationNumber = "PAS-ACC-04",
        });
        var claimResponse = bundle.Entry[0].Resource.Should().BeOfType<ClaimResponse>().Subject;
        claimResponse.PreAuthRef.Should().Be("PAS-ACC-04");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    public void PAS04_AuthorizationService_ExposesStatusByNumberAndMemberSearch()
    {
        // Line-level status inquiry is served by authorization-service today:
        // GET api/authorizations/number/{authNumber} and GET .../search.
        var routes = typeof(global::AuthorizationService.Controllers.AuthorizationsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>().Select(a => a.Template ?? ""))
            .ToList();

        routes.Should().Contain(t => t.Contains("number/"));
        routes.Should().Contain("search");
    }

    [Fact]
    [Trait("Scenario", "PAS-04")]
    [Trait("Backend", "Replace")]
    public void PAS04_BothPasOperationsAreServedOnTheFhirSurface()
    {
        // Replaces the GAP test that asserted $inquire did not exist. Status is
        // now reachable through the Da Vinci PAS operation, not only through the
        // authorization-service REST surface asserted above.
        var postTemplates = typeof(PasController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>().Select(a => a.Template ?? ""))
            .ToList();

        postTemplates.Should().Contain("Claim/$submit");
        postTemplates.Should().Contain("Claim/$inquire");
    }
}
