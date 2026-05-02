using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;

namespace BenefitPlanService.Tests.Models.Benefits;

/// <summary>
/// Backward-compatibility contract for the discriminated-union refactor:
/// every benefit row that was persisted before 5.4 carries no
/// <c>"benefitType"</c> property on the wire, and every such row must
/// hydrate as <see cref="MedicalBenefit"/> with all common fields populated.
/// Unknown discriminators must also fall back to MedicalBenefit so future
/// payer-specific extensions never throw on read.
/// </summary>
public class LegacyBenefitHydrationTests
{
    private static readonly JsonSerializerOptions Opts = BuildOpts();
    private static JsonSerializerOptions BuildOpts()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new BenefitJsonConverter());
        return o;
    }

    [Fact]
    public void Flat_shape_without_discriminator_hydrates_as_MedicalBenefit()
    {
        const string legacyJson = """
        {
            "id": "legacy-1",
            "serviceCategory": "Primary Care",
            "description": "Office visit",
            "cptCodes": ["99213", "99214"],
            "inNetworkCopay": 25,
            "deductibleApplies": false,
            "oopApplies": true,
            "priorAuthRequired": false,
            "requiresPriorAuth": false,
            "visitLimit": 10,
            "visitLimitPeriod": "annual",
            "annualMaximum": 5000
        }
        """;

        var hydrated = JsonSerializer.Deserialize<Benefit>(legacyJson, Opts);

        hydrated.Should().BeOfType<MedicalBenefit>();
        hydrated!.Id.Should().Be("legacy-1");
        hydrated.ServiceCategory.Should().Be("Primary Care");
        hydrated.Description.Should().Be("Office visit");
        hydrated.CptCodes.Should().BeEquivalentTo(new[] { "99213", "99214" });
        hydrated.InNetworkCopay.Should().Be(25m);
        hydrated.DeductibleApplies.Should().BeFalse();
        hydrated.VisitLimit.Should().Be(10);
        hydrated.AnnualMaximum.Should().Be(5000m);
    }

    [Fact]
    public void Empty_string_discriminator_hydrates_as_MedicalBenefit()
    {
        const string json = """
        { "id": "x", "benefitType": "", "serviceCategory": "Primary Care" }
        """;

        var hydrated = JsonSerializer.Deserialize<Benefit>(json, Opts);

        hydrated.Should().BeOfType<MedicalBenefit>();
        hydrated!.ServiceCategory.Should().Be("Primary Care");
    }

    [Fact]
    public void Unknown_discriminator_falls_back_to_MedicalBenefit()
    {
        const string json = """
        { "id": "x", "benefitType": "telehealth-future-shape", "serviceCategory": "Primary Care" }
        """;

        var hydrated = JsonSerializer.Deserialize<Benefit>(json, Opts);

        hydrated.Should().BeOfType<MedicalBenefit>();
        hydrated!.ServiceCategory.Should().Be("Primary Care");
    }

    [Fact]
    public void Discriminator_is_case_insensitive()
    {
        const string json = """
        { "benefitType": "Pharmacy", "serviceCategory": "Pharmacy", "formularyTier": "Tier 1" }
        """;

        var hydrated = JsonSerializer.Deserialize<Benefit>(json, Opts);

        hydrated.Should().BeOfType<PharmacyBenefit>();
        ((PharmacyBenefit)hydrated!).FormularyTier.Should().Be("Tier 1");
    }

    [Fact]
    public void Legacy_BenefitPlan_with_flat_benefits_hydrates_each_as_MedicalBenefit()
    {
        const string planJson = """
        {
            "id": "plan-1",
            "tenantId": "t",
            "planId": "p",
            "planName": "Legacy Plan",
            "payer": "Acme",
            "effectiveDate": "2025-01-01T00:00:00Z",
            "planType": "PPO",
            "lineOfBusiness": "Commercial",
            "benefits": [
                { "id": "b1", "serviceCategory": "Primary Care", "inNetworkCopay": 25 },
                { "id": "b2", "serviceCategory": "Pharmacy", "inNetworkCopay": 10 },
                { "id": "b3", "serviceCategory": "DME", "inNetworkCoinsurance": 0.20 }
            ]
        }
        """;

        var plan = JsonSerializer.Deserialize<BenefitPlan>(planJson, Opts)!;

        plan.Benefits.Should().HaveCount(3);
        plan.Benefits.Should().AllBeOfType<MedicalBenefit>(
            "legacy rows lack the benefitType discriminator and the catch-all default is MedicalBenefit");
    }

    [Fact]
    public void Mutating_a_hydrated_legacy_benefit_writes_medical_discriminator_back()
    {
        const string legacyJson = """
        { "serviceCategory": "Primary Care", "inNetworkCopay": 25 }
        """;
        var hydrated = JsonSerializer.Deserialize<Benefit>(legacyJson, Opts)!;
        hydrated.InNetworkCopay = 30m;

        var rewritten = JsonSerializer.Serialize(hydrated, Opts);

        rewritten.Should().Contain("\"benefitType\":\"medical\"",
            "the hydrated MedicalBenefit instance now carries the discriminator on write");
    }
}
