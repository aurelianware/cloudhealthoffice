using System.Net;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using EnrollmentLob = CloudHealthOffice.ProviderEnrollmentService.Models.LineOfBusiness;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// B1: the adjudicator decides on the basis the controller mapped and the
/// record stores (line of business, providers) and on the state the request
/// names, never on a default; and it reports that basis with the decision.
/// </summary>
public class PasAutoAdjudicatorBasisTests
{
    private const string Npi = "1234567893";

    private readonly Mock<IEnrollmentDecisionGate> _gate = new();
    private readonly Mock<IPriorAuthRuleEngine> _engine = new();
    private PaRuleContext? _ruleContext;

    public PasAutoAdjudicatorBasisTests()
    {
        _gate.Setup(g => g.EvaluateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateOnly>(), It.IsAny<EnrollmentLob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateResult.Deny("PEMS-001", "Provider not enrolled in state Medicaid."));
        _engine.Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .Callback<PaRuleContext, CancellationToken>((c, _) => _ruleContext = c)
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "NoRuleMatch",
                ResolvedRuleSetKey = "test",
            });
    }

    [Fact]
    public async System.Threading.Tasks.Task Commercial_IsNotGatedOnStateMedicaidEnrollment()
    {
        var result = await Adjudicator().TryDecideAsync(
            Claim(state: "TX"), new Bundle(), "t", new PasDecisionBasis(PaLineOfBusiness.Commercial, Npi, null), 12000);

        _gate.VerifyNoOtherCalls();
        result.RuleName.Should().NotBe("EnrollmentGate");
        result.LineOfBusiness.Should().Be("Commercial");
        result.StateCode.Should().Be("TX");
        _ruleContext!.Lob.Should().Be(PaLineOfBusiness.Commercial, "the rule engine decides on the record's line of business");
        _ruleContext.StateCode.Should().Be("TX");
        _ruleContext.RequestingProviderNpi.Should().Be(Npi, "the requesting NPI is the mapped one");
    }

    [Theory]
    [InlineData(PaLineOfBusiness.Medicaid, EnrollmentLob.Medicaid)]
    [InlineData(PaLineOfBusiness.Medicare, EnrollmentLob.Medicare)]
    [InlineData(PaLineOfBusiness.Exchange, EnrollmentLob.Marketplace)]
    public async System.Threading.Tasks.Task StateProgramLines_AreGatedOnTheirOwnLineOfBusiness(
        PaLineOfBusiness lob, EnrollmentLob expected)
    {
        var result = await Adjudicator().TryDecideAsync(
            Claim(state: "tx"), new Bundle(), "t", new PasDecisionBasis(lob, Npi, null), 12000);

        _gate.Verify(g => g.EvaluateAsync(Npi, It.IsAny<string>(), "TX", It.IsAny<DateOnly>(), expected,
            It.IsAny<CancellationToken>()), Times.Once);
        result.Decision.Should().Be("denied");
        result.RuleName.Should().Be("EnrollmentGate");
        result.LineOfBusiness.Should().Be(lob.ToString());
        result.StateCode.Should().Be("TX");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Texas")]
    [InlineData("")]
    public async System.Threading.Tasks.Task NoStateNamed_PendsWithoutStateRules_AndNeverDefaultsToTexas(string? state)
    {
        var result = await Adjudicator().TryDecideAsync(
            Claim(state), new Bundle(), "t", new PasDecisionBasis(PaLineOfBusiness.Medicaid, Npi, null), 12000);

        result.HasDecision.Should().BeFalse();
        result.RuleName.Should().Be("StateUndetermined");
        result.PendReason.Should().Be(PasAutoAdjudicator.StateUndeterminedPendReason);
        result.StateCode.Should().BeNull();
        result.LineOfBusiness.Should().Be("Medicaid");
        _gate.VerifyNoOtherCalls();
        _engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async System.Threading.Tasks.Task ServicingNpi_IsTheMappedOne()
    {
        await Adjudicator().TryDecideAsync(
            Claim(state: "TX"), new Bundle(), "t",
            new PasDecisionBasis(PaLineOfBusiness.Commercial, Npi, "1987654325"), 12000);

        _ruleContext!.ServicingProviderNpi.Should().Be("1987654325");
    }

    [Fact]
    public async System.Threading.Tasks.Task EveryDecision_CarriesItsBasis_EvenWhenDisabled()
    {
        var result = await Adjudicator(enabled: false).TryDecideAsync(
            Claim(state: "NM"), new Bundle(), "t", new PasDecisionBasis(PaLineOfBusiness.VA, Npi, null), 12000);

        result.RuleName.Should().Be("ConfigDisabled");
        result.LineOfBusiness.Should().Be("VA");
        result.StateCode.Should().Be("NM");
    }

    private PasAutoAdjudicator Adjudicator(bool enabled = true)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(new NotFound()) { BaseAddress = new Uri("http://authorization-service.test/") });
        return new PasAutoAdjudicator(
            Options.Create(new PasAutoAdjudicationConfig { Enabled = enabled, DollarThreshold = 0m }),
            factory.Object, _gate.Object, _engine.Object, NullLogger<PasAutoAdjudicator>.Instance);
    }

    private static Claim Claim(string? state) => new()
    {
        Use = ClaimUseCode.Preauthorization,
        Patient = new ResourceReference("Patient/pat-001"),
        Provider = new ResourceReference("Practitioner/prac-001"),
        Item =
        [
            new()
            {
                Sequence = 1,
                ProductOrService = new CodeableConcept("http://www.ama-assn.org/go/cpt", "70553"),
                Serviced = new Date("2026-11-02"),
                Location = state is null ? null : new Address { State = state },
            },
        ],
    };

    private sealed class NotFound : HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
