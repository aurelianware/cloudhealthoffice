using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;
using CorrespondenceService.Models;
using CorrespondenceService.Tests.Support;

namespace CorrespondenceService.Tests.Security;

/// <summary>
/// Template lifecycle and letters through the real pipeline: permissions,
/// tenant isolation, two-person approval, pre-release checks and write-once
/// storage in the document catalog.
/// </summary>
public class CorrespondencePipelineTests : IClassFixture<CorrespondenceFactory>
{
    private const string Base = "/api/v1/correspondence";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly CorrespondenceFactory _factory;

    public CorrespondencePipelineTests(CorrespondenceFactory factory) => _factory = factory;

    private HttpClient As(string subject, string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static string NewTenant() => "tenant-corr-" + Guid.NewGuid().ToString("N")[..8];

    private static JsonObject TemplateBody(string rightsText = "You have the right to ask for a State fair hearing.") => new()
    {
        ["letterTypeCode"] = Builders.LetterTypeCode,
        ["name"] = "Appeal acknowledgment",
        ["kind"] = "StatePrescribed",
        ["formNumber"] = "TX-HHSC-1",
        ["scope"] = new JsonObject { ["state"] = "TX" },
        ["effectiveFrom"] = "2026-01-01",
        ["blocks"] = new JsonArray(
            new JsonObject { ["id"] = "date", ["text"] = "{{ letter.date }}" },
            new JsonObject { ["id"] = "address", ["kind"] = "AddressBlock" },
            new JsonObject { ["id"] = "body", ["text"] = "Dear {{ member.name }}, we received your appeal {{ appeal.id }} on {{ appeal.receivedDate }}." },
            new JsonObject { ["id"] = "rights", ["locked"] = true, ["text"] = rightsText }),
        ["requiredBlocks"] = new JsonArray("body", "rights"),
    };

    /// <summary>A tenant with the letter type and an approved Texas template, authored and approved by different admins.</summary>
    private async Task<(string Tenant, string TemplateId)> ApprovedTemplateAsync()
    {
        var tenant = NewTenant();
        var author = As("author-1", tenant);
        (await author.PostAsJsonAsync($"{Base}/letter-types", new JsonObject
        {
            ["code"] = "appeal_ack", ["name"] = "Appeal acknowledgment", ["category"] = "Appeal", ["audience"] = "Member",
        })).StatusCode.Should().Be(HttpStatusCode.Created);

        var created = await author.PostAsJsonAsync($"{Base}/templates", TemplateBody());
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var templateId = (await created.Content.ReadFromJsonAsync<LetterTemplate>(Json))!.TemplateId;

        (await author.PostAsync($"{Base}/templates/{templateId}/versions/1/approve", null)).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity, "the author cannot approve their own template");
        var approved = await As("approver-2", tenant).PostAsync($"{Base}/templates/{templateId}/versions/1/approve", null);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        return (tenant, templateId);
    }

    private static JsonObject LetterBody(string? line1 = "100 Main St", string subjectKind = "Appeal") => new()
    {
        ["letterTypeCode"] = Builders.LetterTypeCode,
        ["subjects"] = new JsonArray(new JsonObject { ["kind"] = "member", ["id"] = "M-1" }, new JsonObject { ["kind"] = subjectKind, ["id"] = "APL-100" }),
        ["recipient"] = new JsonObject
        {
            ["name"] = "Maria Lopez", ["addressLine1"] = line1, ["city"] = "Dallas", ["state"] = "TX", ["postalCode"] = "75201",
        },
        ["data"] = Builders.Data(),
        ["state"] = "TX",
        ["letterDate"] = "2026-10-21",
    };

    [Fact]
    public async Task A_requested_letter_is_rendered_checked_and_stored_once_in_the_catalog()
    {
        var (tenant, templateId) = await ApprovedTemplateAsync();
        var coordinator = As("um-1", tenant, ChoRolePermissions.UMCoordinator);

        var response = await coordinator.PostAsJsonAsync($"{Base}/letters", LetterBody());
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var letter = (await response.Content.ReadFromJsonAsync<Letter>(Json))!;

        letter.Status.Should().Be(LetterStatus.Rendered);
        letter.TemplateId.Should().Be(templateId);
        letter.TemplateVersion.Should().Be(1);
        letter.DocumentId.Should().NotBeNull();
        letter.Checks.Should().OnlyContain(c => c.Passed);
        letter.Subjects.Select(s => s.Kind).Should().Equal("Member", "Appeal");
        letter.DataSnapshot.Should().Contain("APL-100");
        letter.DataSnapshotHash.Should().HaveLength(64);
        letter.Events.Select(e => e.Type).Should().Equal("Requested", "Rendered");

        var upload = _factory.Documents.Uploads.Single(u => u.TenantId == tenant);
        upload.OnBehalfOf.Should().Be("um-1");
        upload.Links.Should().Contain(("Appeal", "APL-100"));
        upload.ExternalIds["LetterId"].Should().Be(letter.Id);

        (await coordinator.GetFromJsonAsync<Letter>($"{Base}/letters/{letter.Id}", Json))!.Id.Should().Be(letter.Id);
        var list = await coordinator.GetFromJsonAsync<List<Letter>>($"{Base}/letters?subjectKind=appeal&subjectId=APL-100", Json);
        list!.Select(l => l.Id).Should().Equal(letter.Id);

        var content = await coordinator.GetAsync($"{Base}/letters/{letter.Id}/content");
        content.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        _factory.Documents.Opens.Should().Contain(o => o.TenantId == tenant && o.OnBehalfOf == "um-1" && o.DocumentId == letter.DocumentId);
    }

    [Fact]
    public async Task A_letter_that_fails_a_check_is_recorded_but_never_stored()
    {
        var (tenant, _) = await ApprovedTemplateAsync();
        var response = await As("um-1", tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"{Base}/letters", LetterBody(line1: null));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("address line 1");
        _factory.Documents.Uploads.Should().NotContain(u => u.TenantId == tenant);
        _factory.Repo.Letters.Should().ContainSingle(l => l.TenantId == tenant).Which.Status.Should().Be(LetterStatus.CheckFailed);
    }

    [Fact]
    public async Task Catalog_failure_is_a_502_and_records_nothing()
    {
        var (tenant, _) = await ApprovedTemplateAsync();
        _factory.Documents.Fail = true;
        try
        {
            var response = await As("um-1", tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"{Base}/letters", LetterBody());
            response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            // Nothing was stored, so nothing is recorded: a retry starts a new letter.
            _factory.Repo.Letters.Should().NotContain(l => l.TenantId == tenant);

            _factory.Documents.FailStatus = HttpStatusCode.Forbidden;
            var refused = await As("um-1", tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"{Base}/letters", LetterBody());
            refused.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("cannot write documents for every subject");
        }
        finally { _factory.Documents.Fail = false; _factory.Documents.FailStatus = null; }
    }

    [Fact]
    public async Task No_approved_template_is_a_422_naming_the_gap()
    {
        var (tenant, _) = await ApprovedTemplateAsync();
        var body = LetterBody();
        body["state"] = "OK";
        var response = await As("um-1", tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"{Base}/letters", body);
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("state OK");
    }

    [Fact]
    public async Task Requesting_needs_read_access_to_every_subject()
    {
        var (tenant, _) = await ApprovedTemplateAsync();
        // UMCoordinator cannot read providers.
        var response = await As("um-1", tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"{Base}/letters", LetterBody(subjectKind: "Provider"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Repo.Letters.Should().NotContain(l => l.TenantId == tenant);
    }

    [Fact]
    public async Task Roles_without_correspondence_permissions_are_refused()
    {
        var (tenant, templateId) = await ApprovedTemplateAsync();
        var memberServices = As("ms-1", tenant, ChoRolePermissions.MemberServices);
        (await memberServices.PostAsJsonAsync($"{Base}/letters", LetterBody())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await memberServices.GetAsync($"{Base}/templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var coordinator = As("um-1", tenant, ChoRolePermissions.UMCoordinator);
        (await coordinator.PostAsJsonAsync($"{Base}/templates", TemplateBody())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await coordinator.PostAsync($"{Base}/templates/{templateId}/versions/1/retire", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Compliance_approves_templates_but_does_not_author_them()
    {
        var (tenant, templateId) = await ApprovedTemplateAsync();
        var author = As("author-1", tenant);
        (await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions", TemplateBody())).EnsureSuccessStatusCode();

        var compliance = As("compliance-1", tenant, ChoRolePermissions.ComplianceOfficer);
        (await compliance.PostAsJsonAsync($"{Base}/templates", TemplateBody())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var approved = await compliance.PostAsync($"{Base}/templates/{templateId}/versions/2/approve", null);
        approved.StatusCode.Should().Be(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        (await approved.Content.ReadFromJsonAsync<LetterTemplate>(Json))!.ApprovedBy.Should().Be("compliance-1");
    }

    [Fact]
    public async Task Another_tenant_sees_nothing()
    {
        var (tenant, templateId) = await ApprovedTemplateAsync();
        var created = await As("um-1", tenant, ChoRolePermissions.UMCoordinator).PostAsJsonAsync($"{Base}/letters", LetterBody());
        var letterId = (await created.Content.ReadFromJsonAsync<Letter>(Json))!.Id;

        var other = As("admin-x", NewTenant());
        (await other.GetAsync($"{Base}/letters/{letterId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"{Base}/letters/{letterId}/content")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetAsync($"{Base}/templates/{templateId}/versions/1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.GetFromJsonAsync<List<Letter>>($"{Base}/letters?subjectKind=Appeal&subjectId=APL-100", Json)).Should().BeEmpty();
    }

    [Fact]
    public async Task Approved_versions_are_frozen_and_prescribed_wording_cannot_change()
    {
        var (tenant, templateId) = await ApprovedTemplateAsync();
        var author = As("author-1", tenant);

        (await author.PutAsJsonAsync($"{Base}/templates/{templateId}/versions/1", TemplateBody())).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var reworded = await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions", TemplateBody("You may ask for a hearing."));
        reworded.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await reworded.Content.ReadAsStringAsync()).Should().Contain("word for word");

        var v2 = await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions", TemplateBody());
        v2.StatusCode.Should().Be(HttpStatusCode.Created, await v2.Content.ReadAsStringAsync());
        (await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions", TemplateBody())).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "one draft at a time");
    }

    [Fact]
    public async Task Preview_renders_a_draft_without_storing_anything()
    {
        var (tenant, templateId) = await ApprovedTemplateAsync();
        var author = As("author-1", tenant);
        (await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions", TemplateBody())).EnsureSuccessStatusCode();
        var before = _factory.Repo.Letters.Count(l => l.TenantId == tenant);

        var preview = new JsonObject { ["recipient"] = LetterBody()["recipient"]!.DeepClone(), ["data"] = Builders.Data(), ["letterDate"] = "2026-10-21" };
        var pdf = await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions/2/preview", preview);
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");

        var checks = await author.PostAsJsonAsync($"{Base}/templates/{templateId}/versions/2/preview/checks", new JsonObject { ["recipient"] = new JsonObject { ["name"] = "X" } });
        var body = JsonNode.Parse(await checks.Content.ReadAsStringAsync())!;
        body["passed"]!.GetValue<bool>().Should().BeFalse();

        _factory.Repo.Letters.Count(l => l.TenantId == tenant).Should().Be(before);
        _factory.Documents.Uploads.Should().NotContain(u => u.TenantId == tenant);
    }
}
