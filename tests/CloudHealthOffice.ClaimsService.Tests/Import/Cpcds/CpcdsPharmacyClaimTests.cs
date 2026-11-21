using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds;

/// <summary>
/// CPCDS pharmacy claims: the NCPDP header onto the claim record, and that
/// record through the CARIN BB Pharmacy EOB (financial and Basis).
/// </summary>
public class CpcdsPharmacyClaimTests
{
    private const string ProfileBase = "http://hl7.org/fhir/us/carin-bb/StructureDefinition/";

    private static CpcdsClaimMapper.Mapped MapSample(string claimId)
    {
        var parsed = CpcdsClaimFileParser.Parse(CpcdsTestFiles.PharmacySample(), null);
        parsed.FileErrors.Should().BeEmpty();
        parsed.UnrecognizedColumns.Should().BeEmpty();
        var record = parsed.Claims.Single(c => c.ClaimId == claimId);
        return CpcdsClaimMapper.Map(record, record.Rows[0].Get(CpcdsClaimColumn.MemberId)!, new CpcdsImportOptions(), "importer");
    }

    private static Dictionary<string, string> Row(string claimId = "RX-1") => new()
    {
        ["Claim unique identifier"] = claimId,
        ["Member id"] = "M-1",
        ["Claim type"] = "pharmacy",
        ["Claim service start date"] = "2026-02-01",
        ["Claim billing provider NPI"] = "1003000126",
        ["Claim payment amount"] = "8.00",
        ["Days supply"] = "30",
        ["DAW product selection code"] = "0",
        ["Refill number"] = "0",
        ["Refills authorized"] = "3",
        ["Line number"] = "1",
        ["National drug code"] = "00093714656",
        ["Quantity dispensed"] = "30",
        ["Line submitted amount"] = "20.00",
    };

    private static CpcdsClaimMapper.Mapped MapRow(Dictionary<string, string> row)
    {
        var parsed = CpcdsClaimFileParser.Parse(CpcdsTestFiles.Build(row.Keys.ToList(), row), null);
        var record = parsed.Claims.Single();
        return CpcdsClaimMapper.Map(record, "M-1", new CpcdsImportOptions(), "importer");
    }

    private static IEnumerable<string> Codes(JsonNode? node, string path) =>
        node!.AsArray().Select(e => e![path]!["coding"]![0]!["code"]!.GetValue<string>());

    private static JsonObject SupportingInfo(JsonObject eob, string category) =>
        eob["supportingInfo"]!.AsArray()
            .Single(e => e!["category"]!["coding"]![0]!["code"]!.GetValue<string>() == category)!.AsObject();

    [Fact]
    public void Pharmacy_claim_maps_the_ncpdp_header_and_the_drug_line()
    {
        var mapped = MapSample("RX-5001");

        mapped.Errors.Should().BeEmpty();
        mapped.Warnings.Should().BeEmpty();
        var claim = mapped.Claim!;
        claim.ClaimType.Should().Be(ClaimType.Pharmacy);
        claim.Status.Should().Be(ClaimStatus.Paid);
        claim.BillingProviderNPI.Should().Be("1003000126");
        claim.BillingProviderEntityType.Should().Be("2");
        claim.PlaceOfServiceCode.Should().Be("01");

        var rx = claim.Pharmacy!;
        rx.DaysSupply.Should().Be(30m);
        rx.DispenseAsWrittenCode.Should().Be("0");
        rx.RefillNumber.Should().Be(1);
        rx.RefillsAuthorized.Should().Be(5);
        rx.PrescriptionNumber.Should().Be("RX7700112");
        rx.PrescriptionOriginCode.Should().Be("1");
        rx.BrandGenericIndicator.Should().Be("2");
        rx.CompoundCode.Should().Be("1");
        rx.PrescriberNpi.Should().Be("1588667638");
        rx.PharmacyServiceType.Should().Be("1");

        var line = claim.ClaimLines.Single();
        line.NationalDrugCode.Should().Be("00093714656", "a hyphenated 5-4-2 NDC is stored as its 11 digits");
        line.DrugQuantity.Should().Be(30m);
        line.Units.Should().Be(30m);
        line.ProcedureCode.Should().BeEmpty();

        claim.AdjudicationResult!.PayerPayment.Should().Be(8.00m);
        claim.AdjudicationResult.CopayAmount.Should().Be(10.00m);
    }

    [Fact]
    public void Pharmacy_claim_projects_to_the_carin_bb_pharmacy_eob()
    {
        var eob = new ExplanationOfBenefitProjector().Project(MapSample("RX-5001").Claim!);

        eob["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().Contain(ProfileBase + "C4BB-ExplanationOfBenefit-Pharmacy");
        eob["type"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("pharmacy");
        eob["provider"]!["reference"]!.GetValue<string>().Should().Be("Organization/1003000126");

        var careTeam = eob["careTeam"]!.AsArray().Single()!;
        careTeam["provider"]!["reference"]!.GetValue<string>().Should().Be("Practitioner/1588667638");
        careTeam["role"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("prescribing");

        SupportingInfo(eob, "dayssupply")["valueQuantity"]!["value"]!.GetValue<decimal>().Should().Be(30m);
        SupportingInfo(eob, "dawcode")["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("0");
        SupportingInfo(eob, "refillnum")["valueQuantity"]!["value"]!.GetValue<int>().Should().Be(1);
        SupportingInfo(eob, "refillsauthorized")["valueQuantity"]!["value"]!.GetValue<int>().Should().Be(5);
        SupportingInfo(eob, "brandgenericindicator")["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("2");
        SupportingInfo(eob, "rxorigincode")["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("1");
        SupportingInfo(eob, "compoundcode")["code"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("1");
        SupportingInfo(eob, "clmrecvddate").Should().NotBeNull();
        eob["supportingInfo"]!.AsArray().Select(e => e!["sequence"]!.GetValue<int>())
            .Should().OnlyHaveUniqueItems();

        var item = eob["item"]!.AsArray().Single()!;
        item["productOrService"]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/sid/ndc");
        item["productOrService"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("00093714656");
        item["servicedDate"]!.GetValue<string>().Should().Be("2026-03-04");
        item["servicedPeriod"].Should().BeNull();
        item["locationCodeableConcept"].Should().BeNull();
        item["quantity"]!["value"]!.GetValue<decimal>().Should().Be(30m);
        Codes(item["adjudication"], "category").Should().Contain("submitted");

        Codes(eob["adjudication"], "category").Should().Contain("benefitpaymentstatus");
        Codes(eob["total"], "category").Should().Contain(["submitted", "eligible", "benefit", "copay"]);
    }

    [Fact]
    public void The_basis_pharmacy_eob_carries_no_money()
    {
        var eob = new CarinNonFinancialEobProjector().Project(MapSample("RX-5001").Claim!);

        eob["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().Equal(CarinNonFinancialEobProjector.PharmacyBasisProfile);
        eob["total"].Should().BeNull();
        eob["payment"].Should().BeNull();
        var item = eob["item"]!.AsArray().Single()!.AsObject();
        item.ContainsKey("net").Should().BeFalse();
        item.ContainsKey("adjudication").Should().BeFalse();
        Codes(eob["adjudication"], "category").Should().Contain("benefitpaymentstatus");
        eob["supportingInfo"]!.AsArray().Count.Should().BeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void A_denied_pharmacy_claim_carries_its_ncpdp_reject_code_on_the_item_not_as_a_carc()
    {
        var mapped = MapSample("RX-5002");

        mapped.Errors.Should().BeEmpty();
        mapped.Claim!.Status.Should().Be(ClaimStatus.Denied);
        mapped.Claim.ClaimLines.Single().NationalDrugCode.Should().Be("00002821501", "a 4-4-2 NDC is padded to 5-4-2");

        var eob = new ExplanationOfBenefitProjector().Project(mapped.Claim);
        var reject = eob["item"]![0]!["adjudication"]!.AsArray()
            .Single(a => a!["category"]!["coding"]![0]!["code"]!.GetValue<string>() == "rejectreason")!;
        reject["reason"]!["coding"]![0]!["system"]!.GetValue<string>()
            .Should().Be("http://terminology.hl7.org/CodeSystem/NCPDPRejectCode");
        reject["reason"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("75");

        Codes(eob["adjudication"], "category").Should().NotContain("adjustmentreason");
    }

    [Fact]
    public void A_compound_without_an_ndc_is_the_compound_literal()
    {
        var mapped = MapSample("RX-5003");

        mapped.Errors.Should().BeEmpty();
        var eob = new ExplanationOfBenefitProjector().Project(mapped.Claim!);
        var coding = eob["item"]![0]!["productOrService"]!["coding"]![0]!;
        coding["system"]!.GetValue<string>().Should().Be("http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBCompoundLiteral");
        coding["code"]!.GetValue<string>().Should().Be("compound");
    }

    [Fact]
    public void A_pharmacy_claim_without_days_supply_is_refused()
    {
        var row = Row();
        row.Remove("Days supply");

        var mapped = MapRow(row);

        mapped.Claim.Should().BeNull();
        mapped.Errors.Should().ContainSingle().Which.Should().Contain("Days supply");
    }

    [Fact]
    public void A_pharmacy_line_without_an_ndc_is_refused_unless_it_is_a_compound()
    {
        var row = Row();
        row["National drug code"] = "";

        MapRow(row).Errors.Should().ContainSingle().Which.Should().Contain("National drug code");

        row["Compound code"] = "2";
        MapRow(row).Errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("12345-678")]
    public void An_ndc_that_is_not_eleven_digits_is_refused(string ndc)
    {
        var row = Row();
        row["National drug code"] = ndc;

        MapRow(row).Errors.Should().ContainSingle().Which.Should().Contain("11-digit NDC");
    }

    [Theory]
    [InlineData("00093-7146-56", "00093714656")]
    [InlineData("0093-7146-56", "00093714656")]
    [InlineData("00093-146-56", "00093014656")]
    [InlineData("00093-7146-6", "00093714606")]
    [InlineData("00093714656", "00093714656")]
    public void Ndcs_are_normalized_to_eleven_digits(string given, string expected)
        => CpcdsClaimMapper.NormalizeNdc(given).Should().Be(expected);

    [Fact]
    public void Missing_daw_and_refill_values_take_the_ncpdp_defaults_with_warnings()
    {
        var row = Row();
        row.Remove("DAW product selection code");
        row.Remove("Refill number");
        row.Remove("Refills authorized");

        var mapped = MapRow(row);

        mapped.Errors.Should().BeEmpty();
        mapped.Claim!.Pharmacy!.DispenseAsWrittenCode.Should().Be("0");
        mapped.Claim.Pharmacy.RefillNumber.Should().Be(0);
        mapped.Claim.Pharmacy.RefillsAuthorized.Should().Be(0);
        mapped.Warnings.Should().HaveCount(3);
    }

    [Fact]
    public void A_daw_code_outside_the_ncpdp_list_refuses_the_claim()
    {
        var row = Row();
        row["DAW product selection code"] = "Y";

        MapRow(row).Errors.Should().ContainSingle().Which.Should().Contain("DAW product selection code");
    }
}
