using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds;

/// <summary>
/// CPCDS rows → CHO claim, and that claim through the CARIN BB projections
/// that serve Patient Access, so an imported claim is held to the same shape
/// as one CHO adjudicated.
/// </summary>
public class CpcdsClaimMapperTests
{
    private const string ProfileBase = "http://hl7.org/fhir/us/carin-bb/StructureDefinition/";

    private static CpcdsClaimMapper.Mapped MapSample(string claimId, CpcdsImportOptions? options = null)
    {
        var record = CpcdsClaimFileParser.Parse(CpcdsTestFiles.Sample(), null).Claims.Single(c => c.ClaimId == claimId);
        var memberId = record.Rows[0].Get(CpcdsClaimColumn.MemberId)!;
        return CpcdsClaimMapper.Map(record, memberId, options ?? new CpcdsImportOptions(), "importer");
    }

    private static CpcdsClaimMapper.Mapped MapRows(params IReadOnlyDictionary<string, string>[] rows)
    {
        var header = rows.SelectMany(r => r.Keys).Distinct().ToList();
        var parsed = CpcdsClaimFileParser.Parse(CpcdsTestFiles.Build(header, rows), null);
        parsed.FileErrors.Should().BeEmpty();
        var record = parsed.Claims.Single();
        return CpcdsClaimMapper.Map(record, record.Rows[0].Get(CpcdsClaimColumn.MemberId) ?? "", new CpcdsImportOptions(), "importer");
    }

    // ── professional ─────────────────────────────────────────────────────────

    [Fact]
    public void Professional_claim_maps_header_lines_diagnoses_and_money()
    {
        var mapped = MapSample("PRO-1001");

        mapped.Errors.Should().BeEmpty();
        var claim = mapped.Claim!;
        claim.ClaimNumber.Should().Be("PRO-1001");
        claim.MemberId.Should().Be("M100");
        claim.CoverageId.Should().Be("COV-M100");
        claim.ClaimType.Should().Be(ClaimType.Professional);
        claim.Status.Should().Be(ClaimStatus.Paid);
        claim.VersionState.Should().Be(ClaimVersionState.Paid);
        claim.BillingProviderNPI.Should().Be("1234567893");
        claim.RenderingProviderNPI.Should().Be("1588667638");
        claim.ServiceDateFrom.Should().Be(new DateTime(2026, 3, 2));
        claim.ReceivedDate.Should().Be(new DateTime(2026, 3, 5));
        claim.PaidDate.Should().Be(new DateTime(2026, 3, 20));
        claim.TotalChargeAmount.Should().Be(240.00m);

        claim.DiagnosisCodes.Select(d => (d.Code, d.CodeQualifier, d.PointerNumber))
            .Should().Equal(("E119", "ABK", 1), ("I10", "ABF", 2));

        claim.ClaimLines.Should().HaveCount(2);
        var first = claim.ClaimLines[0];
        first.LineNumber.Should().Be(1);
        first.ProcedureCode.Should().Be("99213");
        first.Modifiers.Should().Equal("25");
        first.ChargeAmount.Should().Be(200.00m);
        first.PlaceOfServiceCode.Should().Be("11");
        first.AdjudicationResult!.AllowedAmount.Should().Be(150.00m);
        first.AdjudicationResult.PaidAmount.Should().Be(120.00m);

        var adj = claim.AdjudicationResult!;
        adj.NetworkTier.Should().Be("InNetwork");
        adj.AllowedAmount.Should().Be(180.00m);
        adj.PayerPayment.Should().Be(150.00m);
        adj.CopayAmount.Should().Be(30.00m);
        adj.PatientResponsibility.Should().Be(30.00m);
        adj.PaymentDate.Should().Be(new DateTime(2026, 3, 20));
    }

    [Fact]
    public void Professional_claim_projects_to_the_carin_bb_professional_eob()
    {
        var eob = new ExplanationOfBenefitProjector().Project(MapSample("PRO-1001").Claim!);

        eob["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().Contain(ProfileBase + "C4BB-ExplanationOfBenefit-Professional-NonClinician");
        eob["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/M100");
        eob["identifier"]![0]!["value"]!.GetValue<string>().Should().Be("PRO-1001");
        eob["outcome"]!.GetValue<string>().Should().Be("complete");
        eob["insurance"]![0]!["coverage"]!["reference"]!.GetValue<string>().Should().Be("Coverage/COV-M100");
        eob["item"]!.AsArray().Should().HaveCount(2);
        eob["payment"]!["amount"]!["value"]!.GetValue<decimal>().Should().Be(150.00m);

        Total(eob, "eligible").Should().Be(180.00m);
        Total(eob, "benefit").Should().Be(150.00m);
        Total(eob, "memberliability").Should().Be(30.00m);
    }

    [Fact]
    public void Denied_claim_carries_its_denial_code_on_the_eob()
    {
        var mapped = MapSample("PRO-1002");

        mapped.Claim!.Status.Should().Be(ClaimStatus.Denied);
        mapped.Claim.AdjudicationResult!.NetworkTier.Should().Be("OutOfNetwork");
        mapped.Claim.PaidDate.Should().BeNull();

        var eob = new ExplanationOfBenefitProjector().Project(mapped.Claim);
        eob["adjudication"]!.AsArray()
            .Select(a => a!["reason"]?["coding"]?[0]?["code"]?.GetValue<string>())
            .Should().Contain("50");
    }

    [Theory]
    [InlineData(null, null, "50", 0, ClaimStatus.Denied)]
    [InlineData(null, null, null, 0, ClaimStatus.Paid)]
    [InlineData(null, null, "50", 10, ClaimStatus.Paid)]
    [InlineData("active", "partiallypaid", null, 10, ClaimStatus.PartiallyPaid)]
    [InlineData("cancelled", "paid", null, 10, ClaimStatus.Voided)]
    [InlineData(null, "Denied", null, 0, ClaimStatus.Denied)]
    public void Status_follows_processing_status_then_payment_status_then_the_amounts(
        string? processing, string? payment, string? denial, int paid, ClaimStatus expected)
    {
        CpcdsClaimMapper.MapStatus(processing, payment, denial, paid, []).Should().Be(expected);
    }

    // ── institutional ────────────────────────────────────────────────────────

    [Fact]
    public void Inpatient_claim_maps_bill_type_drg_admission_and_icd10pcs_procedure()
    {
        var mapped = MapSample("INP-2001");

        mapped.Errors.Should().BeEmpty();
        var claim = mapped.Claim!;
        claim.ClaimType.Should().Be(ClaimType.Institutional);
        claim.Institutional!.FacilityTypeCode.Should().Be("11");
        claim.TypeOfBill.Should().Be("111");
        claim.Institutional.DrgCode.Should().Be("470");
        claim.Institutional.AdmissionDate.Should().Be(new DateTime(2026, 5, 4));
        claim.Institutional.DischargeDate.Should().Be(new DateTime(2026, 5, 7));
        claim.Institutional.PatientStatusCode.Should().Be("01");
        claim.Institutional.PrincipalProcedure!.Code.Should().Be("0SRD0JZ");
        claim.Institutional.PrincipalProcedure.CodeQualifier.Should().Be("BBR");

        claim.ClaimLines.Select(l => (l.RevenueCode, l.ProcedureCode)).Should().Equal(("0120", ""), ("0360", ""));
        claim.DiagnosisCodes.Select(d => d.Code).Should().Equal("M1712", "E669");
        claim.DiagnosisCodes[0].PresentOnAdmission.Should().Be("Y");

        var eob = new ExplanationOfBenefitProjector().Project(claim);
        eob["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().Contain(ProfileBase + "C4BB-ExplanationOfBenefit-Inpatient-Institutional");
        eob["subType"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("inpatient");
    }

    [Fact]
    public void Facility_claim_without_a_bill_type_takes_one_from_the_claim_type_and_says_so()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Claim type"] = "outpatient-facility";
        row["Revenue center code"] = "0450";
        row["Procedure code"] = "";

        var mapped = MapRows(row);

        mapped.Errors.Should().BeEmpty();
        mapped.Claim!.Institutional!.FacilityTypeCode.Should().Be("13");
        mapped.Warnings.Should().Contain(w => w.Contains("Bill type not given"));
    }

    // ── oral ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Oral_claim_maps_tooth_and_surfaces_and_projects_to_the_oral_eob()
    {
        var mapped = MapSample("DEN-3001");

        mapped.Errors.Should().BeEmpty();
        var line = mapped.Claim!.ClaimLines.Single();
        mapped.Claim.ClaimType.Should().Be(ClaimType.Dental);
        line.ProcedureCode.Should().Be("D2393");
        line.Teeth.Single().ToothCode.Should().Be("30");
        line.Teeth.Single().Surfaces.Should().Equal("M", "O", "D");

        var eob = new ExplanationOfBenefitProjector().Project(mapped.Claim);
        eob["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>())
            .Should().Contain(ProfileBase + "C4BB-ExplanationOfBenefit-Oral");
    }

    // ── refusals and warnings ────────────────────────────────────────────────

    [Fact]
    public void Pharmacy_claims_are_refused_until_there_is_a_pharmacy_projection()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Claim type"] = "pharmacy";

        var mapped = MapRows(row);

        mapped.Claim.Should().BeNull();
        mapped.Errors.Should().ContainSingle().Which.Should().Contain("Pharmacy");
    }

    [Fact]
    public void An_amount_that_does_not_parse_refuses_the_claim()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Claim payment amount"] = "sixty";

        var mapped = MapRows(row);

        mapped.Claim.Should().BeNull();
        mapped.Errors.Should().ContainSingle().Which.Should().Contain("\"Claim payment amount\" 'sixty' is not an amount");
    }

    [Fact]
    public void Rows_that_disagree_on_the_member_refuse_the_claim()
    {
        var second = CpcdsTestFiles.ProfessionalRow(memberId: "M-2");
        second["Line number"] = "2";

        var mapped = MapRows(CpcdsTestFiles.ProfessionalRow(), second);

        mapped.Claim.Should().BeNull();
        mapped.Errors.Should().Contain(e => e.Contains("Rows disagree on \"Member id\""));
    }

    [Fact]
    public void A_professional_line_without_a_procedure_code_refuses_the_claim()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Procedure code"] = "";

        MapRows(row).Errors.Should().ContainSingle().Which.Should().Contain("Line 1").And.Contain("\"Procedure code\"");
    }

    [Fact]
    public void A_billing_npi_that_is_not_ten_digits_refuses_the_claim()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Claim billing provider NPI"] = "123456789012";

        MapRows(row).Errors.Should().ContainSingle().Which.Should().Contain("not a 10-digit NPI");
    }

    [Fact]
    public void A_diagnosis_that_is_not_icd10cm_is_left_off_with_a_warning()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Diagnosis code"] = "44054006";
        row["Diagnosis code type"] = "SNOMED";

        var mapped = MapRows(row);

        mapped.Errors.Should().BeEmpty();
        mapped.Claim!.DiagnosisCodes.Should().BeEmpty();
        mapped.Warnings.Should().ContainSingle().Which.Should().Contain("not ICD-10-CM");
    }

    [Fact]
    public void Without_a_flagged_principal_the_first_diagnosis_is_principal()
    {
        var first = CpcdsTestFiles.ProfessionalRow();
        first["Diagnosis type"] = "secondary";
        var second = CpcdsTestFiles.ProfessionalRow();
        second["Diagnosis code"] = "R05.9";
        second["Diagnosis type"] = "";

        var mapped = MapRows(first, second);

        mapped.Claim!.DiagnosisCodes.Select(d => (d.Code, d.CodeQualifier)).Should().Equal(("J069", "ABK"), ("R059", "ABF"));
        mapped.Claim.ClaimLines.Should().ContainSingle("both rows name line 1");
    }

    [Fact]
    public void A_submitted_total_that_differs_from_the_lines_is_kept_with_a_warning()
    {
        var row = CpcdsTestFiles.ProfessionalRow();
        row["Line submitted amount"] = "90.00";

        var mapped = MapRows(row);

        mapped.Claim!.TotalChargeAmount.Should().Be(100.00m);
        mapped.Warnings.Should().Contain(w => w.Contains("differs from the sum"));
    }

    [Fact]
    public void Line_of_business_comes_from_the_import_options()
    {
        var mapped = MapSample("PRO-1001", new CpcdsImportOptions { LineOfBusiness = LineOfBusiness.Medicaid });

        mapped.Claim!.LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
    }

    private static decimal Total(JsonObject eob, string category)
        => eob["total"]!.AsArray()
            .Single(t => t!["category"]!["coding"]![0]!["code"]!.GetValue<string>() == category)!["amount"]!["value"]!
            .GetValue<decimal>();
}
