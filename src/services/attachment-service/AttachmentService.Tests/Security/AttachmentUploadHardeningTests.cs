using System.Net;
using System.Net.Http.Headers;
using AttachmentService.Services;
using AttachmentService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AttachmentService.Tests.Security;

/// <summary>
/// <c>POST api/attachments</c> builds a storage path and a Content-Type from
/// what the client sends. Neither may come from the client unchecked: parent
/// ids that would add path segments are refused, only PDF/TIFF/JPEG/PNG files
/// (by DocumentFormat, extension and content) are stored, the size is capped,
/// and the parent must exist for the caller's tenant.
/// </summary>
[Collection(nameof(AttachmentServiceCollection))]
public class AttachmentUploadHardeningTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";

    private static readonly byte[] Pdf = "%PDF-1.4 test"u8.ToArray();
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly AttachmentServiceFactory _factory;

    public AttachmentUploadHardeningTests(AttachmentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
        _factory.Parents.Add(Tenant, AttachmentParentType.Claim, "CLM-1");
    }

    private HttpClient NewClient(WebApplicationFactory<AttachmentService.Controllers.AttachmentsController>? factory = null)
    {
        var client = (factory ?? _factory).CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1"));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static MultipartFormDataContent Form(
        string? claimId = "CLM-1", string? authorizationId = null, string format = "PDF",
        string fileName = "record.pdf", byte[]? content = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("PAYER-1"), "PayerId" },
            { new StringContent("Payer"), "PayerName" },
            { new StringContent("PROV-1"), "ProviderId" },
            { new StringContent("SUB-1"), "SubscriberId" },
            { new StringContent("Medical Records"), "DocumentType" },
            { new StringContent(format), "DocumentFormat" },
        };
        if (claimId is not null) form.Add(new StringContent(claimId), "ClaimId");
        if (authorizationId is not null) form.Add(new StringContent(authorizationId), "AuthorizationId");
        form.Add(new ByteArrayContent(content ?? Pdf), "file", fileName);
        return form;
    }

    private void NothingStored()
    {
        _factory.Attachments.Items.Should().BeEmpty();
        _factory.Documents.Count.Should().Be(0);
    }

    // ── Path segments ───────────────────────────────────────────────────

    [Theory]
    [InlineData("../tenant-b/x")]
    [InlineData("CLM-1/../../tenant-b")]
    [InlineData("..")]
    [InlineData("CLM 1")]
    [InlineData("CLM-1\\..\\x")]
    public async Task ParentIdThatIsNotAPlainId_IsRejected_AndNothingIsStored(string claimId)
    {
        _factory.Parents.Add(Tenant, AttachmentParentType.Claim, claimId);

        var response = await NewClient().PostAsync("/api/attachments", Form(claimId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NothingStored();
        _factory.Parents.Requests.Should().BeEmpty("the id is refused before any lookup");
    }

    [Fact]
    public async Task TheFile_IsFiledInTheCatalog_OnItsParent_ForTheTokenUser()
    {
        var response = await NewClient().PostAsync("/api/attachments", Form(fileName: "Scan.PDF"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var stored = _factory.Attachments.Items.Values.Should().ContainSingle().Subject;
        var file = _factory.Documents.Files.Should().ContainSingle().Subject;
        stored.DocumentId.Should().Be(file.Key);
        file.Value.TenantId.Should().Be(Tenant);
        file.Value.SubjectType.Should().Be(AttachmentParentType.Claim);
        file.Value.SubjectId.Should().Be("CLM-1");
        file.Value.FileName.Should().Be($"{stored.Id}.pdf");
        file.Value.ExternalIds["AttachmentId"].Should().Be(stored.Id);
        stored.FileHash.Should().HaveLength(64);
    }

    [Fact]
    public async Task WhenTheCatalogIsUnreachable_NothingIsStored()
    {
        _factory.Documents.Fail = true;

        var response = await NewClient().PostAsync("/api/attachments", Form());

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        _factory.Attachments.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ARefusedFiling_RemovesTheRecord_AndPassesTheRefusalOn()
    {
        _factory.Documents.RefuseWith = HttpStatusCode.RequestEntityTooLarge;

        var response = await NewClient().PostAsync("/api/attachments", Form());

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _factory.Attachments.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Download_ThatTheCatalogCantServeYet_Is409_NotARetry()
    {
        var stored = _factory.Attachments.Seed(Tenant);
        stored.DocumentId = _factory.Documents.Seed(Tenant, AttachmentParentType.Claim, "CLM-1", Pdf);
        _factory.Documents.RefuseWith = HttpStatusCode.Conflict;

        var response = await NewClient().GetAsync($"/api/attachments/{stored.Id}/download");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Download_OfAnAppealAttachment_NeedsAppealsRead_AsTheCatalogWould()
    {
        // MemberServices holds attachments:read and documents:read but not appeals:read.
        var stored = _factory.Attachments.Seed(Tenant, claimId: null);
        stored.AppealId = "A-1";
        stored.DocumentId = _factory.Documents.Seed(Tenant, AttachmentParentType.Appeal, "A-1", Pdf);
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("rep-1", ChoRolePermissions.MemberServices));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.GetAsync($"/api/attachments/{stored.Id}/download");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Download_OfARecordWithNoParent_IsNotFound()
    {
        var stored = _factory.Attachments.Seed(Tenant, claimId: null);
        stored.DocumentId = "doc-x";

        var response = await NewClient().GetAsync($"/api/attachments/{stored.Id}/download");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BlankClaimId_DoesNotHideTheAuthorizationId()
    {
        _factory.Parents.Add(Tenant, AttachmentParentType.Authorization, "AUTH-7");

        var response = await NewClient().PostAsync("/api/attachments",
            Form(claimId: "", authorizationId: "AUTH-7", format: "png", fileName: "x.png", content: Png));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        _factory.Attachments.Items.Values.Should().ContainSingle();
        var file = _factory.Documents.Files.Values.Should().ContainSingle().Subject;
        file.SubjectType.Should().Be(AttachmentParentType.Authorization);
        file.SubjectId.Should().Be("AUTH-7");
        file.FileName.Should().EndWith(".png");
    }

    // ── Extensions and content types ────────────────────────────────────

    [Theory]
    [InlineData("PDF", "payload.exe")]
    [InlineData("PDF", "record.html")]
    [InlineData("PDF", "record")]
    [InlineData("x-msdownload", "record.pdf")]
    [InlineData("html", "record.html")]
    [InlineData("PNG", "record.pdf")]
    public async Task DisallowedOrMismatchedFormat_Is415_AndNothingIsStored(string format, string fileName)
    {
        var response = await NewClient().PostAsync("/api/attachments", Form(format: format, fileName: fileName));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        NothingStored();
    }

    [Fact]
    public async Task ContentThatIsNotTheDeclaredFormat_Is415_AndNothingIsStored()
    {
        var html = "<html><script>alert(1)</script></html>"u8.ToArray();

        var response = await NewClient().PostAsync("/api/attachments", Form(content: html));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        NothingStored();
    }

    [Fact]
    public async Task Download_ContentType_ComesFromTheAllowlist()
    {
        var created = await NewClient().PostAsync("/api/attachments", Form(format: "jpg", fileName: "x.jpeg",
            content: new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }));
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var stored = _factory.Attachments.Items.Values.Single();

        var response = await NewClient().GetAsync($"/api/attachments/{stored.Id}/download");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        stored.DocumentFormat.Should().Be("JPEG");
    }

    [Fact]
    public async Task Download_OfALegacyRecordWithAnUnknownFormat_IsOctetStream()
    {
        var legacy = _factory.Attachments.Seed(Tenant);
        legacy.DocumentFormat = "html";
        legacy.DocumentId = _factory.Documents.Seed(Tenant, AttachmentParentType.Claim, "CLM-1", "<html>"u8.ToArray());

        var response = await NewClient().GetAsync($"/api/attachments/{legacy.Id}/download");

        response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
    }

    // ── Size ────────────────────────────────────────────────────────────

    private WebApplicationFactory<AttachmentService.Controllers.AttachmentsController> WithLimit(long maxBytes, long overheadBytes) =>
        _factory.WithWebHostBuilder(b => b
            .UseSetting("Attachments:Upload:MaxFileSizeBytes", maxBytes.ToString())
            .UseSetting("Attachments:Upload:FormOverheadBytes", overheadBytes.ToString()));

    [Fact]
    public async Task FileOverTheConfiguredCap_Is413_AndNothingIsStored()
    {
        using var limited = WithLimit(maxBytes: 1024, overheadBytes: 64 * 1024);
        var big = Pdf.Concat(new byte[2048]).ToArray();

        var response = await NewClient(limited).PostAsync("/api/attachments", Form(content: big));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        NothingStored();
    }

    [Fact]
    public async Task RequestBodyOverTheCap_Is413_BeforeTheFormIsRead()
    {
        using var limited = WithLimit(maxBytes: 1024, overheadBytes: 0);
        var form = Form(content: Pdf.Concat(new byte[4096]).ToArray());
        await form.LoadIntoBufferAsync();
        form.Headers.ContentLength.Should().BeGreaterThan(1024);

        var response = await NewClient(limited).PostAsync("/api/attachments", form);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        NothingStored();
        _factory.Parents.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FileUnderTheCap_IsAccepted()
    {
        using var limited = WithLimit(maxBytes: 1024, overheadBytes: 64 * 1024);

        var response = await NewClient(limited).PostAsync("/api/attachments", Form());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ── Parent must exist for the caller's tenant ───────────────────────

    [Fact]
    public async Task ParentBelongingToAnotherTenant_Is404_AndNothingIsStored()
    {
        _factory.Parents.Add(OtherTenant, AttachmentParentType.Claim, "CLM-9");

        var response = await NewClient().PostAsync("/api/attachments", Form(claimId: "CLM-9"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        NothingStored();
        var lookup = _factory.Parents.Requests.Should().ContainSingle().Subject;
        lookup.RequestUri!.AbsolutePath.Should().Be("/api/claims/CLM-9");
        lookup.Headers.GetValues("X-Tenant-ID").Should().Equal(Tenant);
    }

    [Fact]
    public async Task MetadataOnlyAttachment_ForAnotherTenantsParent_IsAlso404()
    {
        _factory.Parents.Add(OtherTenant, AttachmentParentType.Claim, "CLM-9");
        var form = new MultipartFormDataContent
        {
            { new StringContent("CLM-9"), "ClaimId" },
            { new StringContent("PDF"), "DocumentFormat" },
        };

        var response = await NewClient().PostAsync("/api/attachments", form);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        NothingStored();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ParentLookupFailure_FailsClosed_With502(HttpStatusCode downstream)
    {
        _factory.Parents.FailWith = downstream;

        var response = await NewClient().PostAsync("/api/attachments", Form());

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        NothingStored();
    }

    // ── Errors do not echo exception text ───────────────────────────────

    [Fact]
    public async Task UnexpectedFailure_IsAProblemWithATraceId_AndNoExceptionText()
    {
        _factory.Attachments.ThrowOnCreate = new InvalidOperationException("mongodb://admin:hunter2@db/secret-ssn-123-45-6789");

        var response = await NewClient().PostAsync("/api/attachments", Form());

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("hunter2").And.NotContain("123-45-6789");
        body.Should().Contain("\"traceId\"");
    }
}
