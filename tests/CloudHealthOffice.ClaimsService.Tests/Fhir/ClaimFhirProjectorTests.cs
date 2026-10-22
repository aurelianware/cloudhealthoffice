using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Fhir;

/// <summary>
/// The FHIR R4 Claim projection — the claim as billed (ADR 024). What is worth
/// pinning: that no adjudication outcome leaks into it (that is the EOB's job),
/// that <c>status</c> is the fm-status of the resource rather than the
/// adjudication state, and that the line pointers still resolve to the
/// diagnoses they were built against.
/// </summary>
public class ClaimFhirProjectorTests
{
    private static readonly ClaimFhirProjector Projector = new();

    private static Claim Claim() => new()
    {
        Id = "clm-1",
        ClaimVersionId = "clm-1",
        TenantId = "tenant-1",
        ClaimNumber = "CLM-0001",
        MemberId = "MEM-1",
        CoverageId = "cov-1",
        Status = ClaimStatus.Paid,
        ClaimType = ClaimType.Professional,
        LineOfBusiness = LineOfBusiness.Medicaid,
        BillingProviderNPI = "1234567893",
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 180m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        SubmittedDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes = [new DiagnosisCode { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" }],
        ClaimLines =
        [
            new ClaimLine
            {
                LineNumber = 1,
                ProcedureCode = "99214",
                Units = 1,
                ChargeAmount = 180m,
                DiagnosisPointers = [1],
                ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            },
        ],
    };

    private static JsonObject Project(Claim claim) => Projector.Project(claim);

    [Fact]
    public void The_resource_is_the_claim_as_billed_and_shares_the_eob_chain_id()
    {
        var claim = Claim();
        var resource = Project(claim);

        resource["resourceType"]!.GetValue<string>().Should().Be("Claim");
        // The same id the EOB projection emits, so the two name one claim.
        resource["id"]!.GetValue<string>().Should().Be(new ExplanationOfBenefitProjector().Project(claim)["id"]!.GetValue<string>());
        resource["use"]!.GetValue<string>().Should().Be("claim");
        resource["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/MEM-1");
        resource["created"]!.GetValue<string>().Should().StartWith("2026-09-17");
        resource["total"]!["value"]!.GetValue<decimal>().Should().Be(180m);
        resource["total"]!["currency"]!.GetValue<string>().Should().Be("USD");
    }

    [Fact]
    public void A_legacy_row_without_a_chain_id_falls_back_to_its_own_id()
    {
        var claim = Claim();
        claim.ClaimVersionId = string.Empty;

        Project(claim)["id"]!.GetValue<string>().Should().Be("clm-1");
    }

    [Theory]
    // fm-status is about the resource, not the adjudication outcome: a denied
    // or pended claim still stands.
    [InlineData(ClaimStatus.Submitted, "active")]
    [InlineData(ClaimStatus.Pended, "active")]
    [InlineData(ClaimStatus.Denied, "active")]
    [InlineData(ClaimStatus.Deny, "active")]
    [InlineData(ClaimStatus.Paid, "active")]
    [InlineData(ClaimStatus.Voided, "cancelled")]
    public void Status_is_the_fm_status_of_the_resource_not_the_adjudication_state(ClaimStatus stored, string expected)
    {
        var claim = Claim();
        claim.Status = stored;

        Project(claim)["status"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void No_adjudication_outcome_appears_on_the_claim()
    {
        var claim = Claim();
        claim.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = 120m,
            PayerPayment = 100m,
            PatientResponsibility = 20m,
            DenialReasonCode = "CO-97",
        };

        var json = Project(claim).ToJsonString();

        // A Claim carries the request; what adjudication decided is on the EOB.
        json.Should().NotContain("adjudication");
        json.Should().NotContain("120");
        json.Should().NotContain("CO-97");
        Project(claim)["item"]![0]!["adjudication"].Should().BeNull();
    }

    [Fact]
    public void The_billed_line_carries_its_total_charge_and_no_unit_price()
    {
        var claim = Claim();
        claim.ClaimLines[0].Units = 3;
        claim.ClaimLines[0].ChargeAmount = 180m;

        var item = Project(claim)["item"]![0]!;

        item["quantity"]!["value"]!.GetValue<decimal>().Should().Be(3m);
        item["net"]!["value"]!.GetValue<decimal>().Should().Be(180m, "the stored charge is the line total, not a unit price");
        item["unitPrice"].Should().BeNull("setting it to net would imply net = unitPrice x quantity");
    }

    [Fact]
    public void Line_diagnosis_pointers_resolve_to_the_diagnoses_they_were_built_against()
    {
        var claim = Claim();
        claim.DiagnosisCodes =
        [
            new DiagnosisCode { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" },
            new DiagnosisCode { Code = "I10", PointerNumber = 4, CodeQualifier = "ABF" },
        ];
        claim.ClaimLines[0].DiagnosisPointers = [4];

        var resource = Project(claim);
        var sequences = resource["diagnosis"]!.AsArray().Select(d => d!["sequence"]!.GetValue<int>()).ToArray();


        // The 837 pointer numbers are kept, so the line's pointer still finds
        // the diagnosis it names rather than a renumbered one.
        sequences.Should().Equal(1, 4);
        resource["item"]![0]!["diagnosisSequence"]!.AsArray()
            .Select(s => s!.GetValue<int>()).Should().Equal(4);
        resource["diagnosis"]![1]!["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("secondary");
        resource["diagnosis"]![0]!["type"]![0]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("principal");
    }

    [Fact]
    public void Unusable_pointers_fall_back_to_ordinal_numbering_rather_than_colliding()
    {
        var claim = Claim();
        claim.DiagnosisCodes =
        [
            new DiagnosisCode { Code = "E11.9", PointerNumber = 0 },
            new DiagnosisCode { Code = "I10", PointerNumber = 0 },
        ];

        var sequences = Project(claim)["diagnosis"]!.AsArray()
            .Select(d => d!["sequence"]!.GetValue<int>()).ToArray();

        sequences.Should().Equal([1, 2], "two diagnoses cannot share a sequence");
    }

    [Fact]
    public void The_prior_authorization_the_claim_cited_travels_as_a_pre_auth_ref()
    {
        var claim = Claim();
        claim.PriorAuthorizationNumber = "PAS-20260916-ABCD1234";

        var insurance = Project(claim)["insurance"]![0]!;

        insurance["focal"]!.GetValue<bool>().Should().BeTrue();
        insurance["coverage"]!["reference"]!.GetValue<string>().Should().Be("Coverage/cov-1");
        insurance["preAuthRef"]![0]!.GetValue<string>().Should().Be("PAS-20260916-ABCD1234");
    }

    [Fact]
    public void A_claim_with_no_coverage_id_says_so_rather_than_inventing_a_reference()
    {
        var claim = Claim();
        claim.CoverageId = null;

        var coverage = Project(claim)["insurance"]![0]!["coverage"]!;

        coverage["reference"].Should().BeNull("a fabricated Coverage id would not resolve");
        coverage["display"]!.GetValue<string>().Should().Contain("Medicaid");
    }

    [Fact]
    public void A_drug_line_carries_its_ndc_as_a_detail()
    {
        var claim = Claim();
        claim.ClaimLines[0].NationalDrugCode = "00002771501";
        claim.ClaimLines[0].DrugQuantity = 2.5m;
        claim.ClaimLines[0].DrugUnitOfMeasure = "ML";

        var detail = Project(claim)["item"]![0]!["detail"]![0]!;

        detail["productOrService"]!["coding"]![0]!["system"]!.GetValue<string>().Should().Be(ClaimFhirProjector.NdcSystem);
        detail["productOrService"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("00002771501");
        detail["quantity"]!["value"]!.GetValue<decimal>().Should().Be(2.5m);
        detail["quantity"]!["code"]!.GetValue<string>().Should().Be("ML");
    }

    [Fact]
    public void A_drug_line_with_no_quantity_omits_it_rather_than_sending_null()
    {
        var claim = Claim();
        claim.ClaimLines[0].NationalDrugCode = "00002771501";

        var detail = Project(claim)["item"]![0]!["detail"]![0]!;

        detail.AsObject().ContainsKey("quantity").Should().BeFalse();
    }

    [Fact]
    public void A_line_that_overrides_the_place_of_service_says_so_where_fhir_defines_it()
    {
        var claim = Claim();
        claim.ClaimLines[0].PlaceOfServiceCode = "21";

        var resource = Project(claim);

        resource["item"]![0]!["locationCodeableConcept"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("21");
        // The claim's own place of service is still on the header extension.
        resource["extension"]![0]!["valueCoding"]!["code"]!.GetValue<string>().Should().Be("11");
    }

    [Fact]
    public void A_line_that_repeats_the_claims_place_of_service_does_not_restate_it()
    {
        var claim = Claim();
        claim.ClaimLines[0].PlaceOfServiceCode = "11";

        Project(claim)["item"]![0]!["locationCodeableConcept"].Should().BeNull();
    }

    [Fact]
    public void No_profile_is_claimed()
    {
        Project(Claim())["meta"]!["profile"].Should().BeNull("the projection is not validated CARIN BB or PAS (G-14, G-60)");
    }

    [Theory]
    [InlineData(ClaimType.Professional, "professional")]
    [InlineData(ClaimType.Institutional, "institutional")]
    [InlineData(ClaimType.Dental, "oral")]
    public void Type_uses_the_hl7_claim_type_codes(ClaimType stored, string expected)
    {
        var claim = Claim();
        claim.ClaimType = stored;

        var coding = Project(claim)["type"]!["coding"]![0]!;

        coding["system"]!.GetValue<string>().Should().Be(ClaimFhirProjector.ClaimTypeSystem);
        coding["code"]!.GetValue<string>().Should().Be(expected);
    }
}
