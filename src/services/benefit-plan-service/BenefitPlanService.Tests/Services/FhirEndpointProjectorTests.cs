using System.Text.Json.Nodes;
using BenefitPlanService.Models;
using BenefitPlanService.Services;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.9 — projector correctness for
/// <see cref="FhirEndpointProjector"/>. Mirrors the BP 5.8
/// <see cref="FhirInsurancePlanProjectorTests"/> test surface (one Endpoint
/// per projectable PlanDocumentReference, ordering, status derivation,
/// determinism, internal-reference skip).
/// </summary>
public sealed class FhirEndpointProjectorTests
{
    private readonly FhirEndpointProjector _projector = new();

    // ── projection happy path ───────────────────────────────────────────

    [Fact]
    public void Projects_one_endpoint_per_projectable_document()
    {
        var plan = MakePlan();
        plan.Documents = new List<PlanDocumentReference>
        {
            new()
            {
                Id = "doc-sbc",
                DocType = PlanDocumentType.SBC,
                Location = "https://example.com/sbc.pdf",
                ContentType = "application/pdf",
            },
            new()
            {
                Id = "doc-formulary",
                DocType = PlanDocumentType.Formulary,
                Location = "https://example.com/formulary.pdf",
                ContentType = "application/pdf",
            },
        };

        var endpoints = _projector.ProjectAll(plan);

        endpoints.Should().HaveCount(2);
        endpoints[0]!["resourceType"]!.GetValue<string>().Should().Be("Endpoint");
    }

    [Fact]
    public void Endpoint_id_is_PlanDocumentReference_id_verbatim()
    {
        var plan = MakePlan();
        var doc = MakeDoc("doc-sbc", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        plan.Documents.Add(doc);

        var endpoint = _projector.Project(plan, doc)!;

        endpoint["id"]!.GetValue<string>().Should().Be("doc-sbc",
            "Decision 2 — Endpoint.id is the source-system identifier verbatim");
    }

    [Fact]
    public void Connection_type_carries_static_document_under_cho_system()
    {
        var plan = MakePlan();
        var doc = MakeDoc("doc-sbc", PlanDocumentType.SBC, "https://example.com/sbc.pdf");

        var endpoint = _projector.Project(plan, doc)!;

        var coding = endpoint["connectionType"]!.AsObject();
        coding["system"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.EndpointConnectionTypeSystem);
        coding["code"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.EndpointConnectionTypeStaticDocument);
    }

    [Fact]
    public void PayloadType_coding_uses_per_DocType_code_under_cho_system()
    {
        var plan = MakePlan();
        var sbc = MakeDoc("doc-sbc", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        var mrf = MakeDoc("doc-mrf", PlanDocumentType.MachineReadableRateFile, "https://example.com/mrf.json");

        var sbcCoding = _projector.Project(plan, sbc)!["payloadType"]!.AsArray()[0]!["coding"]!.AsArray()[0]!;
        sbcCoding["system"]!.GetValue<string>()
            .Should().Be(ChoBenefitPlanFhirUrls.PlanDocumentTypeSystem);
        sbcCoding["code"]!.GetValue<string>().Should().Be("sbc");

        var mrfCoding = _projector.Project(plan, mrf)!["payloadType"]!.AsArray()[0]!["coding"]!.AsArray()[0]!;
        mrfCoding["code"]!.GetValue<string>().Should().Be("mrf");
    }

    [Fact]
    public void Address_passes_through_https_location_verbatim()
    {
        var plan = MakePlan();
        var doc = MakeDoc("doc-eoc", PlanDocumentType.EOC, "https://example.com/eoc.pdf");

        var endpoint = _projector.Project(plan, doc)!;

        endpoint["address"]!.GetValue<string>().Should().Be("https://example.com/eoc.pdf");
    }

    [Fact]
    public void PayloadMimeType_passes_through_when_set_and_omitted_when_null()
    {
        var plan = MakePlan();
        var withMime = MakeDoc("doc-1", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        withMime.ContentType = "application/pdf";
        var noMime = MakeDoc("doc-2", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        noMime.ContentType = null;

        var withMimeEndpoint = _projector.Project(plan, withMime)!;
        withMimeEndpoint["payloadMimeType"]!.AsArray()[0]!.GetValue<string>()
            .Should().Be("application/pdf");

        var noMimeEndpoint = _projector.Project(plan, noMime)!;
        noMimeEndpoint.AsObject().ContainsKey("payloadMimeType").Should().BeFalse(
            "Decision 6 — pass-through only; never infer ContentType");
    }

    [Fact]
    public void Name_prefers_DisplayName_then_falls_back_to_DocType_display()
    {
        var plan = MakePlan();
        var withDisplay = MakeDoc("doc-1", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        withDisplay.DisplayName = "2026 Aurelian Gold SBC";
        var noDisplay = MakeDoc("doc-2", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        noDisplay.DisplayName = null;

        _projector.Project(plan, withDisplay)!["name"]!.GetValue<string>()
            .Should().Be("2026 Aurelian Gold SBC");
        _projector.Project(plan, noDisplay)!["name"]!.GetValue<string>()
            .Should().Be("Summary of Benefits and Coverage");
    }

    [Fact]
    public void Meta_profile_is_plan_net_endpoint()
    {
        var plan = MakePlan();
        var doc = MakeDoc("doc-sbc", PlanDocumentType.SBC, "https://example.com/sbc.pdf");

        var endpoint = _projector.Project(plan, doc)!;

        var profiles = endpoint["meta"]!["profile"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToList();
        profiles.Should().Contain(ChoBenefitPlanFhirUrls.PlanNetEndpointProfile);
    }

    // ── status derivation (Decision 5) ──────────────────────────────────

    [Fact]
    public void Status_active_when_effective_date_is_null_or_in_past()
    {
        var plan = MakePlan();
        var nullEffective = MakeDoc("d1", PlanDocumentType.SBC, "https://example.com/a.pdf");
        var pastEffective = MakeDoc("d2", PlanDocumentType.SBC, "https://example.com/b.pdf");
        pastEffective.EffectiveDate = DateTime.UtcNow.AddYears(-1);

        _projector.Project(plan, nullEffective)!["status"]!.GetValue<string>().Should().Be("active");
        _projector.Project(plan, pastEffective)!["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public void Status_off_when_document_is_future_dated()
    {
        var plan = MakePlan();
        var future = MakeDoc("d1", PlanDocumentType.SBC, "https://example.com/a.pdf");
        future.EffectiveDate = DateTime.UtcNow.AddDays(30);

        _projector.Project(plan, future)!["status"]!.GetValue<string>().Should().Be("off");
    }

    [Fact]
    public void Status_off_when_parent_plan_is_retired()
    {
        var plan = MakePlan();
        plan.EffectiveDate = DateTime.UtcNow.AddYears(-2);
        plan.TerminationDate = DateTime.UtcNow.AddDays(-30);
        var doc = MakeDoc("d1", PlanDocumentType.SBC, "https://example.com/a.pdf");

        _projector.Project(plan, doc)!["status"]!.GetValue<string>().Should().Be("off",
            "retired parent plans retire their endpoints too");
    }

    // ── version + projectability gates ──────────────────────────────────

    [Fact]
    public void Returns_null_when_plan_is_not_published()
    {
        var plan = MakePlan();
        plan.VersionState = PlanVersionState.Draft;
        var doc = MakeDoc("d1", PlanDocumentType.SBC, "https://example.com/a.pdf");
        plan.Documents.Add(doc);

        _projector.Project(plan, doc).Should().BeNull();
        _projector.ProjectAll(plan).Should().BeEmpty();
    }

    [Fact]
    public void Returns_null_for_internal_documentreference_location()
    {
        var plan = MakePlan();
        var doc = MakeDoc("d1", PlanDocumentType.SBC, "documentreference/abc-123");

        _projector.Project(plan, doc).Should().BeNull(
            "Decision 4 — internal references aren't externally addressable");
    }

    [Fact]
    public void OrderedProjectableDocuments_skips_non_projectable_entries()
    {
        var plan = MakePlan();
        plan.Documents = new List<PlanDocumentReference>
        {
            MakeDoc("d-external", PlanDocumentType.SBC, "https://example.com/a.pdf"),
            MakeDoc("d-internal", PlanDocumentType.EOC, "documentreference/x"),
        };

        var ordered = _projector.OrderedProjectableDocuments(plan);
        ordered.Should().HaveCount(1);
        ordered[0]!.Id.Should().Be("d-external");
    }

    // ── ordering (Decision 8) ───────────────────────────────────────────

    [Fact]
    public void Documents_order_by_DocType_then_EffectiveDate_desc_then_Id()
    {
        var plan = MakePlan();
        plan.Documents = new List<PlanDocumentReference>
        {
            new() { Id = "z-other", DocType = PlanDocumentType.Other, Location = "https://example.com/o.pdf" },
            new() { Id = "a-spd",   DocType = PlanDocumentType.SPD,   Location = "https://example.com/spd.pdf" },
            new() { Id = "b-mrf",   DocType = PlanDocumentType.MachineReadableRateFile, Location = "https://example.com/mrf.json" },
            new() { Id = "c-eoc",   DocType = PlanDocumentType.EOC,   Location = "https://example.com/eoc.pdf" },
            new() { Id = "d-form",  DocType = PlanDocumentType.Formulary, Location = "https://example.com/f.pdf" },
            new() { Id = "e-sbc",   DocType = PlanDocumentType.SBC,   Location = "https://example.com/sbc.pdf" },
        };

        var ordered = _projector.OrderedProjectableDocuments(plan)
            .Select(d => d.Id).ToList();

        ordered.Should().Equal("e-sbc", "c-eoc", "d-form", "a-spd", "b-mrf", "z-other");
    }

    // ── determinism ─────────────────────────────────────────────────────

    [Fact]
    public void Repeated_projection_produces_identical_json()
    {
        var plan = MakePlan();
        var doc = MakeDoc("doc-sbc", PlanDocumentType.SBC, "https://example.com/sbc.pdf");
        doc.ContentType = "application/pdf";
        doc.EffectiveDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        doc.DisplayName = "2026 SBC";
        plan.Documents.Add(doc);

        var first = _projector.Project(plan, doc)!;
        var second = _projector.Project(plan, doc)!;
        first.ToJsonString().Should().Be(second.ToJsonString());
    }

    [Fact]
    public void ProjectAll_returns_empty_for_non_published_plan()
    {
        var plan = MakePlan();
        plan.VersionState = PlanVersionState.Superseded;
        plan.Documents.Add(MakeDoc("d1", PlanDocumentType.SBC, "https://example.com/a.pdf"));

        _projector.ProjectAll(plan).Should().BeEmpty();
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
        Documents = new List<PlanDocumentReference>(),
    };

    private static PlanDocumentReference MakeDoc(string id, PlanDocumentType type, string location) => new()
    {
        Id = id,
        DocType = type,
        Location = location,
    };
}
