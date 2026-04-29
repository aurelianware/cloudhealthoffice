using System.Text.Json.Nodes;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Services;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.8 — projector correctness, US Core / Plan-Net profile
/// structure assertions, edge cases (non-Active version returns null,
/// network enrichment, family-accumulator extension emission, ACA-cap
/// extension emission, cost-sharing projection across tiers).
/// </summary>
public sealed class FhirInsurancePlanProjectorTests
{
    private readonly FhirInsurancePlanProjector _projector = new();

    // ── status / version filtering ──────────────────────────────────────

    [Fact]
    public void Projects_active_published_plan()
    {
        var plan = MakePlan();

        var result = _projector.Project(plan);

        result.Should().NotBeNull();
        result!["resourceType"]!.GetValue<string>().Should().Be("InsurancePlan");
        result["id"]!.GetValue<string>().Should().Be(plan.PlanId);
        result["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public void Returns_null_for_non_published_versions()
    {
        var plan = MakePlan();
        plan.VersionState = PlanVersionState.Draft;

        _projector.Project(plan).Should().BeNull();

        plan.VersionState = PlanVersionState.Superseded;
        _projector.Project(plan).Should().BeNull();
    }

    [Fact]
    public void Returns_retired_status_when_terminated()
    {
        var plan = MakePlan();
        plan.EffectiveDate = DateTime.UtcNow.AddYears(-2);
        plan.TerminationDate = DateTime.UtcNow.AddDays(-30);

        var result = _projector.Project(plan);

        result.Should().NotBeNull();
        result!["status"]!.GetValue<string>().Should().Be("retired");
    }

    [Fact]
    public void Returns_null_for_future_effective_plan()
    {
        var plan = MakePlan();
        plan.EffectiveDate = DateTime.UtcNow.AddDays(30);

        _projector.Project(plan).Should().BeNull();
    }

    // ── identifier / type / name ────────────────────────────────────────

    [Fact]
    public void Emits_PlanId_identifier_under_cho_system()
    {
        var plan = MakePlan();

        var result = _projector.Project(plan)!;

        var identifier = result["identifier"]!.AsArray();
        identifier.Should().HaveCount(1);
        identifier[0]!["use"]!.GetValue<string>().Should().Be("official");
        identifier[0]!["system"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.PlanIdSystem);
        identifier[0]!["value"]!.GetValue<string>().Should().Be(plan.PlanId);
    }

    [Fact]
    public void Type_emits_two_codings_per_decision_8a()
    {
        var plan = MakePlan();
        plan.PlanType = PlanType.HMO;

        var result = _projector.Project(plan)!;

        var type = result["type"]!.AsArray();
        type.Should().HaveCount(1);
        var codings = type[0]!["coding"]!.AsArray();
        codings.Should().HaveCount(2);

        codings[0]!["system"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.InsurancePlanTypeSystem);
        codings[0]!["code"]!.GetValue<string>().Should().Be("medical");

        codings[1]!["system"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.PlanProductShapeSystem);
        codings[1]!["code"]!.GetValue<string>().Should().Be("HMO");
    }

    [Fact]
    public void Emits_name_period_and_owned_by()
    {
        var plan = MakePlan();
        plan.PlanName = "Aurelian Gold PPO 2026";
        plan.Payer = "AurelianHealth";

        var result = _projector.Project(plan)!;

        result["name"]!.GetValue<string>().Should().Be("Aurelian Gold PPO 2026");
        result["period"]!["start"]!.GetValue<string>()
            .Should().StartWith(plan.EffectiveDate.ToString("yyyy-MM-dd"));
        result["ownedBy"]!["display"]!.GetValue<string>().Should().Be("AurelianHealth");
        result["ownedBy"]!.AsObject()
            .ContainsKey("reference").Should().BeFalse(
                "Decision 12 — display-only ownedBy until Payer→Organization linking lands");
    }

    [Fact]
    public void Emits_endpoint_references_for_published_plan_documents()
    {
        var plan = MakePlan();
        plan.Documents = new List<PlanDocumentReference>
        {
            new()
            {
                Id = "doc-eoc",
                DocType = PlanDocumentType.EOC,
                Location = "https://example.com/eoc.pdf",
            },
            new()
            {
                Id = "doc-sbc",
                DocType = PlanDocumentType.SBC,
                Location = "https://example.com/sbc.pdf",
            },
        };

        var result = _projector.Project(plan)!;
        var endpoint = result["endpoint"]!.AsArray();

        endpoint.Should().HaveCount(2);
        // Decision 8 — SBC (consumer-facing) before EOC.
        endpoint[0]!["reference"]!.GetValue<string>().Should().Be("Endpoint/doc-sbc");
        endpoint[1]!["reference"]!.GetValue<string>().Should().Be("Endpoint/doc-eoc");

        // BP 5.8 Reference convention — no display field; the Endpoint
        // resource itself carries the operator-authored name.
        endpoint[0]!.AsObject().ContainsKey("display").Should().BeFalse();
    }

    [Fact]
    public void Endpoint_references_skip_internal_documentreference_locations()
    {
        var plan = MakePlan();
        plan.Documents = new List<PlanDocumentReference>
        {
            new()
            {
                Id = "doc-external",
                DocType = PlanDocumentType.SBC,
                Location = "https://example.com/sbc.pdf",
            },
            new()
            {
                Id = "doc-internal",
                DocType = PlanDocumentType.EOC,
                Location = "documentreference/abc-123",
            },
        };

        var result = _projector.Project(plan)!;
        var endpoint = result["endpoint"]!.AsArray();

        endpoint.Should().HaveCount(1,
            "internal documentreference/{id} entries are not externally addressable; " +
            "Endpoint requires an external URL");
        endpoint[0]!["reference"]!.GetValue<string>().Should().Be("Endpoint/doc-external");
    }

    [Fact]
    public void Endpoint_references_order_within_DocType_by_EffectiveDate_then_Id()
    {
        var plan = MakePlan();
        plan.Documents = new List<PlanDocumentReference>
        {
            new()
            {
                Id = "sbc-old",
                DocType = PlanDocumentType.SBC,
                Location = "https://example.com/sbc-old.pdf",
                EffectiveDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Id = "sbc-new",
                DocType = PlanDocumentType.SBC,
                Location = "https://example.com/sbc-new.pdf",
                EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                Id = "mrf",
                DocType = PlanDocumentType.MachineReadableRateFile,
                Location = "https://example.com/mrf.json",
            },
            new()
            {
                Id = "formulary",
                DocType = PlanDocumentType.Formulary,
                Location = "https://example.com/formulary.pdf",
            },
        };

        var result = _projector.Project(plan)!;
        var refs = result["endpoint"]!.AsArray()
            .Select(n => n!["reference"]!.GetValue<string>()).ToList();

        // Decision 8: SBC (1), EOC (2), Formulary (3), SPD (4), MRF (5), Other (6).
        // Within DocType, EffectiveDate desc then Id.
        refs.Should().Equal(
            "Endpoint/sbc-new",
            "Endpoint/sbc-old",
            "Endpoint/formulary",
            "Endpoint/mrf");
    }

    [Fact]
    public void Endpoint_array_is_empty_when_plan_has_no_documents()
    {
        var plan = MakePlan();

        var result = _projector.Project(plan)!;

        result["endpoint"]!.AsArray().Should().BeEmpty(
            "no projectable documents → empty array (cardinality 0..*)");
    }

    // ── meta + profiles ─────────────────────────────────────────────────

    [Fact]
    public void Emits_us_core_and_plan_net_profiles()
    {
        var plan = MakePlan();

        var result = _projector.Project(plan)!;

        var profiles = result["meta"]!["profile"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        profiles.Should().Contain(ChoBenefitPlanFhirUrls.UsCoreInsurancePlanProfile);
        profiles.Should().Contain(ChoBenefitPlanFhirUrls.PlanNetInsurancePlanProfile);
    }

    // ── network references (Decision 10) ────────────────────────────────

    [Fact]
    public void Emits_all_non_null_NetworkId_tiers_at_top_level_ordered_by_TierLevel()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Out-of-Network", TierLevel = 3, NetworkId = "net-oon" },
            new() { TierName = "Tier 1 In-Network", TierLevel = 1, NetworkId = "net-pri" },
            new() { TierName = "Tier 2 Preferred", TierLevel = 2, NetworkId = "net-sec" },
        };

        var result = _projector.Project(plan)!;
        var refs = result["network"]!.AsArray()
            .Select(n => n!["reference"]!.GetValue<string>()).ToList();

        refs.Should().Equal(
            "Organization/net-pri",
            "Organization/net-sec",
            "Organization/net-oon");
    }

    [Fact]
    public void Skips_NetworkTiers_with_null_NetworkId()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-1" },
            new() { TierName = "Tier 2", TierLevel = 2, NetworkId = null },
        };

        var result = _projector.Project(plan)!;
        var refs = result["network"]!.AsArray()
            .Select(n => n!["reference"]!.GetValue<string>()).ToList();

        refs.Should().ContainSingle().Which.Should().Be("Organization/net-1");
    }

    [Fact]
    public void Enriches_network_references_with_display_when_lookup_provided()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };

        var lookup = new[]
        {
            new OrganizationLookupResult
            {
                OrganizationId = "net-pri",
                Name = "Aurelian Primary Network",
            }
        };

        var result = _projector.Project(plan, lookup, acaLimits: null)!;
        var topLevel = result["network"]!.AsArray()[0]!.AsObject();
        topLevel["display"]!.GetValue<string>().Should().Be("Aurelian Primary Network");
    }

    [Fact]
    public void Omits_display_when_no_lookup_provided()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };

        var result = _projector.Project(plan)!;
        var topLevel = result["network"]!.AsArray()[0]!.AsObject();
        topLevel.ContainsKey("display").Should().BeFalse();
    }

    // ── coverage[] grouping ─────────────────────────────────────────────

    [Fact]
    public void Coverage_groups_benefits_by_BenefitType_discriminator()
    {
        var plan = MakePlan();
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit { ServiceCategory = "Office Visit" },
            new PharmacyBenefit { ServiceCategory = "Generic" },
            new MedicalBenefit { ServiceCategory = "Specialist" },
        };

        var result = _projector.Project(plan)!;

        var coverage = result["coverage"]!.AsArray();
        coverage.Should().HaveCount(2);

        var medical = coverage.FirstOrDefault(c =>
            c!["type"]!["text"]!.GetValue<string>() == "Medical");
        medical.Should().NotBeNull();
        medical!["benefit"]!.AsArray().Should().HaveCount(2);

        var pharmacy = coverage.FirstOrDefault(c =>
            c!["type"]!["text"]!.GetValue<string>() == "Pharmacy");
        pharmacy.Should().NotBeNull();
        pharmacy!["benefit"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void Coverage_benefit_emits_text_only_type_per_BP_5_6_incoherence()
    {
        var plan = MakePlan();
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit { ServiceCategory = "Mental Health Inpatient" },
        };

        var result = _projector.Project(plan)!;

        var benefitType = result["coverage"]!.AsArray()[0]!["benefit"]!.AsArray()[0]!["type"]!.AsObject();
        benefitType.ContainsKey("coding").Should().BeFalse(
            "BP 5.6 X12↔ServiceCategory incoherence — emit text-only until BP 5.10 lands");
        benefitType["text"]!.GetValue<string>().Should().Be("Mental Health Inpatient");
    }

    [Fact]
    public void Coverage_benefit_requirement_combines_prior_auth_and_limitations()
    {
        var plan = MakePlan();
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit
            {
                ServiceCategory = "MRI",
                PriorAuthRequired = true,
                Limitations = "Not covered for cosmetic indications.",
            },
        };

        var result = _projector.Project(plan)!;
        var requirement = result["coverage"]!.AsArray()[0]!["benefit"]!.AsArray()[0]!
            ["requirement"]!.GetValue<string>();

        requirement.Should().Contain("Prior authorization required");
        requirement.Should().Contain("cosmetic indications");
    }

    [Fact]
    public void Coverage_benefit_emits_visit_and_dollar_limits()
    {
        var plan = MakePlan();
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit
            {
                ServiceCategory = "Physical Therapy",
                VisitLimit = 60,
                VisitLimitPeriod = "year",
                AnnualMaximum = 5_000m,
            },
        };

        var result = _projector.Project(plan)!;
        var limit = result["coverage"]!.AsArray()[0]!["benefit"]!.AsArray()[0]!["limit"]!.AsArray();

        limit.Should().HaveCount(2);
        limit[0]!["value"]!["value"]!.GetValue<double>().Should().Be(60);
        limit[0]!["value"]!["unit"]!.GetValue<string>().Should().Be("year");
        limit[1]!["value"]!["currency"]!.GetValue<string>().Should().Be("USD");
        limit[1]!["value"]!["value"]!.GetValue<double>().Should().Be(5_000);
    }

    // ── plan[] with generalCost (Decision 11) ───────────────────────────

    [Fact]
    public void Plan_emits_one_entry_per_NetworkTier_with_NetworkId()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1 In-Network", TierLevel = 1, NetworkId = "net-pri" },
            new() { TierName = "Out-of-Network", TierLevel = 2, NetworkId = "net-oon" },
        };
        plan.CostSharing = new CostSharing
        {
            IndividualDeductible = 1_000m,
            FamilyDeductible = 2_000m,
            IndividualOutOfPocketMax = 5_000m,
            FamilyOutOfPocketMax = 10_000m,
            OutNetworkIndividualDeductible = 3_000m,
            OutNetworkFamilyDeductible = 6_000m,
            OutNetworkIndividualOutOfPocketMax = 12_000m,
            OutNetworkFamilyOutOfPocketMax = 24_000m,
        };

        var result = _projector.Project(plan)!;
        var planArray = result["plan"]!.AsArray();
        planArray.Should().HaveCount(2);
    }

    [Fact]
    public void GeneralCost_emits_individual_and_family_with_correct_groupSize()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };
        plan.CostSharing = new CostSharing
        {
            IndividualDeductible = 1_500m,
            FamilyDeductible = 3_000m,
            IndividualOutOfPocketMax = 6_000m,
            FamilyOutOfPocketMax = 12_000m,
        };

        var result = _projector.Project(plan)!;
        var general = result["plan"]!.AsArray()[0]!["generalCost"]!.AsArray();

        var indDed = general.FirstOrDefault(g =>
            g!["type"]!["text"]!.GetValue<string>() == "Deductible" &&
            g["groupSize"]!.GetValue<int>() == 1);
        indDed.Should().NotBeNull();
        indDed!["cost"]!["value"]!.GetValue<double>().Should().Be(1_500);

        var famOop = general.FirstOrDefault(g =>
            g!["type"]!["text"]!.GetValue<string>() == "Out-of-Pocket Maximum" &&
            g["groupSize"]!.GetValue<int>() == 2);
        famOop.Should().NotBeNull();
        famOop!["cost"]!["value"]!.GetValue<double>().Should().Be(12_000);
    }

    [Fact]
    public void GeneralCost_emits_aca_cap_entry_for_aggregate_when_enforced_with_limits()
    {
        var plan = MakePlan();
        plan.FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate;
        plan.PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(1);
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1 In-Network", TierLevel = 1, NetworkId = "net-pri" },
        };

        var acaLimits = new AcaLimits(2026, 10_600m, 21_200m);
        var result = _projector.Project(plan, networks: null, acaLimits: acaLimits)!;

        var general = result["plan"]!.AsArray()[0]!["generalCost"]!.AsArray();
        var acaEntry = general.FirstOrDefault(g =>
            g!["type"]!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>() == "aca-individual-cap");

        acaEntry.Should().NotBeNull(
            "Decision 11 dual emission — ACA cap projects as a generalCost entry");
        acaEntry!["groupSize"]!.GetValue<int>().Should().Be(1);
        acaEntry["cost"]!["value"]!.GetValue<double>().Should().Be(10_600);
        acaEntry["comment"]!.GetValue<string>().Should().Contain("45 CFR §156.130");

        // Per-cost extension flag — Decision 11 disambiguates from a real
        // plan-level individual cap.
        var ext = acaEntry["extension"]!.AsArray();
        ext.Should().ContainSingle();
        ext[0]!["url"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.AcaCapEnforcedExt);
        ext[0]!["valueBoolean"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void GeneralCost_does_not_emit_aca_cap_for_legacy_aggregate_plan()
    {
        var plan = MakePlan();
        plan.FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate;
        plan.PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(-30); // legacy
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };
        plan.CostSharing = new CostSharing
        {
            IndividualDeductible = 1_000m,
            IndividualOutOfPocketMax = 5_000m,
        };

        var result = _projector.Project(plan, networks: null,
            acaLimits: new AcaLimits(2026, 10_600m, 21_200m))!;

        var general = result["plan"]!.AsArray()[0]!["generalCost"]!.AsArray();
        general.Should().NotBeEmpty();
        general.Any(g =>
            g!["type"]!["coding"]?.AsArray()[0]?["code"]?.GetValue<string>() == "aca-individual-cap")
            .Should().BeFalse(
                "legacy Aggregate plans (PublishedAt < cutoff) don't emit the ACA cap");
    }

    [Fact]
    public void GeneralCost_emits_aca_cap_only_on_primary_in_network_tier()
    {
        // Decision 11 contract — the per-member cap projects on exactly
        // one generalCost block, the lowest-TierLevel in-network tier.
        // A plan with Tier 1 + Tier 2 Preferred (both in-network) plus
        // an OON tier must emit the cap exactly once.
        var plan = MakePlan();
        plan.FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate;
        plan.PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(1);
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1 In-Network",  TierLevel = 1, NetworkId = "net-pri" },
            new() { TierName = "Tier 2 Preferred",   TierLevel = 2, NetworkId = "net-sec" },
            new() { TierName = "Out-of-Network",     TierLevel = 3, NetworkId = "net-oon" },
        };
        plan.CostSharing = new CostSharing
        {
            IndividualDeductible = 1_000m,
            IndividualOutOfPocketMax = 5_000m,
        };

        var result = _projector.Project(plan, networks: null,
            acaLimits: new AcaLimits(2026, 10_600m, 21_200m))!;

        var planArray = result["plan"]!.AsArray();
        var acaEntries = planArray
            .SelectMany(p => p!["generalCost"]?.AsArray() ?? new JsonArray())
            .Where(g =>
                g!["type"]!["coding"]?.AsArray()[0]?["code"]?.GetValue<string>() == "aca-individual-cap")
            .ToList();

        acaEntries.Should().ContainSingle(
            "the ACA cap must not duplicate across in-network tiers");
    }

    [Fact]
    public void GeneralCost_does_not_emit_aca_cap_for_embedded_plan()
    {
        var plan = MakePlan();
        plan.FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded;
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };
        plan.CostSharing = new CostSharing
        {
            IndividualDeductible = 1_000m,
            IndividualOutOfPocketMax = 5_000m,
        };

        var result = _projector.Project(plan, networks: null,
            acaLimits: new AcaLimits(2026, 10_600m, 21_200m))!;

        var general = result["plan"]!.AsArray()[0]!["generalCost"]!.AsArray();
        general.Should().NotBeEmpty();
        general.Any(g =>
            g!["type"]!["coding"]?.AsArray()[0]?["code"]?.GetValue<string>() == "aca-individual-cap")
            .Should().BeFalse("Embedded plans never emit the ACA cap entry");
    }

    // ── plan[].specificCost ─────────────────────────────────────────────

    [Fact]
    public void SpecificCost_emits_per_benefit_cost_with_two_qualifiers()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1 In-Network", TierLevel = 1, NetworkId = "net-pri" },
        };
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit
            {
                ServiceCategory = "Office Visit",
                InNetworkCopay = 25m,
                InNetworkCoinsurance = 0.20m,
            },
        };

        var result = _projector.Project(plan)!;
        var specific = result["plan"]!.AsArray()[0]!["specificCost"]!.AsArray();

        specific.Should().HaveCount(1);
        var costs = specific[0]!["benefit"]!.AsArray()[0]!["cost"]!.AsArray();
        costs.Should().HaveCount(2);

        var copay = costs.FirstOrDefault(c =>
            c!["qualifiers"]!.AsArray().Any(q =>
                q!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>() == "copay"));
        copay.Should().NotBeNull();
        copay!["value"]!["currency"]!.GetValue<string>().Should().Be("USD");
        copay["value"]!["value"]!.GetValue<double>().Should().Be(25);

        var coins = costs.FirstOrDefault(c =>
            c!["qualifiers"]!.AsArray().Any(q =>
                q!["coding"]!.AsArray()[0]!["code"]!.GetValue<string>() == "coinsurance"));
        coins.Should().NotBeNull();
        coins!["value"]!["unit"]!.GetValue<string>().Should().Be("%");
        coins["value"]!["value"]!.GetValue<double>().Should().Be(20);
    }

    [Fact]
    public void Coinsurance_above_one_is_treated_as_already_in_percent()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit
            {
                ServiceCategory = "Specialist",
                InNetworkCoinsurance = 30m, // already a percent value
            },
        };

        var result = _projector.Project(plan)!;
        var costs = result["plan"]!.AsArray()[0]!["specificCost"]!.AsArray()[0]!
            ["benefit"]!.AsArray()[0]!["cost"]!.AsArray();
        costs[0]!["value"]!["value"]!.GetValue<double>().Should().Be(30);
    }

    // ── extensions (Decision 13) ────────────────────────────────────────

    [Fact]
    public void Top_level_extension_includes_family_accumulator_model()
    {
        var plan = MakePlan();
        plan.FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate;

        var result = _projector.Project(plan)!;
        var extensions = result["extension"]!.AsArray();

        var fam = extensions.FirstOrDefault(e =>
            e!["url"]!.GetValue<string>() == ChoBenefitPlanFhirUrls.FamilyAccumulatorModelExt);
        fam.Should().NotBeNull();
        fam!["valueCode"]!.GetValue<string>().Should().Be("Aggregate");
    }

    [Fact]
    public void Top_level_aca_cap_extension_only_emitted_when_enforced()
    {
        var enforced = MakePlan();
        enforced.FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate;
        enforced.PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(1);

        var enforcedResult = _projector.Project(enforced)!;
        var enforcedExt = enforcedResult["extension"]!.AsArray()
            .Any(e => e!["url"]!.GetValue<string>() == ChoBenefitPlanFhirUrls.AcaCapEnforcedExt);
        enforcedExt.Should().BeTrue();

        var legacy = MakePlan();
        legacy.FamilyAccumulatorModel = FamilyAccumulatorModel.Aggregate;
        legacy.PublishedAt = AcaCapEnforcementPolicy.CutoffUtc.AddDays(-30);

        var legacyResult = _projector.Project(legacy)!;
        var legacyExt = legacyResult["extension"]!.AsArray()
            .Any(e => e!["url"]!.GetValue<string>() == ChoBenefitPlanFhirUrls.AcaCapEnforcedExt);
        legacyExt.Should().BeFalse();
    }

    // ── deterministic output ────────────────────────────────────────────

    [Fact]
    public void Repeated_projection_produces_identical_json()
    {
        var plan = MakePlan();
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit
            {
                ServiceCategory = "Office Visit",
                InNetworkCopay = 25m,
            },
        };
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };

        var first = _projector.Project(plan)!;
        var second = _projector.Project(plan)!;

        first.ToJsonString().Should().Be(second.ToJsonString());
    }

    [Fact]
    public void Network_references_can_be_round_tripped_to_json()
    {
        var plan = MakePlan();
        plan.NetworkTiers = new List<NetworkTier>
        {
            new() { TierName = "Tier 1", TierLevel = 1, NetworkId = "net-pri" },
        };
        plan.Benefits = new List<Benefit>
        {
            new MedicalBenefit { ServiceCategory = "Office Visit" },
        };

        var result = _projector.Project(plan)!;
        // Coverage entry repeats top-level networks. If the projector
        // accidentally shared JsonNode identity instead of cloning,
        // ToJsonString() would throw "node already has a parent."
        var roundTripped = JsonNode.Parse(result.ToJsonString())!.AsObject();
        roundTripped["coverage"]!.AsArray()[0]!["network"]!.AsArray()[0]!["reference"]!
            .GetValue<string>().Should().Be("Organization/net-pri");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static BenefitPlan MakePlan() => new()
    {
        Id = Guid.NewGuid().ToString(),
        TenantId = "tenant-a",
        PlanId = "AUR-GOLD-PPO-2026",
        PlanName = "Aurelian Gold PPO 2026",
        Payer = "AurelianHealth",
        EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        PlanType = PlanType.PPO,
        LineOfBusiness = LineOfBusiness.Commercial,
        VersionState = PlanVersionState.Published,
        VersionNumber = 1,
        VersionId = Guid.NewGuid().ToString(),
        PublishedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        FamilyAccumulatorModel = FamilyAccumulatorModel.Embedded,
        Benefits = new List<Benefit>(),
        NetworkTiers = new List<NetworkTier>(),
        CostSharing = new CostSharing(),
    };
}
