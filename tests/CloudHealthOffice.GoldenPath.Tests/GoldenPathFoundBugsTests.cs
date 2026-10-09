using CloudHealthOffice.GoldenPath.Tests.Harness;
using CloudHealthOffice.Testing.Mongo;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// Defects the golden path exposed. Each test states the correct behaviour;
/// a skipped one still fails today (remove the Skip when its fix lands).
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class GoldenPathFoundBugsTests
{
    private readonly GoldenPathHarness _harness;

    public GoldenPathFoundBugsTests(MongoRunnerFixture mongo) => _harness = new GoldenPathHarness(mongo);

    // Fixed: 005010X221A1 2100 CLP for an institutional claim: CLP08 facility
    // type code and CLP09 claim frequency code are required, and CLP11 carries
    // the DRG. They travel from claims-service's Claim.Institutional /
    // ClaimFrequencyCode through ClaimDto and ClaimPayment.
    [Fact]
    public async Task InstitutionalClp_CarriesFacilityFrequencyAndDrg()
    {
        var r = await _harness.RunAsync(GoldenInputs.Scenario(), GoldenInputs.Edi837("05-inpatient-drg"));

        var clp = X12835.Segments(r.Edi835).Single(s => s[0] == "CLP");
        Assert.True(clp.Length > 11, string.Join("*", clp));
        Assert.Equal("11", clp[8]);
        Assert.Equal("1", clp[9]);
        Assert.Equal("470", clp[11]);
    }

    // Fixed: N1*PE (1000B payee) name was the NPI: ClaimDto.ProviderName is
    // now read from claims-service's billingProviderName.
    [Fact]
    public async Task PayeeName_IsTheBillingProviderName()
    {
        var r = await _harness.RunAsync(
            GoldenInputs.Scenario(GoldenInputs.Prior(deductible: 440m, oop: 440m)), GoldenInputs.Edi837("01-office-visit"));

        var payee = X12835.Segments(r.Edi835).Single(s => s[0] == "N1" && s[1] == "PE");
        Assert.Equal("SYNTHETIC FAMILY CLINIC", payee[2]);
    }

    // OOP max: the engine reports the full coinsurance as PR-2 and the cap as a
    // negative OA-23 (CAS*PR*2*30.00 + CAS*OA*23*-10.00). The member owes 20, so
    // the 835 should say PR-2 20.00 with no OA adjustment; a negative OA-23
    // ("prior payer adjudication") is not what happened. BenefitEngine OA-23
    // code is owned by other work, so this is not changed here.
    [Fact(Skip = "bug: OOP-max reduction is remitted as a negative OA-23 instead of reducing the PR amount (BenefitEngine OA-23 code, out of scope here)")]
    public async Task OopMaxReduction_IsNotRemittedAsNegativeAdjustment()
    {
        var r = await _harness.RunAsync(
            GoldenInputs.Scenario(GoldenInputs.Prior(deductible: 500m, oop: 2980m)), GoldenInputs.Edi837("06-oop-max"));

        var cas = X12835.Segments(r.Edi835).Where(s => s[0] == "CAS").ToList();
        Assert.DoesNotContain(cas, s => s[1] == "OA" && s[2] == "23");
        Assert.Contains(cas, s => s[1] == "PR" && s[2] == "2" && s[3] == "20.00");
    }

    // Without a tenant REV mapping, an 837I stay is categorized by POS
    // inference on PlaceOfServiceCode — which for an 837I is CLM05-1, the
    // facility type code ("11" = hospital inpatient), so the stay is treated
    // as POS 11 (office) → service type 98 and denied CO-96 on a plan with
    // no "98" benefit. The fallback should use the bill type / an inpatient
    // category for institutional claims.
    [Fact(Skip = "bug: 837I POS fallback reads CLM05-1 facility type '11' as place of service 11 (office); an unmapped inpatient stay is denied as an office visit")]
    public async Task InpatientStay_WithoutRevenueMapping_IsNotAnOfficeVisit()
    {
        var scenario = GoldenInputs.Scenario();
        var withoutRev = new GoldenScenario
        {
            PlanDocument = scenario.PlanDocument,
            FeeSchedules = scenario.FeeSchedules,
            Contracts = scenario.Contracts,
            CategoryMappings = scenario.CategoryMappings.Where(m => m.ServiceTypeCode != "Inpatient Hospital").ToList(),
        };

        var r = await _harness.RunAsync(withoutRev, GoldenInputs.Edi837("05-inpatient-drg"));

        Assert.All(r.BenefitResult.Lines, l => Assert.NotEqual("98", l.ServiceTypeCode));
    }
}
