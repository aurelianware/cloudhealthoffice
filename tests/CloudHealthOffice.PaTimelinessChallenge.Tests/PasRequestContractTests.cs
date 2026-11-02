extern alias fhirsvc;
using CloudHealthOffice.Tools.PaTimelinessChallenge;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using PasMapper = fhirsvc::FhirService.Services.Pas.PasAuthorizationMapper;
using ComplianceChecker = fhirsvc::FhirService.Services.Cms0057ComplianceChecker;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>Every live request passes CHO's real PAS compliance check and maps onto an authorization record.</summary>
public sealed class PasRequestContractTests
{
    private static readonly FhirJsonParser Parser = new();

    [Fact]
    public void Every_live_request_is_compliant_and_maps_with_its_line_of_business_and_urgency()
    {
        var now = CorpusFixture.AsOf;
        var live = CorpusFixture.Cases.Where(c => c.Cohort == PaCohort.Live).ToList();
        Assert.NotEmpty(live);
        foreach (var c in live)
        {
            var bundle = Parser.Parse<Bundle>(PasRequestBuilder.Build(c, now).ToJsonString());
            var claim = bundle.Entry.Select(e => e.Resource).OfType<Claim>().Single();

            var compliance = new ComplianceChecker().ValidateCompliance(claim);
            Assert.True(compliance.Compliant, $"{c.CaseId}: {string.Join("; ", compliance.Issues.Select(i => i.Message))}");

            var mapping = PasMapper.Map(bundle, claim, now.UtcDateTime);
            Assert.True(mapping.IsValid, $"{c.CaseId}: {string.Join("; ", mapping.Errors.Select(e => $"{e.Expression}: {e.Message}"))}");
            var record = mapping.Request!;
            Assert.Equal(c.Member.MemberId, record.MemberId);
            Assert.Equal(c.RequestingProvider.Npi, record.RequestingProviderNPI);
            Assert.Equal(c.LevelOfService, record.LevelOfService);
            Assert.Equal(c.Lines.Count, record.RequestedServices.Count);
            // PAS has no Exchange claim filing code: an Exchange request is recorded as Commercial.
            var expectedLob = c.LineOfBusiness == PaLineOfBusiness.Exchange ? "Commercial" : c.LineOfBusiness.ToString();
            Assert.Equal(expectedLob, record.LineOfBusiness);
            Assert.Empty(mapping.Warnings);
        }
    }

    [Fact]
    public void Drug_lines_keep_a_drug_code_system()
    {
        var drug = CorpusFixture.Cases.First(c => c.Cohort == PaCohort.Live && c.Scenario == PaScenario.DrugExcluded);
        var bundle = Parser.Parse<Bundle>(PasRequestBuilder.Build(drug, CorpusFixture.AsOf).ToJsonString());
        var record = PasMapper.Map(bundle, bundle.Entry.Select(e => e.Resource).OfType<Claim>().Single(), CorpusFixture.AsOf.UtcDateTime).Request!;
        Assert.All(record.RequestedServices, s =>
            Assert.True(s.ProductOrServiceSystem is PasRequestBuilder.NdcSystem
                || (s.ProductOrServiceSystem == PasRequestBuilder.HcpcsSystem && s.ProcedureCode!.StartsWith('J'))));
    }
}
