using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Controllers;
using DocumentService.Models;

namespace DocumentService.Tests.Security;

/// <summary>
/// The catalog's access rules, end to end through the real pipeline: a
/// document is opened from a subject it is linked to, by a caller holding that
/// subject's read permission; sensitive documents need more; Part 2 records
/// are withheld; and every attempt lands in the access audit.
/// </summary>
public sealed class DocumentPipelineTests : IClassFixture<DocumentServiceFactory>, IDisposable
{
    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF");
    private static readonly byte[] OtherPdf = Encoding.ASCII.GetBytes("%PDF-1.7\n2 0 obj << >> endobj\n%%EOF");
    private static readonly byte[] Html = Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly DocumentServiceFactory _factory;

    public DocumentPipelineTests(DocumentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    public void Dispose() => _factory.Reset();

    private HttpClient Client(string role, string user = "user-1", string tenant = Tenant)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient Examiner(string tenant = Tenant) => Client(ChoRolePermissions.ClaimsExaminer, "examiner-1", tenant);
    private HttpClient UmCoordinator() => Client(ChoRolePermissions.UMCoordinator, "um-1");
    private HttpClient Compliance() => Client(ChoRolePermissions.ComplianceOfficer, "compliance-1");

    private static MultipartFormDataContent Form(byte[] bytes, string contentType = "application/pdf",
        string type = "MedicalRecord", string[]? links = null, string[]? sensitivity = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(type), "Type" } };
        foreach (var link in links ?? ["Claim:CLM-1"])
            form.Add(new StringContent(link), "Links");
        foreach (var tag in sensitivity ?? [])
            form.Add(new StringContent(tag), "Sensitivity");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", "record.pdf");
        return form;
    }

    private async Task<DocumentView> UploadAsync(HttpClient client, byte[] bytes, string[]? links = null, string[]? sensitivity = null)
    {
        var response = await client.PostAsync("/api/v1/documents", Form(bytes, links: links, sensitivity: sensitivity));
        Assert.True(response.IsSuccessStatusCode, $"upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentView>(Json))!;
    }

    private static string On(string kind, string id) => $"subjectKind={kind}&subjectId={id}";

    // ── Authentication and tenant ───────────────────────────────────

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/v1/documents?{On("Claim", "CLM-1")}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARoleWithoutDocumentsRead_IsRefused()
    {
        // EnrollmentSpecialist can read members but holds no documents permission.
        var response = await Client(ChoRolePermissions.EnrollmentSpecialist).GetAsync($"/api/v1/documents?{On("Member", "M-1")}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherTenant_CannotSeeTheDocument()
    {
        var doc = await UploadAsync(Examiner(), Pdf);

        var list = await Examiner(OtherTenant).GetFromJsonAsync<DocumentListResponse>($"/api/v1/documents?{On("Claim", "CLM-1")}", Json);
        var get = await Examiner(OtherTenant).GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-1")}");

        Assert.Empty(list!.Documents);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    // ── Upload and reading through a subject ────────────────────────

    [Fact]
    public async Task Upload_StoresTheFileOnce_UnderTheTenant_AndAudits()
    {
        var doc = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1", "Member:M-1"]);

        var stored = Assert.Single(_factory.Documents.All);
        Assert.StartsWith($"tenants/{Tenant}/documents/", stored.StoragePath);
        Assert.NotNull(_factory.Storage.Inner.GetBytes(DocumentsController.Container, stored.StoragePath));
        Assert.Equal("examiner-1", stored.CreatedBy);
        Assert.Equal(ScanStatus.Pending, doc.ScanStatus);
        Assert.Equal(2, doc.Links.Count);
        Assert.Equal("DEFAULT-10Y", doc.RetentionPolicyId);
        Assert.Equal("false", _factory.Storage.Inner.GetTags(DocumentsController.Container, stored.StoragePath)!["legalHold"]);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Upload && e.DocumentId == doc.Id && e.Allowed);
    }

    [Fact]
    public async Task Upload_NeedsReadAccessToEveryLinkedSubject()
    {
        // A claims examiner can't file a document on an appeal it can't read.
        var response = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, links: ["Claim:CLM-1", "Appeal:A-1"]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Documents.All);
    }

    [Theory]
    [InlineData(new string[0], "at least one subject")]
    [InlineData(new[] { "Claim" }, "Kind:Id")]
    [InlineData(new[] { "Claim:../../x" }, "Kind:Id")]
    [InlineData(new[] { "Spaceship:1" }, "Kind:Id")]
    public async Task Upload_RejectsMissingOrMalformedLinks(string[] links, string message)
    {
        var form = Form(Pdf, links: links.Length == 0 ? [] : links);
        if (links.Length == 0)
        {
            form = new MultipartFormDataContent { { new StringContent("MedicalRecord"), "Type" } };
            var file = new ByteArrayContent(Pdf);
            file.Headers.ContentType = new("application/pdf");
            form.Add(file, "file", "r.pdf");
        }

        var response = await Examiner().PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upload_RefusesContentThatIsNotTheDeclaredType()
    {
        var response = await Examiner().PostAsync("/api/v1/documents", Form(Html, contentType: "application/pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_factory.Documents.All);
        Assert.Equal(0, _factory.Storage.Inner.Count);
    }

    [Fact]
    public async Task List_ReturnsTheSubjectsDocuments_AndAuditsIt()
    {
        await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1"]);
        await UploadAsync(Examiner(), OtherPdf, links: ["Claim:CLM-2"]);

        var list = await Examiner().GetFromJsonAsync<DocumentListResponse>($"/api/v1/documents?{On("Claim", "CLM-1")}", Json);

        Assert.Single(list!.Documents);
        Assert.Equal(0, list.Withheld);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.List && e.SubjectId == "CLM-1" && e.Allowed);
    }

    [Fact]
    public async Task List_OfASubjectTheCallerCantRead_IsForbidden_AndAudited()
    {
        var response = await Examiner().GetAsync($"/api/v1/documents?{On("Appeal", "A-1")}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.List && !e.Allowed && e.Detail!.Contains("appeals:read"));
    }

    [Fact]
    public async Task Get_FromASubjectTheDocumentIsNotLinkedTo_Is404_AndAuditedAsDenied()
    {
        var doc = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1"]);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-2")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.View && e.DocumentId == doc.Id && !e.Allowed);
    }

    [Fact]
    public async Task Get_WithoutASubject_IsRefused()
    {
        var doc = await UploadAsync(Examiner(), Pdf);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Links_ToSubjectsTheCallerCantRead_AreCountedNotShown()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1", "Appeal:A-1"]);

        var view = await Examiner().GetFromJsonAsync<DocumentView>($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-1")}", Json);

        Assert.Equal(new DocumentLink { Kind = DocumentLinkKind.Claim, Id = "CLM-1" }, Assert.Single(view!.Links));
        Assert.Equal(1, view.OtherLinks);
    }

    // ── Content and scanning ────────────────────────────────────────

    [Fact]
    public async Task Content_OfAnUnscannedDocument_IsRefusedByDefault()
    {
        var doc = await UploadAsync(Examiner(), Pdf);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/content?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Download && !e.Allowed && e.Detail!.Contains("Pending"));
    }

    [Fact]
    public async Task Content_IsServedWhenUnscannedContentIsAllowed_AsAnAttachment_AndAudited()
    {
        _factory.AllowUnscanned = true;
        var doc = await UploadAsync(Examiner(), Pdf);
        var client = Examiner();
        client.DefaultRequestHeaders.Add(DocumentsController.ScreenHeader, "claim-review");

        var response = await client.GetAsync($"/api/v1/documents/{doc.Id}/content?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Download && e.Allowed && e.Screen == "claim-review");
    }

    [Theory]
    [InlineData(ScanStatus.Infected)]
    [InlineData(ScanStatus.Failed)]
    public async Task Content_ThatFailedScanning_IsNeverServed(ScanStatus status)
    {
        _factory.AllowUnscanned = true;
        var doc = await UploadAsync(Examiner(), Pdf);
        var stored = _factory.Documents.All.Single();
        stored.Scan = new DocumentScan { Status = status, Engine = "test" };
        _factory.Documents.Seed(stored);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/content?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task WhenTheAuditCantBeWritten_NothingIsServed()
    {
        _factory.AllowUnscanned = true;
        var doc = await UploadAsync(Examiner(), Pdf);
        _factory.Audit.Fail = true;

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/content?{On("Claim", "CLM-1")}");

        Assert.False(response.IsSuccessStatusCode);
        Assert.NotEqual(Pdf, await response.Content.ReadAsByteArrayAsync());
    }

    // ── Sensitivity ─────────────────────────────────────────────────

    [Fact]
    public async Task SensitiveDocuments_AreWithheldFromCallersWithoutTheSensitivePermission()
    {
        await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["BEHAVIORAL_HEALTH"]);
        await UploadAsync(Examiner(), OtherPdf, links: ["Claim:CLM-1"]);

        var examiner = await Examiner().GetFromJsonAsync<DocumentListResponse>($"/api/v1/documents?{On("Claim", "CLM-1")}", Json);
        var um = await UmCoordinator().GetFromJsonAsync<DocumentListResponse>($"/api/v1/documents?{On("Claim", "CLM-1")}", Json);

        Assert.Single(examiner!.Documents);
        Assert.Equal(1, examiner.Withheld);
        Assert.Equal(2, um!.Documents.Count);
        Assert.Equal(0, um.Withheld);
    }

    [Fact]
    public async Task Part2Records_AreWithheldFromEveryone_UntilTheConsentCheckExists()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["PART2_SUD"]);

        foreach (var client in new[] { UmCoordinator(), Compliance(), Client(ChoRolePermissions.TenantAdmin, "admin-1") })
        {
            var response = await client.GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-1")}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            // The refusal says nothing about why: naming Part 2 would itself disclose it.
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(DocumentsController.RestrictedMessage, body);
            Assert.DoesNotContain("Part 2", body);
        }
        Assert.All(_factory.Audit.Entries.Where(e => e.Action == DocumentAccessAction.View),
            e => Assert.Contains("Part 2", e.Detail));
    }

    [Fact]
    public async Task ASensitiveDocument_RefusesWithoutNamingTheTag_EvenForATenantAdmin()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["HIV"]);

        var response = await Client(ChoRolePermissions.TenantAdmin, "admin-1").GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("HIV", await response.Content.ReadAsStringAsync());
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.View && !e.Allowed && e.Detail!.Contains("HIV"));
    }

    [Fact]
    public async Task Upload_RejectsAnUnknownSensitivityTag()
    {
        var response = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, sensitivity: ["SECRET"]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Duplicates ──────────────────────────────────────────────────

    [Fact]
    public async Task IdenticalContent_IsStoredOnce_AndLinkedToBothSubjects()
    {
        var first = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1"]);

        var response = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, links: ["Claim:CLM-2"]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = (await response.Content.ReadFromJsonAsync<DocumentView>(Json))!;
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Links.Count);
        Assert.Single(_factory.Documents.All);
        Assert.Equal(1, _factory.Storage.Inner.Count);
    }

    [Fact]
    public async Task IdenticalBytes_NeverAttachTheUploaderToADocumentTheyCantSee()
    {
        var hidden = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["BEHAVIORAL_HEALTH"]);

        var response = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, links: ["Claim:CLM-2"]));

        // A fresh document, answered exactly like a first upload.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mine = (await response.Content.ReadFromJsonAsync<DocumentView>(Json))!;
        Assert.NotEqual(hidden.Id, mine.Id);
        Assert.Single(_factory.Documents.All.Single(d => d.Id == hidden.Id).Links);
    }

    [Fact]
    public async Task IdenticalBytes_DontMergeIntoAHeldDocument_OrRevealTheHold()
    {
        var held = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1"]);
        await Compliance().PutAsJsonAsync($"/api/v1/documents/{held.Id}/legal-hold", new { legalHold = true, reason = "litigation" });

        var response = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, links: ["Claim:CLM-2"]));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, _factory.Documents.All.Count);
    }

    [Fact]
    public async Task List_AuditsWhichDocumentsWereShown()
    {
        var doc = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1"]);

        await Examiner().GetAsync($"/api/v1/documents?{On("Claim", "CLM-1")}");

        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.List && e.Detail!.Contains(doc.Id));
    }

    [Fact]
    public async Task RefusedUploads_AreAudited()
    {
        await Examiner().PostAsync("/api/v1/documents", Form(Pdf, links: ["Appeal:A-1"]));
        await Examiner().PostAsync("/api/v1/documents", Form(Html, contentType: "application/pdf"));

        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Upload && !e.Allowed && e.SubjectKind == "Appeal");
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Upload && !e.Allowed && e.Detail!.Contains("not application/pdf"));
    }

    // ── Links ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddLink_FromALinkedSubject_ToAReadableOne()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Authorization:AUTH-1"]);

        var response = await UmCoordinator().PostAsJsonAsync($"/api/v1/documents/{doc.Id}/links?{On("Authorization", "AUTH-1")}",
            new { link = "Appeal:A-9" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(_factory.Documents.All.Single().Links, l => l.Kind == DocumentLinkKind.Appeal && l.Id == "A-9");
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Link && e.Allowed);
    }

    [Fact]
    public async Task AddLink_ToASubjectTheCallerCantRead_IsForbidden()
    {
        var doc = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1"]);

        var response = await Examiner().PostAsJsonAsync($"/api/v1/documents/{doc.Id}/links?{On("Claim", "CLM-1")}",
            new { link = "Appeal:A-9" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(_factory.Documents.All.Single().Links);
        // Refused, and recorded as refused.
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Link && !e.Allowed);
        Assert.DoesNotContain(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.Link && e.Allowed);
    }

    [Fact]
    public async Task RemoveLink_KeepsAtLeastOne()
    {
        var doc = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1", "Claim:CLM-2"]);

        var first = await Examiner().DeleteAsync($"/api/v1/documents/{doc.Id}/links/Claim/CLM-2?{On("Claim", "CLM-1")}");
        var last = await Examiner().DeleteAsync($"/api/v1/documents/{doc.Id}/links/Claim/CLM-1?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        Assert.Single(_factory.Documents.All.Single().Links);
    }

    // ── Legal hold ──────────────────────────────────────────────────

    [Fact]
    public async Task LegalHold_NeedsTheLegalHoldPermission()
    {
        var doc = await UploadAsync(Examiner(), Pdf);

        var response = await Examiner().PutAsJsonAsync($"/api/v1/documents/{doc.Id}/legal-hold", new { legalHold = true, reason = "litigation" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LegalHold_TagsStorage_FreezesLinks_AndNeedsAReasonToRelease()
    {
        var doc = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-1", "Claim:CLM-2"]);

        var hold = await Compliance().PutAsJsonAsync($"/api/v1/documents/{doc.Id}/legal-hold", new { legalHold = true, reason = "litigation" });
        var unlink = await Examiner().DeleteAsync($"/api/v1/documents/{doc.Id}/links/Claim/CLM-2?{On("Claim", "CLM-1")}");
        var releaseWithoutReason = await Compliance().PutAsJsonAsync($"/api/v1/documents/{doc.Id}/legal-hold", new { legalHold = false });

        Assert.Equal(HttpStatusCode.OK, hold.StatusCode);
        var stored = _factory.Documents.All.Single();
        Assert.True(stored.Retention.LegalHold.Set);
        Assert.Equal("compliance-1", stored.Retention.LegalHold.By);
        Assert.Equal("true", _factory.Storage.Inner.GetTags(DocumentsController.Container, stored.StoragePath)!["legalHold"]);
        Assert.Equal(HttpStatusCode.Conflict, unlink.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, releaseWithoutReason.StatusCode);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.LegalHold && e.Detail!.Contains("litigation"));
    }

    // ── Access log ──────────────────────────────────────────────────

    [Fact]
    public async Task AccessLog_ShowsAllowedAndDeniedAttempts_ToAuditReadersOnly()
    {
        var doc = await UploadAsync(Examiner(), Pdf);
        await Examiner().GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-1")}");
        await Examiner().GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-404")}");

        var forExaminer = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/access-log");
        var log = await Compliance().GetFromJsonAsync<List<DocumentAccessEvent>>($"/api/v1/documents/{doc.Id}/access-log", Json);

        Assert.Equal(HttpStatusCode.Forbidden, forExaminer.StatusCode);
        Assert.Contains(log!, e => e.Action == DocumentAccessAction.View && e.Allowed);
        Assert.Contains(log!, e => e.Action == DocumentAccessAction.View && !e.Allowed && e.SubjectId == "CLM-404");
    }

    [Fact]
    public async Task AccessLog_OfASensitiveDocument_NeedsTheSensitivePermissionToo()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["GENETIC"]);

        // ComplianceViewer holds audit:read but not documents:read-sensitive.
        var viewer = await Client(ChoRolePermissions.ComplianceViewer, "viewer-1").GetAsync($"/api/v1/documents/{doc.Id}/access-log");
        var officer = await Compliance().GetAsync($"/api/v1/documents/{doc.Id}/access-log");

        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, officer.StatusCode);
    }

    [Fact]
    public async Task LegalHold_AnswersOnlyTheHoldState_AndAuditsEveryAttempt()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["PART2_SUD"]);

        var set = await Compliance().PutAsJsonAsync($"/api/v1/documents/{doc.Id}/legal-hold", new { legalHold = true, reason = "litigation" });
        var again = await Compliance().PutAsJsonAsync($"/api/v1/documents/{doc.Id}/legal-hold", new { legalHold = true, reason = "litigation" });
        var missing = await Compliance().PutAsJsonAsync("/api/v1/documents/nope/legal-hold", new { legalHold = true, reason = "x" });

        var body = await set.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.DoesNotContain("Claim", body);
        Assert.DoesNotContain("PART2", body);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(3, _factory.Audit.Entries.Count(e => e.Action == DocumentAccessAction.LegalHold));
    }

    // ── Services acting for a user ──────────────────────────────────

    private HttpClient Service(string clientId = "attachment-service")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task AServiceUpload_RecordsTheUserItActedFor_AndItsSourceIds()
    {
        var client = Service();
        client.DefaultRequestHeaders.Add(DocumentsController.OnBehalfOfHeader, "provider-user-9");
        var form = Form(Pdf, links: ["Claim:CLM-1"]);
        form.Add(new StringContent("AttachmentId=att-1"), "ExternalIds");
        form.Add(new StringContent("X12-275"), "SourceChannel");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = _factory.Documents.All.Single();
        Assert.Equal("attachment-service", stored.CreatedBy);
        Assert.Equal("provider-user-9", stored.CreatedOnBehalfOf);
        Assert.Equal("att-1", stored.Source.ExternalIds["AttachmentId"]);
        Assert.Equal("X12-275", stored.Source.Channel);
        Assert.Contains(_factory.Audit.Entries, e => e.Actor == "attachment-service" && e.OnBehalfOf == "provider-user-9");
    }

    [Fact]
    public async Task AUser_CannotClaimToActForSomeoneElse()
    {
        var client = Examiner();
        client.DefaultRequestHeaders.Add(DocumentsController.OnBehalfOfHeader, "someone-else");

        await UploadAsync(client, Pdf);

        Assert.Null(_factory.Documents.All.Single().CreatedOnBehalfOf);
        Assert.All(_factory.Audit.Entries, e => Assert.Null(e.OnBehalfOf));
    }

    [Fact]
    public async Task AService_DoesNotGetSensitiveDocuments()
    {
        var doc = await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["HIV"]);

        var response = await Service().GetAsync($"/api/v1/documents/{doc.Id}?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("no-equals-sign")]
    [InlineData("1bad=x")]
    [InlineData("Key=has space")]
    public async Task MalformedExternalIds_AreRejected(string entry)
    {
        var form = Form(Pdf);
        form.Add(new StringContent(entry), "ExternalIds");

        var response = await Examiner().PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Documents.All);
    }

    // ── Pre-signed upload ───────────────────────────────────────────

    private async Task<PresignedUploadResponse> StartAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/documents", new
        {
            type = "ItemizedBill",
            contentType = "application/pdf",
            fileName = "bill.pdf",
            links = new[] { "Claim:CLM-1" }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PresignedUploadResponse>(Json))!;
    }

    [Fact]
    public async Task PresignedUpload_ThenFinalize_StoresTheDocument()
    {
        var start = await StartAsync(Examiner());
        var pending = _factory.Documents.All.Single();
        Assert.NotNull(pending.PendingUploadPath);
        Assert.EndsWith(".upload", pending.PendingUploadPath);

        // The client PUTs to the link; here the bytes go straight to the staging object.
        await _factory.Storage.Inner.UploadAsync(DocumentsController.Container, pending.PendingUploadPath!, new MemoryStream(Pdf), "application/pdf");
        var finalize = await Examiner().PostAsync($"/api/v1/documents/{start.DocumentId}/finalize", null);

        Assert.Equal(HttpStatusCode.OK, finalize.StatusCode);
        var stored = _factory.Documents.All.Single();
        Assert.Null(stored.PendingUploadPath);
        Assert.Equal(Pdf.Length, stored.SizeBytes);
        Assert.NotNull(_factory.Storage.Inner.GetBytes(DocumentsController.Container, stored.StoragePath));
        Assert.Null(_factory.Storage.Inner.GetBytes(DocumentsController.Container, pending.PendingUploadPath!));
    }

    [Fact]
    public async Task Finalize_ByAnyoneButTheUploader_IsNotFound()
    {
        var start = await StartAsync(Examiner());
        var pending = _factory.Documents.All.Single();
        await _factory.Storage.Inner.UploadAsync(DocumentsController.Container, pending.PendingUploadPath!, new MemoryStream(Pdf), "application/pdf");

        var response = await Client(ChoRolePermissions.ClaimsExaminer, "someone-else").PostAsync($"/api/v1/documents/{start.DocumentId}/finalize", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Finalize_OfADuplicate_MergesIntoTheExistingDocument()
    {
        var existing = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-9"]);
        var start = await StartAsync(Examiner());
        var pending = _factory.Documents.All.Single(d => d.Id == start.DocumentId);
        await _factory.Storage.Inner.UploadAsync(DocumentsController.Container, pending.PendingUploadPath!, new MemoryStream(Pdf), "application/pdf");

        var response = await Examiner().PostAsync($"/api/v1/documents/{start.DocumentId}/finalize", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var only = Assert.Single(_factory.Documents.All);
        Assert.Equal(existing.Id, only.Id);
        Assert.Equal(2, only.Links.Count);
    }

    [Fact]
    public async Task ARepeatedFinalize_NeverLeavesTheDocumentWithoutContent()
    {
        var start = await StartAsync(Examiner());
        var pending = _factory.Documents.All.Single();
        await _factory.Storage.Inner.UploadAsync(DocumentsController.Container, pending.PendingUploadPath!, new MemoryStream(Pdf), "application/pdf");

        var first = await Examiner().PostAsync($"/api/v1/documents/{start.DocumentId}/finalize", null);
        var retry = await Examiner().PostAsync($"/api/v1/documents/{start.DocumentId}/finalize", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, retry.StatusCode);
        var stored = _factory.Documents.All.Single();
        Assert.Equal(Pdf, _factory.Storage.Inner.GetBytes(DocumentsController.Container, stored.StoragePath));
    }

    [Fact]
    public async Task IdenticalBytes_MergeIntoAnyCopyTheUploaderCanOpen_NotJustTheOldest()
    {
        await UploadAsync(UmCoordinator(), Pdf, links: ["Claim:CLM-1"], sensitivity: ["BEHAVIORAL_HEALTH"]);
        var mine = await UploadAsync(Examiner(), Pdf, links: ["Claim:CLM-2"]);

        var again = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, links: ["Claim:CLM-3"]));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, _factory.Documents.All.Count);
        Assert.Equal(2, _factory.Documents.All.Single(d => d.Id == mine.Id).Links.Count);
    }

    [Fact]
    public async Task ReadingAnAccessLog_IsItselfAudited()
    {
        var doc = await UploadAsync(Examiner(), Pdf);

        await Compliance().GetAsync($"/api/v1/documents/{doc.Id}/access-log");

        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.AccessLog && e.Allowed && e.Actor == "compliance-1");
    }

    [Fact]
    public async Task PresignedUpload_WhenStorageCantSign_Is501_AndRecordsNothing()
    {
        _factory.Storage.CanSign = false;

        var response = await Examiner().PostAsJsonAsync("/api/v1/documents", new
        {
            type = "ItemizedBill",
            contentType = "application/pdf",
            links = new[] { "Claim:CLM-1" }
        });

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Empty(_factory.Documents.All);
    }
}
