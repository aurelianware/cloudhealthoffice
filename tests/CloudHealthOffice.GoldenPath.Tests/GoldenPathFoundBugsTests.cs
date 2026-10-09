using CloudHealthOffice.GoldenPath.Tests.Harness;
using CloudHealthOffice.Testing.Mongo;

namespace CloudHealthOffice.GoldenPath.Tests;

/// <summary>
/// Defects the golden path exposed that are too large to fix here, or sit
/// in code other work owns. Each test states the correct behaviour and
/// fails today; remove the Skip when the fix lands.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class GoldenPathFoundBugsTests
{
    private readonly GoldenPathHarness _harness;

    public GoldenPathFoundBugsTests(MongoRunnerFixture mongo) => _harness = new GoldenPathHarness(mongo);

    // 005010X221A1 2100 CLP for an institutional claim: CLP08 facility type
    // code and CLP09 claim frequency code are required, and CLP11 carries the
    // DRG when the claim was paid by DRG. payment-service's ClaimDto /
    // ClaimPayment carry none of them (claims-service has them on
    // Claim.Institutional / ClaimFrequencyCode), so the DRG remittance in
    // Golden/05-inpatient-drg.835 ends at CLP07.
    [Fact(Skip = "bug: 837I 835 omits CLP08 facility code, CLP09 frequency code and CLP11 DRG (payment-service ClaimDto/ClaimPayment lack the fields)")]
    public async Task InstitutionalClp_CarriesFacilityFrequencyAndDrg()
    {
        var r = await _harness.RunAsync(GoldenInputs.Scenario(), GoldenInputs.Edi837("05-inpatient-drg"));

        var clp = X12835.Segments(r.Edi835).Single(s => s[0] == "CLP");
        Assert.True(clp.Length > 11, string.Join("*", clp));
        Assert.Equal("11", clp[8]);
        Assert.Equal("1", clp[9]);
        Assert.Equal("470", clp[11]);
    }

    // N1*PE (1000B payee) name is the NPI: payment-service fills PayeeName from
    // ClaimDto.ProviderName, which claims-service never sends (it sends
    // billingProviderName).
    [Fact(Skip = "bug: 835 N1*PE payee name is the NPI; ClaimDto.ProviderName is never populated from claims-service's billingProviderName")]
    public async Task PayeeName_IsTheBillingProviderName()
    {
        var r = await _harness.RunAsync(
            GoldenInputs.Scenario(GoldenInputs.Prior(deductible: 440m, oop: 440m)), GoldenInputs.Edi837("01-office-visit"));

        var payee = X12835.Segments(r.Edi835).Single(s => s[0] == "N1" && s[1] == "PE");
        Assert.Equal("SYNTHETIC FAMILY CLINIC", payee[2]);
    }

    // OOP max (fixed in #1262): the cap reduces the PR amount itself, so the
    // 835 says PR-2 20.00 with no OA-23 — previously CAS*PR*2*30.00 plus a
    // negative CAS*OA*23*-10.00. Kept as a regression guard.
    [Fact]
    public async Task OopMaxReduction_IsNotRemittedAsNegativeAdjustment()
    {
        var r = await _harness.RunAsync(
            GoldenInputs.Scenario(GoldenInputs.Prior(deductible: 500m, oop: 2980m)), GoldenInputs.Edi837("06-oop-max"));

        var cas = X12835.Segments(r.Edi835).Where(s => s[0] == "CAS").ToList();
        Assert.DoesNotContain(cas, s => s[1] == "OA" && s[2] == "23");
        Assert.Contains(cas, s => s[1] == "PR" && s[2] == "2" && s[3] == "20.00");
    }

    // Without a tenant REV mapping, an 837I stay used to be categorized by POS
    // inference on PlaceOfServiceCode — which for an 837I is CLM05-1, the
    // facility type code ("11" = hospital inpatient), so the stay was treated
    // as POS 11 (office) → service type 98 and denied CO-96 on a plan with
    // no "98" benefit. Fixed: the resolver's fallback for institutional
    // claims infers from the type of bill and revenue code (TOB 111 →
    // Inpatient Hospital), so the stay is approved under the inpatient benefit.
    [Fact]
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
        Assert.Contains(r.BenefitResult.Lines, l => l.ServiceTypeCode == "Inpatient Hospital");
    }
}
