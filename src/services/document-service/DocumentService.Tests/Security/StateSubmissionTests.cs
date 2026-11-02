using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Models;

namespace DocumentService.Tests.Security;

/// <summary>
/// A file CHO generated for a state regulator (a Texas HHSC monthly text
/// file) is kept as a StateSubmission: plain text or CSV only, linked to its
/// state report run, readable only with reports:compliance.
/// </summary>
public sealed class StateSubmissionTests : IClassFixture<DocumentServiceFactory>, IDisposable
{
    private const string Tenant = "tenant-1";
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("000001SUP092027014F260915ABCD1234 123456789\r\n");

    private readonly DocumentServiceFactory _factory;

    public StateSubmissionTests(DocumentServiceFactory factory)
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

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string type = DocumentTypes.StateSubmission,
        string link = "StateReport:tx-member-appeals-01-202609-20261102T0030Z-ab12cd34")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(type), "Type" }, { new StringContent(link), "Links" },
            { new StringContent(DocumentChannels.Generated), "SourceChannel" }, { new StringContent("TX"), "StateCode" },
        };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", "tx-member-appeals-01-202609.txt");
        return form;
    }

    [Fact]
    public async Task A_state_submission_is_kept_as_text_linked_to_its_report_run()
    {
        var response = await Client(ChoRolePermissions.TenantAdmin, "admin-1").PostAsync("/api/v1/documents", Form(Text, "text/plain"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = Assert.Single(_factory.Documents.All);
        Assert.Equal(DocumentTypes.StateSubmission, stored.Type);
        Assert.Contains(stored.Links, l => l.Kind == DocumentLinkKind.StateReport);
    }

    [Fact]
    public async Task Linking_to_a_state_report_needs_reports_compliance()
    {
        var response = await Client(ChoRolePermissions.ClaimsExaminer, "examiner-1").PostAsync("/api/v1/documents", Form(Text, "text/plain"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Documents.All);
    }

    [Fact]
    public async Task A_state_submission_is_never_a_pdf_and_no_other_type_is_plain_text()
    {
        var admin = Client(ChoRolePermissions.TenantAdmin, "admin-1");

        var pdf = await admin.PostAsync("/api/v1/documents", Form(Encoding.ASCII.GetBytes("%PDF-1.7\n%%EOF"), "application/pdf"));
        var text = await admin.PostAsync("/api/v1/documents", Form(Text, "text/plain", type: "DecisionLetter", link: "Appeal:A-1"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, pdf.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
    }
}
