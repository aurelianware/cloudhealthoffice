using System.Text.Json;
using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Fhir;

/// <summary>
/// Unit tests for the FHIR R4 ExplanationOfBenefit projector (capability
/// 5.11). Covers the legacy field set inherited from the v1 member-search
/// endpoint plus the 5.11 enhancements: totals expansion (coinsurance +
/// patient-responsibility), header denial adjudication, line-level NCCI/MUE
/// edit-failure adjudication, AI-examination supportingInfo, and Coverage
/// reference (Decision 15 amendment — uses CoverageId, not MemberId).
/// </summary>
public class ExplanationOfBenefitProjectorTests
{
    private readonly ExplanationOfBenefitProjector _projector = new();

    // ── Header / identity ────────────────────────────────────────────────────

    [Fact]
    public void Project_emits_required_header_fields()
    {
        var claim = MinimalClaim();

        var json = _projector.Project(claim);

        json["resourceType"]!.GetValue<string>().Should().Be("ExplanationOfBenefit");
        json["use"]!.GetValue<string>().Should().Be("claim");
        json["insurer"]!["display"]!.GetValue<string>().Should().Be("CloudHealthOffice");
        json["patient"]!["reference"]!.GetValue<string>().Should().Be("Patient/MEM-1");
        json["provider"]!["identifier"]!["system"]!.GetValue<string>()
            .Should().Be("http://hl7.org/fhir/sid/us-npi");
        json["provider"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("1234567890");
        json["billablePeriod"]!["start"]!.GetValue<string>().Should().Be("2026-01-15");
    }

    [Fact]
    public void Project_uses_ClaimVersionId_for_FHIR_id_when_present()
    {
        var claim = MinimalClaim();
        claim.Id = "row-abc";
        claim.ClaimVersionId = "chain-xyz";

        var json = _projector.Project(claim);

        json["id"]!.GetValue<string>().Should().Be("chain-xyz");
    }

    [Fact]
    public void Project_falls_back_to_per_row_Id_when_ClaimVersionId_empty()
    {
        var claim = MinimalClaim();
        claim.Id = "row-only";
        claim.ClaimVersionId = string.Empty;

        var json = _projector.Project(claim);

        json["id"]!.GetValue<string>().Should().Be("row-only");
    }

    [Fact]
    public void Project_emits_meta_lastUpdated_in_iso_8601()
    {
        var claim = MinimalClaim();
        claim.LastUpdatedDate = new DateTime(2026, 4, 30, 14, 0, 0, DateTimeKind.Utc);

        var json = _projector.Project(claim);

        json["meta"]!["lastUpdated"]!.GetValue<string>().Should().StartWith("2026-04-30T14:00:00");
    }

    // ── Status / type / outcome mappers ──────────────────────────────────────

    [Theory]
    [InlineData(ClaimStatus.Submitted, "draft", "queued")]
    [InlineData(ClaimStatus.Received, "draft", "queued")]
    [InlineData(ClaimStatus.InAdjudication, "draft", "queued")]
    [InlineData(ClaimStatus.Pended, "draft", "queued")]
    [InlineData(ClaimStatus.Approved, "active", "complete")]
    [InlineData(ClaimStatus.Paid, "active", "complete")]
    [InlineData(ClaimStatus.PartiallyPaid, "active", "partial")]
    [InlineData(ClaimStatus.Denied, "active", "error")]
    [InlineData(ClaimStatus.Voided, "cancelled", "queued")]
    public void Project_maps_status_and_outcome_consistently(
        ClaimStatus status, string expectedStatus, string expectedOutcome)
    {
        var claim = MinimalClaim();
        claim.Status = status;

        var json = _projector.Project(claim);

        json["status"]!.GetValue<string>().Should().Be(expectedStatus);
        json["outcome"]!.GetValue<string>().Should().Be(expectedOutcome);
    }

    [Theory]
    [InlineData(ClaimType.Professional, "professional")]
    [InlineData(ClaimType.Institutional, "institutional")]
    [InlineData(ClaimType.Dental, "oral")]
    public void Project_maps_claim_type(ClaimType type, string expected)
    {
        var claim = MinimalClaim();
        claim.ClaimType = type;

        var json = _projector.Project(claim);

        json["type"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be(expected);
    }

    // ── insurance[] (Decision 15) ────────────────────────────────────────────

    [Fact]
    public void Project_emits_Coverage_reference_using_CoverageId_when_present()
    {
        var claim = MinimalClaim();
        claim.CoverageId = "COV-987";

        var json = _projector.Project(claim);

        var insurance = json["insurance"]!.AsArray();
        insurance.Should().HaveCount(1);
        insurance[0]!["focal"]!.GetValue<bool>().Should().BeTrue();
        insurance[0]!["coverage"]!["reference"]!.GetValue<string>()
            .Should().Be("Coverage/COV-987");
    }

    [Fact]
    public void Project_omits_insurance_when_CoverageId_null()
    {
        var claim = MinimalClaim();
        claim.CoverageId = null;

        var json = _projector.Project(claim);

        json.ContainsKey("insurance").Should().BeFalse(
            "Decision 15 — Coverage reference is omitted when no CoverageId is on the claim");
    }

    // ── totals expansion ─────────────────────────────────────────────────────

    [Fact]
    public void Project_emits_full_total_set_including_coinsurance_and_patient_responsibility()
    {
        var claim = MinimalClaim();
        claim.TotalChargeAmount = 500m;
        claim.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = 400m,
            PayerPayment = 280m,
            CopayAmount = 25m,
            DeductibleAmount = 50m,
            CoinsuranceAmount = 35m,
            PatientResponsibility = 110m,
        };

        var json = _projector.Project(claim);
        var totals = json["total"]!.AsArray();

        totals.Should().HaveCount(7);

        totals.Should().Contain(t => CategoryCode(t!) == "submitted" && Amount(t!) == 500m);
        totals.Should().Contain(t => CategoryCode(t!) == "eligible" && Amount(t!) == 400m);
        totals.Should().Contain(t => CategoryCode(t!) == "benefit" && Amount(t!) == 280m);
        totals.Should().Contain(t => CategoryCode(t!) == "copay" && Amount(t!) == 25m);
        totals.Should().Contain(t => CategoryCode(t!) == "deductible" && Amount(t!) == 50m);
        totals.Should().Contain(t => CategoryCode(t!) == "coinsurance" && Amount(t!) == 35m);
        totals.Should().Contain(t => CategoryCode(t!) == "patientresponsibility"
                                  && Amount(t!) == 110m);
    }

    [Fact]
    public void Project_omits_total_and_payment_when_AdjudicationResult_null()
    {
        var claim = MinimalClaim();
        claim.AdjudicationResult = null;

        var json = _projector.Project(claim);

        json.ContainsKey("total").Should().BeFalse(
            "Decision 14 — total[] is gated on AdjudicationResult being populated");
        json.ContainsKey("payment").Should().BeFalse();
    }

    [Fact]
    public void Project_emits_payment_block_only_when_PaymentDate_set()
    {
        var claim = MinimalClaim();
        claim.AdjudicationResult = new AdjudicationResult
        {
            PayerPayment = 100m,
            PaymentDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        var json = _projector.Project(claim);

        json["payment"]!["date"]!.GetValue<string>().Should().Be("2026-04-01");
        Amount(json["payment"]!).Should().Be(100m);
    }

    // ── header adjudication[] for denials and CARC/RARC trail ────────────────

    [Fact]
    public void Project_emits_header_denial_adjudication_when_DenialReasonCode_present()
    {
        var claim = MinimalClaim();
        claim.AdjudicationResult = new AdjudicationResult
        {
            DenialReasonCode = "29",
            DenialReason = "The time limit for filing has expired",
        };

        var json = _projector.Project(claim);

        var adjudication = json["adjudication"]!.AsArray();
        adjudication.Should().NotBeEmpty();

        var denial = adjudication[0]!;
        denial["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("29");
        denial["reason"]!["coding"]!.AsArray()[0]!["display"]!.GetValue<string>()
            .Should().Be("The time limit for filing has expired");
    }

    [Fact]
    public void Project_emits_one_header_adjudication_entry_per_AdjustmentReason()
    {
        var claim = MinimalClaim();
        claim.AdjudicationResult = new AdjudicationResult
        {
            AdjustmentReasons =
            {
                new ClaimAdjustmentReason
                {
                    GroupCode = "CO",
                    ReasonCode = "45",
                    Amount = 30m,
                    Description = "Charge exceeds fee schedule",
                },
                new ClaimAdjustmentReason
                {
                    GroupCode = "PR",
                    ReasonCode = "1",
                    Amount = 50m,
                    Description = "Deductible amount",
                }
            }
        };

        var json = _projector.Project(claim);
        var adjudication = json["adjudication"]!.AsArray();

        adjudication.Should().HaveCount(2);
        adjudication[0]!["category"]!["text"]!.GetValue<string>().Should().Be("CO");
        adjudication[0]!["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("45");
        Amount(adjudication[0]!).Should().Be(30m);
        adjudication[1]!["category"]!["text"]!.GetValue<string>().Should().Be("PR");
    }

    [Fact]
    public void Project_emits_header_adjudication_entry_per_RemarkCode_under_RARC_system()
    {
        var claim = MinimalClaim();
        claim.AdjudicationResult = new AdjudicationResult
        {
            RemarkCodes = { "M76", "N122" },
        };

        var json = _projector.Project(claim);
        var adjudication = json["adjudication"]!.AsArray();

        adjudication.Should().HaveCount(2);
        adjudication[0]!["category"]!["coding"]!.AsArray()[0]!["system"]!.GetValue<string>()
            .Should().Be("https://x12.org/codes/remittance-advice-remark-codes");
        adjudication[0]!["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("M76");
        adjudication[1]!["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("N122");
    }

    [Fact]
    public void Project_omits_header_adjudication_when_no_denial_or_reasons_or_remarks()
    {
        var claim = MinimalClaim();
        claim.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = 100m,
        };

        var json = _projector.Project(claim);

        json.ContainsKey("adjudication").Should().BeFalse(
            "header adjudication[] should be absent on a clean approval");
    }

    // ── item[].adjudication[] from NCCI/MUE edit failures (Decision 9) ──────

    [Fact]
    public void Project_emits_item_adjudication_for_each_NCCI_edit_failure()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1,
            ProcedureCode = "27447",
            ChargeAmount = 4500m,
            Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom,
            ServiceDateTo = claim.ServiceDateTo,
        });
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 2,
            ProcedureCode = "27486",
            ChargeAmount = 3200m,
            Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom,
            ServiceDateTo = claim.ServiceDateTo,
        });
        claim.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures =
            {
                new NcciEditFailureSnapshot
                {
                    EditType = "NcciPair",
                    RuleId = "NE001",
                    Column1Code = "27447",
                    Column2Code = "27486",
                    Message = "Component code bundled into comprehensive code",
                    AffectedLineNumbers = { 2 },
                    SuggestedCarc = "B15",
                    SuggestedRarc = "M51",
                },
            },
        };

        var json = _projector.Project(claim);
        var items = json["item"]!.AsArray();

        // Line 1 has no edit failure — no adjudication[] under it.
        items[0]!.AsObject().ContainsKey("adjudication").Should().BeFalse();

        // Line 2 carries the bundled edit.
        var line2Adj = items[1]!["adjudication"]!.AsArray();
        line2Adj.Should().HaveCount(1);
        line2Adj[0]!["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("B15");
        line2Adj[0]!["reason"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("27447-27486");
        line2Adj[0]!["reason"]!["coding"]!.AsArray()[0]!["display"]!.GetValue<string>()
            .Should().Be("Component code bundled into comprehensive code");
        line2Adj[0]!["extension"]!.AsArray()[0]!["url"]!.GetValue<string>()
            .Should().Be("urn:cho:ncci-rarc");
        line2Adj[0]!["extension"]!.AsArray()[0]!["valueString"]!.GetValue<string>()
            .Should().Be("M51");
    }

    [Fact]
    public void Project_uses_default_237_CARC_when_engine_did_not_supply_one()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "12345", ChargeAmount = 100m, Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures =
            {
                new NcciEditFailureSnapshot
                {
                    EditType = "NcciPair", RuleId = "NE001",
                    Column1Code = "12345", Column2Code = "67890",
                    AffectedLineNumbers = { 1 },
                    // SuggestedCarc deliberately null
                },
            },
        };

        var json = _projector.Project(claim);
        var line1Adj = json["item"]!.AsArray()[0]!["adjudication"]!.AsArray();

        line1Adj[0]!["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("237", "default CARC for engine-supplied edits without a SuggestedCarc");
    }

    [Fact]
    public void Project_falls_back_to_RuleId_for_MUE_failures_without_pair_codes()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "94010", ChargeAmount = 50m, Units = 5,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.PendDetails = new PendDetails
        {
            PendCode = "MUE",
            EditFailures =
            {
                new NcciEditFailureSnapshot
                {
                    EditType = "Mue", RuleId = "NE002",
                    Message = "Units exceed MUE limit",
                    AffectedLineNumbers = { 1 },
                    UnitsBilled = 5m, MueMaxUnits = 2,
                    SuggestedCarc = "151",
                },
            },
        };

        var json = _projector.Project(claim);
        var line1Adj = json["item"]!.AsArray()[0]!["adjudication"]!.AsArray();

        line1Adj[0]!["reason"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>()
            .Should().Be("NE002", "MUE failures fall back to RuleId because there is no Column1/Column2 pair");
    }

    [Fact]
    public void Project_drops_failures_with_no_AffectedLineNumbers()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures =
            {
                new NcciEditFailureSnapshot { RuleId = "X", AffectedLineNumbers = new List<int>() },
            },
        };

        var json = _projector.Project(claim);

        json["item"]!.AsArray()[0]!.AsObject().ContainsKey("adjudication").Should().BeFalse();
    }

    // ── supportingInfo[] (AI examination, Decision 5) ────────────────────────

    [Fact]
    public void Project_emits_AI_examination_supportingInfo_with_disposition_and_confidence()
    {
        var claim = MinimalClaim();
        claim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "Approve",
            ConfidenceScore = 0.83,
            ModelId = "claude-opus-4-7",
            PromptVersion = "ncci-pend-v1",
            GeneratedAt = new DateTime(2026, 4, 20, 10, 30, 0, DateTimeKind.Utc),
        };

        var json = _projector.Project(claim);
        var info = json["supportingInfo"]!.AsArray();
        info.Should().HaveCount(1);

        var entry = info[0]!;
        entry["sequence"]!.GetValue<int>().Should().Be(1);
        entry["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("info");
        entry["code"]!["coding"]!.AsArray()[0]!["system"]!.GetValue<string>()
            .Should().Be("urn:cho:ai-examination-disposition");
        entry["code"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>().Should().Be("Approve");
        entry["valueString"]!.GetValue<string>().Should().Be("Confidence: 0.83");
        entry["reason"]!["coding"]!.AsArray()[0]!["display"]!.GetValue<string>()
            .Should().Contain("model=claude-opus-4-7").And.Contain("prompt=ncci-pend-v1");
        entry["timingDateTime"]!.GetValue<string>().Should().StartWith("2026-04-20T10:30:00");
    }

    [Fact]
    public void Project_omits_AI_Rationale_and_PolicyCitations_from_FHIR_surface()
    {
        var claim = MinimalClaim();
        claim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "EscalateToHuman",
            ConfidenceScore = 0.4,
            Rationale = "SECRET LLM RATIONALE that should not leak to patient access",
            PolicyCitations = { "NCCI Manual Ch.1 §F.3" },
        };

        var json = _projector.Project(claim);
        var jsonText = json.ToJsonString();

        jsonText.Should().NotContain("SECRET LLM RATIONALE",
            "Decision 5 — Rationale is deliberately deferred from Phase 1 patient-access surface");
        jsonText.Should().NotContain("NCCI Manual",
            "Decision 5 — PolicyCitations are deliberately deferred from Phase 1");
    }

    [Fact]
    public void Project_omits_supportingInfo_when_AiExamination_null()
    {
        var claim = MinimalClaim();
        claim.AiExamination = null;

        var json = _projector.Project(claim);

        json.ContainsKey("supportingInfo").Should().BeFalse();
    }

    // ── End-to-end JSON parseability ─────────────────────────────────────────

    [Fact]
    public void Project_returns_JSON_that_parses_as_FHIR_resource_shape()
    {
        var claim = FullyPopulatedClaim();

        var json = _projector.Project(claim);
        var serialized = json.ToJsonString();
        using var doc = JsonDocument.Parse(serialized);

        doc.RootElement.GetProperty("resourceType").GetString().Should().Be("ExplanationOfBenefit");
        doc.RootElement.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("status").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("item").GetArrayLength().Should().BeGreaterThan(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Claim MinimalClaim() => new()
    {
        Id = "claim-1",
        ClaimVersionId = "claim-1",
        TenantId = "tenant-a",
        ClaimNumber = "CLM-0001",
        MemberId = "MEM-1",
        BillingProviderNPI = "1234567890",
        BillingProviderName = "Test Provider",
        ClaimType = ClaimType.Professional,
        Status = ClaimStatus.Submitted,
        SubmittedDate = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateFrom = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 100m,
        LineOfBusiness = LineOfBusiness.Commercial,
        PlaceOfServiceCode = "11",
        LastUpdatedDate = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Claim FullyPopulatedClaim()
    {
        var claim = MinimalClaim();
        claim.CoverageId = "COV-1";
        claim.Status = ClaimStatus.PartiallyPaid;
        claim.DiagnosisCodes.Add(new DiagnosisCode
        {
            Code = "M17.11", PointerNumber = 1, Description = "Osteoarthritis right knee",
        });
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "27447", ProcedureDescription = "TKA",
            ChargeAmount = 4500m, Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = 4000m,
            PayerPayment = 3200m,
            CopayAmount = 50m,
            DeductibleAmount = 250m,
            CoinsuranceAmount = 500m,
            PatientResponsibility = 800m,
            PaymentDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        claim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "Approve", ConfidenceScore = 0.91,
            ModelId = "claude-opus-4-7", PromptVersion = "ncci-pend-v1",
        };
        return claim;
    }

    private static string CategoryCode(JsonNode node) =>
        node["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();

    private static decimal Amount(JsonNode node) =>
        node["amount"]!["value"]!.GetValue<decimal>();
}
