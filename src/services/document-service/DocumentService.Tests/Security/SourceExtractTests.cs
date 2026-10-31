using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Models;

namespace DocumentService.Tests.Security;

/// <summary>
/// The file drop keeps every file a plan's systems send as a SourceExtract:
/// CSV or X12 only, linked to its import run, readable only with claims:import.
/// No other document type may be a spreadsheet or an X12 file.
/// </summary>
public sealed class SourceExtractTests : IClassFixture<DocumentServiceFactory>, IDisposable
{
    private const string Tenant = "tenant-1";
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes("Claim unique identifier,Member id\nC-1,M-1\n");
    private static readonly byte[] X12 = Encoding.ASCII.GetBytes("ISA*00*          *00*          *ZZ*A*ZZ*B*261101*0930*^*00501*000000001*0*T*:~");

    private readonly DocumentServiceFactory _factory;

    public SourceExtractTests(DocumentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    public void Dispose() => _factory.Reset();

    private HttpClient Client(string role, string user)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string type = DocumentTypes.SourceExtract,
        string link = "ImportRun:20261031T200000Z-abc", string? channel = DocumentChannels.Sftp)
    {
        var form = new MultipartFormDataContent { { new StringContent(type), "Type" }, { new StringContent(link), "Links" } };
        if (channel is not null)
            form.Add(new StringContent(channel), "SourceChannel");
        form.Add(new StringContent("DropRun=20261031T200000Z-abc"), "ExternalIds");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", "qnxt_claims_20261031.csv");
        return form;
    }

    [Theory]
    [InlineData("text/csv")]
    [InlineData("application/edi-x12")]
    public async Task A_source_extract_is_stored_linked_to_its_run_under_the_ten_year_policy(string contentType)
    {
        var bytes = contentType == "text/csv" ? Csv : X12;
        var response = await Client(ChoRolePermissions.TenantAdmin, "admin-1").PostAsync("/api/v1/documents", Form(bytes, contentType));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = Assert.Single(_factory.Documents.All);
        Assert.Equal(DocumentTypes.SourceExtract, stored.Type);
        Assert.Equal(DocumentChannels.Sftp, stored.Source.Channel);
        Assert.Equal("DEFAULT-10Y", stored.Retention.PolicyId);
        Assert.Contains(stored.Links, l => l.Kind == DocumentLinkKind.ImportRun);
    }

    [Fact]
    public async Task Linking_to_an_import_run_needs_claims_import()
    {
        var response = await Client(ChoRolePermissions.ClaimsExaminer, "examiner-1").PostAsync("/api/v1/documents", Form(Csv, "text/csv"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Documents.All);
    }

    [Fact]
    public async Task No_other_document_type_may_be_a_spreadsheet()
    {
        var response = await Client(ChoRolePermissions.TenantAdmin, "admin-1")
            .PostAsync("/api/v1/documents", Form(Csv, "text/csv", type: "MedicalRecord", link: "Claim:CLM-1"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task A_source_extract_is_never_a_pdf()
    {
        var response = await Client(ChoRolePermissions.TenantAdmin, "admin-1")
            .PostAsync("/api/v1/documents", Form(Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"), "application/pdf"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task An_X12_extract_must_start_with_its_ISA()
    {
        var response = await Client(ChoRolePermissions.TenantAdmin, "admin-1")
            .PostAsync("/api/v1/documents", Form(Csv, "application/edi-x12"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public void Source_extracts_have_their_own_size_cap()
    {
        var policy = new DocumentService.Services.DocumentUploadPolicy(10, ["application/pdf"], maxSourceExtractBytes: 1000);

        Assert.Equal(1000, policy.MaxBytesFor(DocumentTypes.SourceExtract));
        Assert.Equal(10, policy.MaxBytesFor("MedicalRecord"));
        Assert.False(policy.IsAllowedContentType("MedicalRecord", "text/csv"));
        Assert.True(policy.IsAllowedContentType(DocumentTypes.SourceExtract, "text/csv; charset=utf-8"));
    }
}
