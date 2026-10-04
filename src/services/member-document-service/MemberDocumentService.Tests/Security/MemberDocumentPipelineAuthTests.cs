using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using MemberDocumentService.Models;
using MemberDocumentService.Repositories;
using MemberDocumentService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MemberDocumentService.Tests.Security;

/// <summary>
/// The real member-document-service pipeline with storage replaced. Member
/// documents are PHI: every caller needs a CHO token, the tenant and the
/// uploader come from it, reads need members:read and writes members:write.
/// Uploads are limited by content type, signature and size, and blob paths
/// carry the token tenant.
/// </summary>
public class MemberDocumentPipelineAuthTests : IClassFixture<MemberDocumentPipelineAuthTests.Factory>
{
    public const long MaxBytes = 4096;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<IMemberDocumentRepository> Repository { get; } = new();
        public Mock<IMemberDocumentBlobService> Blobs { get; } = new();
        public AuditCapture Audit { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["BlobStorage:ConnectionString"] = "UseDevelopmentStorage=true",
                ["MemberDocuments:MaxUploadBytes"] = MaxBytes.ToString(),
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMemberDocumentRepository>();
                services.RemoveAll<IMemberDocumentBlobService>();
                services.AddSingleton(Repository.Object);
                services.AddSingleton(Blobs.Object);
                services.AddSingleton<ILoggerProvider>(Audit);
            });
        }
    }

    /// <summary>Collects the service's AUDIT log lines.</summary>
    public sealed class AuditCapture : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

        public void Clear() { lock (_lines) _lines.Clear(); }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (line.StartsWith("AUDIT", StringComparison.Ordinal))
                lock (_lines) _lines.Add(line);
        }

        public void Dispose() { }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "doc-user-7";
    private const string DocId = "doc-1";

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF");
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] Html = Encoding.ASCII.GetBytes("<html><script>alert(document.cookie)</script></html>");

    private readonly Factory _factory;
    private readonly List<(string Path, byte[] Bytes)> _uploads = new();

    public MemberDocumentPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Repository.Reset();
        _factory.Blobs.Reset();
        _factory.Audit.Clear();

        _factory.Repository.Setup(r => r.CreateAsync(It.IsAny<MemberDocument>()))
            .ReturnsAsync((MemberDocument d) => d);
        _factory.Repository.Setup(r => r.UpdateAsync(It.IsAny<MemberDocument>()))
            .ReturnsAsync((MemberDocument d) => d);
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId))
            .ReturnsAsync(() => StoredDocument());
        _factory.Repository.Setup(r => r.ListByMemberIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<MemberDocument>());

        _factory.Blobs.Setup(b => b.UploadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
                It.IsAny<string>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string path, Stream s, string _, IDictionary<string, string> _, CancellationToken _) =>
            {
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                _uploads.Add((path, copy.ToArray()));
                return Task.FromResult(copy.Length);
            });
        _factory.Blobs.Setup(b => b.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Pdf));
        _factory.Blobs.Setup(b => b.GenerateUploadSasUri(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()))
            .Returns((string c, string p, string _, DateTimeOffset _) => new Uri($"https://blob.example/{c}/{p}?sig=x"));
    }

    private static MemberDocument StoredDocument(string? pendingPath = null, bool legalHold = false) => new()
    {
        Id = DocId,
        TenantId = Tenant,
        MemberId = "mem-1",
        Category = "EOB",
        BlobContainer = "member-documents",
        BlobPath = $"tenants/{Tenant}/members/mem-1/{DocId}.pdf",
        ContentType = "application/pdf",
        PendingUploadBlobPath = pendingPath,
        LegalHold = legalHold,
        LegalHoldSetBy = legalHold ? "compliance-9" : null,
        LegalHoldHistory = legalHold
            ? [new LegalHoldEvent { Action = LegalHoldAction.Set, Actor = "compliance-9", Reason = "subpoena" }]
            : [],
    };

    private HttpClient Client(string tenant = Tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string contentType, string memberId = "mem-1",
        string fileName = "eob.pdf", string fileField = "file", bool legalHold = false)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(memberId), "MemberId" },
            { new StringContent("EOB"), "Category" },
            { new StringContent("Uploaded"), "Source" },
            { new StringContent("someone-else"), "UploadedBy" },
        };
        if (legalHold)
        {
            form.Add(new StringContent("true"), "LegalHold");
            form.Add(new StringContent("litigation 2026-17"), "LegalHoldReason");
        }
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, fileField, fileName);
        return form;
    }

    private void NothingStored()
    {
        _factory.Repository.Verify(r => r.CreateAsync(It.IsAny<MemberDocument>()), Times.Never);
        Assert.Empty(_uploads);
    }

    // ── Authentication and tenant ───────────────────────────────────

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/v1/member-documents/{DocId}/content");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        _factory.Blobs.Verify(b => b.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        // Before: the local TenantMiddleware took the tenant from X-Tenant-ID with
        // no authentication, so anyone could download any tenant's documents.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var content = await client.GetAsync($"/api/v1/member-documents/{DocId}/content");
        var list = await client.GetAsync("/api/v1/members/mem-1/documents");
        var upload = await client.PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf"));

        Assert.Equal(HttpStatusCode.Unauthorized, content.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
        NothingStored();
    }

    [Fact]
    public async Task DevTenantHeaderWithoutToken_IsRejected()
    {
        // Before: X-Dev-Tenant-ID was a second unauthenticated tenant source.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", Tenant);

        var response = await client.GetAsync($"/api/v1/member-documents/{DocId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HeaderNamingAnotherTenant_IsForbidden()
    {
        // Before: the header won, so a token for tenant-1 read tenant-2's documents.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(User, Tenant, [ChoRolePermissions.TenantAdmin]));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/v1/member-documents/{DocId}/content");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DocumentsAreLookedUpInTheTokenTenant()
    {
        var response = await Client(OtherTenant).GetAsync($"/api/v1/member-documents/{DocId}/content");

        // doc-1 belongs to tenant-1; a tenant-2 token cannot reach it.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        _factory.Repository.Verify(r => r.GetByIdAsync(OtherTenant, DocId), Times.Once);
        _factory.Blobs.Verify(b => b.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Permissions ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/v1/member-documents/doc-1")]
    [InlineData("/api/v1/member-documents/doc-1/content")]
    [InlineData("/api/v1/members/mem-1/documents")]
    [InlineData("/api/v1/members/mem-1/fhir/DocumentReference")]
    public async Task RoleWithoutMembersRead_CannotReadDocuments(string path)
    {
        // ProviderRelations holds no members:* permission.
        var response = await Client(Tenant, ChoRolePermissions.ProviderRelations).GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MembersRead_CanDownload()
    {
        var response = await Client(Tenant, ChoRolePermissions.MemberServices).GetAsync($"/api/v1/member-documents/{DocId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ReadOnlyRole_CannotUploadOrChangeLegalHold()
    {
        // MemberServices holds members:read but not members:write.
        var client = Client(Tenant, ChoRolePermissions.MemberServices);

        var upload = await client.PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf"));
        var hold = await client.PutAsJsonAsync($"/api/v1/member-documents/{DocId}/legal-hold", new { legalHold = true });
        var finalize = await client.PostAsync($"/api/v1/member-documents/{DocId}/finalize", null);

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, hold.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, finalize.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
        NothingStored();
    }

    // ── Legal holds ─────────────────────────────────────────────────

    private static object Hold(bool legalHold, string? reason = "subpoena 44") => new { legalHold, reason };

    private void NoHoldChange()
    {
        _factory.Repository.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
        _factory.Blobs.Verify(b => b.SetTagsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_factory.Audit.Lines);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnrollmentSpecialist_WithMembersWrite_CannotSetOrReleaseALegalHold(bool legalHold)
    {
        // Before: the hold endpoint took the default members:write, so any
        // enrollment user could place or lift a hold.
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(StoredDocument(legalHold: !legalHold));

        var response = await Client(Tenant, ChoRolePermissions.EnrollmentSpecialist)
            .PutAsJsonAsync($"/api/v1/member-documents/{DocId}/legal-hold", Hold(legalHold));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
        NoHoldChange();
    }

    [Fact]
    public async Task ComplianceViewer_CannotSetALegalHold()
    {
        var response = await Client(Tenant, ChoRolePermissions.ComplianceViewer)
            .PutAsJsonAsync($"/api/v1/member-documents/{DocId}/legal-hold", Hold(true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.Repository.VerifyNoOtherCalls();
        NoHoldChange();
    }

    [Theory]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task LegalHoldRole_SetsAHold_RecordedAndAudited(string role)
    {
        // ComplianceOfficer has no members:write; records:legal-hold is enough.
        var response = await Client(Tenant, role)
            .PutAsJsonAsync($"/api/v1/member-documents/{DocId}/legal-hold", Hold(true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.Repository.Verify(r => r.UpdateAsync(It.Is<MemberDocument>(d =>
            d.LegalHold && d.LegalHoldSetBy == User && d.LegalHoldReason == "subpoena 44"
            && d.LegalHoldHistory.Count == 1
            && d.LegalHoldHistory[0].Action == LegalHoldAction.Set
            && d.LegalHoldHistory[0].Actor == User)), Times.Once);
        _factory.Blobs.Verify(b => b.SetTagsAsync("member-documents", $"tenants/{Tenant}/members/mem-1/{DocId}.pdf",
            It.Is<IDictionary<string, string>>(t => t["legalHold"] == "true"), It.IsAny<CancellationToken>()), Times.Once);
        var audit = Assert.Single(_factory.Audit.Lines);
        Assert.Contains("legal hold Set", audit);
        Assert.Contains($"{Tenant}/{DocId}", audit);
        Assert.Contains($"by {User}", audit);
        Assert.Contains("subpoena 44", audit);
    }

    [Fact]
    public async Task ComplianceOfficer_ReleasesAHold_WithAReason_RecordedAndAudited()
    {
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(StoredDocument(legalHold: true));

        var response = await Client(Tenant, ChoRolePermissions.ComplianceOfficer)
            .PutAsJsonAsync($"/api/v1/member-documents/{DocId}/legal-hold", Hold(false, "case dismissed\nAUDIT forged"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _factory.Repository.Verify(r => r.UpdateAsync(It.Is<MemberDocument>(d =>
            !d.LegalHold
            && d.LegalHoldHistory.Count == 2
            && d.LegalHoldHistory[1].Action == LegalHoldAction.Released
            && d.LegalHoldHistory[1].Actor == User
            && d.LegalHoldHistory[1].Reason == "case dismissed\nAUDIT forged")), Times.Once);
        var audit = Assert.Single(_factory.Audit.Lines);
        Assert.Contains("legal hold Released", audit);
        Assert.Contains($"by {User}", audit);
        // The reason cannot start a second log line.
        Assert.DoesNotContain('\n', audit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ReleasingAHold_WithoutAReason_IsRejected(string? reason)
    {
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(StoredDocument(legalHold: true));

        var response = await Client(Tenant, ChoRolePermissions.ComplianceOfficer)
            .PutAsJsonAsync($"/api/v1/member-documents/{DocId}/legal-hold", Hold(false, reason));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        NoHoldChange();
    }

    [Fact]
    public async Task EnrollmentSpecialist_CannotUploadUnderALegalHold()
    {
        // Before: an upload with LegalHold=true placed a hold with members:write.
        var client = Client(Tenant, ChoRolePermissions.EnrollmentSpecialist);

        var multipart = await client.PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf", legalHold: true));
        var presigned = await client.PostAsJsonAsync("/api/v1/member-documents", new
        {
            memberId = "mem-1",
            category = "Letter",
            fileName = "letter.pdf",
            contentType = "application/pdf",
            legalHold = true,
        });

        Assert.Equal(HttpStatusCode.Forbidden, multipart.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, presigned.StatusCode);
        NothingStored();
        _factory.Blobs.Verify(b => b.GenerateUploadSasUri(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task TenantAdmin_UploadsUnderALegalHold_RecordedAndAudited()
    {
        var response = await Client(Tenant, ChoRolePermissions.TenantAdmin)
            .PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf", legalHold: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _factory.Repository.Verify(r => r.CreateAsync(It.Is<MemberDocument>(d =>
            d.LegalHold && d.LegalHoldSetBy == User && d.LegalHoldReason == "litigation 2026-17"
            && d.LegalHoldHistory.Count == 1)), Times.Once);
        Assert.Contains("legal hold Set", Assert.Single(_factory.Audit.Lines));
    }

    [Theory]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task HeldDocument_CannotBeDeletedModifiedOrReplaced(string role)
    {
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(StoredDocument(legalHold: true));
        var client = Client(Tenant, role);

        var delete = await client.DeleteAsync($"/api/v1/member-documents/{DocId}");
        var put = await client.PutAsJsonAsync($"/api/v1/member-documents/{DocId}", new { category = "Other" });
        var patch = await client.PatchAsync($"/api/v1/member-documents/{DocId}", JsonContent.Create(new { category = "Other" }));
        var refinalize = await client.PostAsync($"/api/v1/member-documents/{DocId}/finalize", null);

        // No route deletes or edits a document (405: the path exists for GET only).
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, patch.StatusCode);
        // Before: finalize of a finalized document rewrote its size from the blob.
        Assert.Equal(HttpStatusCode.Conflict, refinalize.StatusCode);
        _factory.Repository.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
        _factory.Blobs.Verify(b => b.DeleteIfExistsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _factory.Blobs.Verify(b => b.SetTagsAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_uploads);
    }

    // ── Uploads ─────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_RecordsTheTokenSubjectAsUploader()
    {
        // Before: the form's UploadedBy ("someone-else") was stored as the uploader.
        var response = await Client(Tenant, ChoRolePermissions.EnrollmentSpecialist)
            .PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _factory.Repository.Verify(r => r.CreateAsync(It.Is<MemberDocument>(d =>
            d.UploadedBy == User && d.TenantId == Tenant)), Times.Once);
    }

    [Fact]
    public async Task Upload_StoresTheBlobUnderTheTokenTenant()
    {
        // Before: blobs were stored at members/{memberId}/..., shared by every tenant.
        var response = await Client().PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var upload = Assert.Single(_uploads);
        Assert.StartsWith($"tenants/{Tenant}/members/mem-1/", upload.Path);
        Assert.EndsWith(".pdf", upload.Path);
        Assert.Equal(Pdf, upload.Bytes);
    }

    [Theory]
    [InlineData("../tenant-2/members/mem-9")]
    [InlineData("mem-1/../../tenants/tenant-2")]
    [InlineData("..")]
    public async Task Upload_MemberIdThatIsNotOnePathSegment_IsRejected(string memberId)
    {
        // Before: the member id went into the blob path verbatim.
        var response = await Client().PostAsync("/api/v1/member-documents", Upload(Pdf, "application/pdf", memberId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        NothingStored();
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/x-msdownload")]
    [InlineData("image/svg+xml")]
    public async Task Upload_ContentTypeNotAllowed_IsRejected(string contentType)
    {
        // Before: any content type was stored and later served with that type.
        var response = await Client().PostAsync("/api/v1/member-documents", Upload(Html, contentType, fileName: "x.html"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        NothingStored();
    }

    [Fact]
    public async Task Upload_ContentThatIsNotTheDeclaredType_IsRejected()
    {
        // Before: an HTML page declared as application/pdf was accepted.
        var response = await Client().PostAsync("/api/v1/member-documents", Upload(Html, "application/pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        NothingStored();
    }

    [Fact]
    public async Task Upload_OverTheSizeLimit_IsRejected()
    {
        // Before: there was no size limit below Kestrel's 30 MB request cap.
        var big = new byte[MaxBytes + 1];
        Pdf.CopyTo(big, 0);

        var response = await Client().PostAsync("/api/v1/member-documents", Upload(big, "application/pdf"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        NothingStored();
    }

    [Fact]
    public async Task IdCardServiceToken_CanUploadACard_AsIdCardService()
    {
        // idcard-service uploads card PDFs/PNGs with its own service token when no
        // user is on the call (e.g. reissue jobs), in exactly this form layout.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("idcard-service", Tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        var form = Upload(Png, "image/png", fileName: "card.png", fileField: "File");
        form.Add(new StringContent("idcard"), "Subcategory");

        var response = await client.PostAsync("/api/v1/member-documents", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // idcard-service reads the new document's id from the response.
        using var created = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(created.RootElement.GetProperty("id").GetString()));
        _factory.Repository.Verify(r => r.CreateAsync(It.Is<MemberDocument>(d =>
            d.UploadedBy == "idcard-service" && d.TenantId == Tenant && d.ContentType == "image/png")), Times.Once);
    }

    // ── Downloads ───────────────────────────────────────────────────

    [Fact]
    public async Task Download_IsAnAttachmentThatIsNeverSniffed()
    {
        var response = await Client().GetAsync($"/api/v1/member-documents/{DocId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
    }

    // ── Pre-signed uploads ──────────────────────────────────────────

    [Fact]
    public async Task PresignedUpload_SasCoversOnlyAStagingBlobUnderTheTokenTenant()
    {
        var response = await Client().PostAsJsonAsync("/api/v1/member-documents", new
        {
            memberId = "mem-1",
            category = "Letter",
            fileName = "letter.pdf",
            contentType = "application/pdf",
            uploadedBy = "someone-else",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PresignedUploadResponse>();
        Assert.StartsWith($"tenants/{Tenant}/members/mem-1/", body!.BlobPath);
        Assert.EndsWith(".upload", body.BlobPath);
        Assert.True(body.ExpiresAtUtc <= DateTime.UtcNow.AddMinutes(15).AddSeconds(5));
        _factory.Blobs.Verify(b => b.GenerateUploadSasUri("member-documents", body.BlobPath, "application/pdf", It.IsAny<DateTimeOffset>()), Times.Once);
        _factory.Repository.Verify(r => r.CreateAsync(It.Is<MemberDocument>(d =>
            d.UploadedBy == User
            && d.PendingUploadBlobPath == body.BlobPath
            && d.BlobPath != body.BlobPath)), Times.Once);
    }

    [Fact]
    public async Task PresignedUpload_ContentTypeNotAllowed_IsRejected()
    {
        var response = await Client().PostAsJsonAsync("/api/v1/member-documents", new
        {
            memberId = "mem-1",
            category = "Letter",
            fileName = "letter.html",
            contentType = "text/html",
        });

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        _factory.Blobs.Verify(b => b.GenerateUploadSasUri(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
        NothingStored();
    }

    [Fact]
    public async Task PendingUpload_CannotBeDownloaded()
    {
        // Before: whatever the SAS holder wrote was served before anyone checked it.
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId))
            .ReturnsAsync(StoredDocument(pendingPath: $"tenants/{Tenant}/members/mem-1/{DocId}.pdf.upload"));

        var response = await Client().GetAsync($"/api/v1/member-documents/{DocId}/content");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        _factory.Blobs.Verify(b => b.DownloadAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Finalize_RejectsAndDiscardsContentThatIsNotTheDeclaredType()
    {
        // Before: finalize only tagged the blob, so HTML uploaded through the SAS
        // as "application/pdf" became a downloadable document.
        var staging = $"tenants/{Tenant}/members/mem-1/{DocId}.pdf.upload";
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(StoredDocument(staging));
        _factory.Blobs.Setup(b => b.DownloadAsync("member-documents", staging, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Html));

        var response = await Client().PostAsync($"/api/v1/member-documents/{DocId}/finalize", null);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_uploads);
        _factory.Blobs.Verify(b => b.DeleteIfExistsAsync("member-documents", staging, It.IsAny<CancellationToken>()), Times.Once);
        _factory.Repository.Verify(r => r.UpdateAsync(It.IsAny<MemberDocument>()), Times.Never);
    }

    [Fact]
    public async Task Finalize_RejectsAndDiscardsOversizeContent()
    {
        var staging = $"tenants/{Tenant}/members/mem-1/{DocId}.pdf.upload";
        var big = new byte[MaxBytes + 1];
        Pdf.CopyTo(big, 0);
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(StoredDocument(staging));
        _factory.Blobs.Setup(b => b.DownloadAsync("member-documents", staging, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(big));

        var response = await Client().PostAsync($"/api/v1/member-documents/{DocId}/finalize", null);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_uploads);
        _factory.Blobs.Verify(b => b.DeleteIfExistsAsync("member-documents", staging, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Finalize_CopiesValidContentOutOfReachOfTheSas()
    {
        var staging = $"tenants/{Tenant}/members/mem-1/{DocId}.pdf.upload";
        var stored = StoredDocument(staging);
        _factory.Repository.Setup(r => r.GetByIdAsync(Tenant, DocId)).ReturnsAsync(stored);
        _factory.Blobs.Setup(b => b.DownloadAsync("member-documents", staging, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Pdf));

        var response = await Client().PostAsync($"/api/v1/member-documents/{DocId}/finalize", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var upload = Assert.Single(_uploads);
        Assert.Equal(stored.BlobPath, upload.Path);
        Assert.Equal(Pdf, upload.Bytes);
        _factory.Blobs.Verify(b => b.DeleteIfExistsAsync("member-documents", staging, It.IsAny<CancellationToken>()), Times.Once);
        _factory.Repository.Verify(r => r.UpdateAsync(It.Is<MemberDocument>(d =>
            d.PendingUploadBlobPath == null
            && d.SizeBytes == Pdf.Length
            && d.ContentHashSha256.Length == 64)), Times.Once);
    }
}
