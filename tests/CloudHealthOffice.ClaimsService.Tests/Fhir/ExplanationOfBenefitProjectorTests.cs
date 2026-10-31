using System.Text.Json;
using System.Text.Json.Nodes;
using ClaimsService.Fhir;
using ClaimsService.Models;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Fhir;

/// <summary>
/// Unit tests for the Patient Access ExplanationOfBenefit projection, which
/// now declares and satisfies the CARIN BB 2.2.0 <b>financial</b> profiles.
///
/// <para>
/// The shaping the two EOB projections share is tested through the Basis
/// goldens and <c>CarinNonFinancialEobProjectorTests</c>; what these cover is
/// the money and the reasons this projector adds — the total set, the
/// adjudication amount per line, and the denial, adjustment and remark trail as
/// <c>adjustmentreason</c> slices.
/// </para>
///
/// <para>
/// Several of these tests changed shape when the profile was declared, and the
/// reason is the same in each case: CARIN BB binds adjudication
/// <c>category</c> to its own discriminator value set and puts the X12 code in
/// <c>reason</c>. Before, each CARC was emitted as the category, which named a
/// code where a consumer looks for a discriminator. The codes did not change;
/// where they sit did. What the instances now satisfy is checked by the HL7
/// validator over <c>conformance/golden/</c>, not here.
/// </para>
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
    public void Project_names_the_configured_plan_organization_as_insurer()
    {
        var projector = new ExplanationOfBenefitProjector(
            new CarinEobInsurerOptions { Reference = "Organization/plan", Display = "Example Health Plan" });

        var insurer = projector.Project(MinimalClaim())["insurer"]!;

        insurer["reference"]!.GetValue<string>().Should().Be("Organization/plan");
        insurer["display"]!.GetValue<string>().Should().Be("Example Health Plan");
    }

    [Theory]
    [InlineData("2", "Organization/1234567890")]
    [InlineData("1", "Practitioner/1234567890")]
    [InlineData(null, null)]
    public void Project_types_the_billing_provider_only_when_the_837_said_what_it_is(string? entityType, string? reference)
    {
        var claim = MinimalClaim();
        claim.BillingProviderEntityType = entityType;

        var provider = _projector.Project(claim)["provider"]!;

        provider["reference"]?.GetValue<string>().Should().Be(reference);
        provider["identifier"]!["value"]!.GetValue<string>().Should().Be("1234567890");
    }

    [Fact]
    public void Project_declares_the_financial_profile_with_and_without_the_2_0_version()
    {
        // CARIN BB 2.0.0's EOB-*-meta-profile-version invariants are errors and
        // want the canonical followed by "|2.0." (nightly 2026-10-30).
        var json = _projector.Project(MinimalClaim());

        json["meta"]!["profile"]!.AsArray().Select(p => p!.GetValue<string>()).Should().Equal(
            "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-ExplanationOfBenefit-Professional-NonClinician",
            "http://hl7.org/fhir/us/carin-bb/StructureDefinition/C4BB-ExplanationOfBenefit-Professional-NonClinician|2.0.0");
    }

    [Fact]
    public void Project_pays_the_billing_provider_when_the_837_names_no_pay_to_plan()
    {
        var json = _projector.Project(MinimalClaim());

        json["payee"]!["type"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("provider");
        json["payee"]!["type"]!["coding"]![0]!["system"]!.GetValue<string>()
            .Should().Be("http://terminology.hl7.org/CodeSystem/payeetype");
        json["payee"]!["party"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("1234567890");
    }

    [Fact]
    public void Project_pays_the_pay_to_plan_on_a_subrogation_demand()
    {
        var claim = MinimalClaim();
        claim.PayToPlan = new ClaimPayToPlan { Name = "Subrogation Plan", Identifier = "PLAN-9" };

        var json = _projector.Project(claim);

        json["payee"]!["type"]!["coding"]![0]!["code"]!.GetValue<string>().Should().Be("other");
        json["payee"]!["party"]!["display"]!.GetValue<string>().Should().Be("Subrogation Plan",
            "C4BB requires a party when the payee type is other");
        json["payee"]!["party"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("PLAN-9");
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
    public void Project_dots_a_diagnosis_stored_the_way_the_837_sends_it()
    {
        // Inferno run 40979709678 rejected J189 as an unknown ICD-10-CM code.
        var claim = MinimalClaim();
        claim.DiagnosisCodes.Add(new DiagnosisCode { Code = "J189", PointerNumber = 1, CodeQualifier = "ABK" });

        var json = _projector.Project(claim);

        json["diagnosis"]![0]!["diagnosisCodeableConcept"]!["coding"]![0]!["code"]!.GetValue<string>()
            .Should().Be("J18.9");
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

    /// <summary>
    /// <c>status</c> is the resource's fm-status and <c>outcome</c> is how far
    /// adjudication got — the rule the Provider Access projection of the same
    /// claim already followed, and now the only rule, because one claim cannot
    /// be <c>draft</c> on one API and <c>active</c> on the other.
    ///
    /// <para>
    /// Two values changed with it. A claim in adjudication is <c>active</c>
    /// rather than <c>draft</c>: it exists and stands, and <c>draft</c> in FHIR
    /// means a resource not yet in force. And a denial is <c>complete</c> rather
    /// than <c>error</c>: the claim was processed and the answer was no, where
    /// <c>error</c> says it could not be processed at all.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ClaimStatus.Submitted, "active", "queued")]
    [InlineData(ClaimStatus.Received, "active", "queued")]
    [InlineData(ClaimStatus.InAdjudication, "active", "queued")]
    [InlineData(ClaimStatus.Pended, "active", "queued")]
    [InlineData(ClaimStatus.Approved, "active", "complete")]
    [InlineData(ClaimStatus.Paid, "active", "complete")]
    [InlineData(ClaimStatus.PartiallyPaid, "active", "partial")]
    [InlineData(ClaimStatus.Denied, "active", "complete")]
    [InlineData(ClaimStatus.Voided, "cancelled", "complete")]
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
    public void Project_names_the_plan_by_display_when_no_CoverageId_is_on_the_claim()
    {
        var claim = MinimalClaim();
        claim.CoverageId = null;

        var json = _projector.Project(claim);

        var insurance = json["insurance"]!.AsArray();
        insurance.Should().HaveCount(1,
            "CARIN BB makes insurance 1..* with a required coverage, so it can never be omitted");
        insurance[0]!["coverage"]!.AsObject().ContainsKey("reference").Should().BeFalse(
            "a literal reference to a Coverage that would 404 is worse than one that says what is known");
        insurance[0]!["coverage"]!["display"]!.GetValue<string>().Should().Be("CloudHealthOffice");
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

        // Coinsurance and member liability are C4BB's own codes: the FHIR
        // adjudication system defines neither, and "patientresponsibility" —
        // which this projection emitted before — is not a code in any system, so
        // it was never one a consumer could resolve.
        var coinsurance = totals.Single(t => CategoryCode(t!) == "coinsurance")!;
        CategorySystem(coinsurance).Should().Be(C4bbAdjudication);
        Amount(coinsurance).Should().Be(35m);

        var memberLiability = totals.Single(t => CategoryCode(t!) == "memberliability")!;
        CategorySystem(memberLiability).Should().Be(C4bbAdjudication);
        Amount(memberLiability).Should().Be(110m);

        totals.Should().NotContain(t => CategoryCode(t!) == "patientresponsibility");
    }

    [Fact]
    public void Project_still_states_what_was_submitted_when_the_claim_is_not_adjudicated()
    {
        var claim = MinimalClaim();
        claim.TotalChargeAmount = 100m;
        claim.AdjudicationResult = null;

        var json = _projector.Project(claim);
        var totals = json["total"]!.AsArray();

        totals.Should().HaveCount(1,
            "total is 1..* on every CARIN BB financial profile, and what was submitted is known "
            + "from the moment the claim arrives — the plan's share is not");
        CategoryCode(totals[0]!).Should().Be("submitted");
        Amount(totals[0]!).Should().Be(100m);

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

        var denial = AdjustmentReasons(json).Should().ContainSingle().Subject;

        CategoryCode(denial).Should().Be("adjustmentreason",
            "CARIN BB binds category to its own discriminator; the X12 code goes in reason");
        CategorySystem(denial).Should().Be(Discriminator);
        ReasonCoding(denial, Carc)["code"]!.GetValue<string>().Should().Be("29");
        ReasonCoding(denial, Carc)["display"]!.GetValue<string>()
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
        var reasons = AdjustmentReasons(json);

        reasons.Should().HaveCount(2);
        ReasonCoding(reasons[0], Carc)["code"]!.GetValue<string>().Should().Be("45");
        Amount(reasons[0]).Should().Be(30m);

        // The X12 group code says who the adjustment falls to. It is not a CARC
        // and has no element of its own, so it is the reason's text rather than a
        // coding that would fail the bound value set.
        reasons[0]["reason"]!["text"]!.GetValue<string>().Should().Be("CO");
        reasons[1]["reason"]!["text"]!.GetValue<string>().Should().Be("PR");
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
        var reasons = AdjustmentReasons(json);

        reasons.Should().HaveCount(2);

        // A remark is an adjustment reason too: the value set CARIN BB binds the
        // reason to draws from both the CARC and the RARC code systems, so a
        // remark does not need — and may not have — a category of its own.
        ReasonCoding(reasons[0], Rarc)["code"]!.GetValue<string>().Should().Be("M76");
        ReasonCoding(reasons[1], Rarc)["code"]!.GetValue<string>().Should().Be("N122");
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

        AdjustmentReasons(json).Should().BeEmpty(
            "a clean approval has no adjustment trail to state");
    }

    // ── item[].net / unitPrice (ChargeAmount = line total) ─────────────────

    [Fact]
    public void Project_item_net_is_line_total_and_unitPrice_is_derived()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "97110", ChargeAmount = 200m, Units = 4,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });

        var item = _projector.Project(claim)["item"]!.AsArray()[0]!;

        item["quantity"]!["value"]!.GetValue<decimal>().Should().Be(4m);
        item["net"]!["value"]!.GetValue<decimal>().Should().Be(200m,
            "ClaimLine.ChargeAmount is already the line total (SV102/SV203)");
        item["unitPrice"]!["value"]!.GetValue<decimal>().Should().Be(50m);
    }

    [Fact]
    public void Project_item_unitPrice_rounds_to_cents_and_is_omitted_for_zero_units()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "97110", ChargeAmount = 100m, Units = 3,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 2, ProcedureCode = "99213", ChargeAmount = 75m, Units = 0,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });

        var items = _projector.Project(claim)["item"]!.AsArray();

        items[0]!["net"]!["value"]!.GetValue<decimal>().Should().Be(100m);
        items[0]!["unitPrice"]!["value"]!.GetValue<decimal>().Should().Be(33.33m);
        items[1]!["net"]!["value"]!.GetValue<decimal>().Should().Be(75m);
        items[1]!.AsObject().ContainsKey("unitPrice").Should().BeFalse();
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

        // Line 1 has no edit failure — only the entries the profile requires.
        AdjustmentReasons(items[0]!).Should().BeEmpty();

        // Line 2 carries the bundled edit.
        var edit = AdjustmentReasons(items[1]!).Should().ContainSingle().Subject;
        ReasonCoding(edit, Carc)["code"]!.GetValue<string>().Should().Be("B15");

        // The engine rule rides alongside the CARC as a second coding in the
        // same concept, so the reason says both what the adjustment is and which
        // rule produced it.
        ReasonCoding(edit, NcciEdit)["code"]!.GetValue<string>().Should().Be("27447-27486");
        ReasonCoding(edit, NcciEdit)["display"]!.GetValue<string>()
            .Should().Be("Component code bundled into comprehensive code");

        // The suggested RARC is a third coding rather than the CHO-private
        // extension it used to be. A urn: canonical can never be dereferenced,
        // so the validator reports such an extension as not allowed; the remark
        // belongs in the bound value set, which it is a member of.
        ReasonCoding(edit, Rarc)["code"]!.GetValue<string>().Should().Be("M51");
        edit.AsObject().ContainsKey("extension").Should().BeFalse();
    }

    [Fact]
    public void Project_codes_duplicate_findings_under_duplicate_edit_system_not_ncci()
    {
        var claim = MinimalClaim();
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m, Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 2, ProcedureCode = "36415", ChargeAmount = 50m, Units = 1,
            ServiceDateFrom = claim.ServiceDateFrom, ServiceDateTo = claim.ServiceDateTo,
        });
        claim.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            EditFailures =
            {
                new NcciEditFailureSnapshot
                {
                    EditType = "Mue", RuleId = "NE002", Message = "MUE exceeded",
                    AffectedLineNumbers = { 2 }, SuggestedCarc = "151",
                },
            },
            DuplicateFindings =
            {
                new DuplicateFindingSnapshot
                {
                    DuplicateType = "Exact", RuleId = "DUP001", LineNumber = 2,
                    Message = "Line 2 vs claim CN-PRIOR (prior-1) line 2: identical service",
                    MatchedClaimId = "prior-1", MatchedClaimNumber = "CN-PRIOR", MatchedLineNumber = 2,
                    SuggestedCarc = "18",
                },
                new DuplicateFindingSnapshot
                {
                    DuplicateType = "Suspect", RuleId = "DUP002", LineNumber = 1,
                    Message = "Line 1 vs claim CN-PRIOR (prior-1) line 1: different modifiers",
                    SuggestedCarc = null,
                },
            },
        };

        var json = _projector.Project(claim);
        var items = json["item"]!.AsArray();

        var suspect = AdjustmentReasons(items[0]!).Should().ContainSingle().Subject;
        ReasonCoding(suspect, Carc)["code"]!.GetValue<string>()
            .Should().Be("18", "CARC 18 is the fallback for duplicate findings");
        ReasonCoding(suspect, DuplicateEdit)["code"]!.GetValue<string>().Should().Be("DUP002");

        // Line 2 carries both the NCCI edit and the duplicate, each under its own system.
        var line2 = AdjustmentReasons(items[1]!);
        line2.Should().HaveCount(2);

        var duplicate = line2.Should().ContainSingle(a => HasCoding(a, DuplicateEdit)).Subject;
        ReasonCoding(duplicate, DuplicateEdit)["code"]!.GetValue<string>().Should().Be("DUP001");
        ReasonCoding(duplicate, Carc)["code"]!.GetValue<string>().Should().Be("18");

        line2.Should().ContainSingle(a => HasCoding(a, NcciEdit));
        json.ToJsonString().Should().NotContain("\"urn:cho:ncci-edit\",\"code\":\"DUP");
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
        var edit = AdjustmentReasons(json["item"]!.AsArray()[0]!).Should().ContainSingle().Subject;

        ReasonCoding(edit, Carc)["code"]!.GetValue<string>()
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
        var edit = AdjustmentReasons(json["item"]!.AsArray()[0]!).Should().ContainSingle().Subject;

        ReasonCoding(edit, NcciEdit)["code"]!.GetValue<string>()
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

        AdjustmentReasons(json["item"]!.AsArray()[0]!).Should().BeEmpty();
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
        entry["sequence"]!.GetValue<int>().Should().Be(1,
            "the entry continues the numbering of whatever the profile's own supporting-info "
            + "slices put in the array first — here, nothing");
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

        json.ContainsKey("supportingInfo").Should().BeFalse(
            "this claim carries nothing else the profile's supporting-info slices ask for either");
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

    private const string Discriminator =
        "http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBAdjudicationDiscriminator";
    private const string C4bbAdjudication =
        "http://hl7.org/fhir/us/carin-bb/CodeSystem/C4BBAdjudication";
    private const string Carc = "https://x12.org/codes/claim-adjustment-reason-codes";
    private const string Rarc = "https://x12.org/codes/remittance-advice-remark-codes";
    private const string NcciEdit = "urn:cho:ncci-edit";
    private const string DuplicateEdit = "urn:cho:duplicate-edit";

    private static string CategoryCode(JsonNode node) =>
        node["category"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>();

    private static string CategorySystem(JsonNode node) =>
        node["category"]!["coding"]!.AsArray()[0]!["system"]!.GetValue<string>();

    private static decimal Amount(JsonNode node) =>
        node["amount"]!["value"]!.GetValue<decimal>();

    /// <summary>
    /// The <c>adjustmentreason</c> slices of an owner's adjudication array —
    /// the resource header's or one item's. Picking them out by discriminator
    /// rather than by position is the point: the entries the profile requires
    /// share the array, and an assertion on <c>[0]</c> would silently start
    /// reading one of those instead.
    /// </summary>
    private static List<JsonNode> AdjustmentReasons(JsonNode owner) =>
        (owner["adjudication"]?.AsArray() ?? new JsonArray())
            .Where(entry => entry is not null && CategoryCode(entry) == "adjustmentreason")
            .Select(entry => entry!)
            .ToList();

    private static JsonObject ReasonCoding(JsonNode adjustment, string system) =>
        adjustment["reason"]!["coding"]!.AsArray()
            .Single(coding => coding!["system"]!.GetValue<string>() == system)!
            .AsObject();

    private static bool HasCoding(JsonNode adjustment, string system) =>
        adjustment["reason"]!["coding"]!.AsArray()
            .Any(coding => coding!["system"]!.GetValue<string>() == system);
}
