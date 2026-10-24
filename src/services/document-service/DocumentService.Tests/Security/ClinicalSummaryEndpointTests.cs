using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Models;

namespace DocumentService.Tests.Security;

/// <summary>
/// Reading a stored C-CDA as a clinical summary, through the real pipeline.
///
/// The summary is the document's contents under another name, so it must pass
/// exactly the gates <c>/content</c> passes — permission, tenant, sensitivity,
/// scan, audit — and never become a side door into bytes the caller may not
/// open.
/// </summary>
public sealed class ClinicalSummaryEndpointTests : IClassFixture<DocumentServiceFactory>, IDisposable
{
    private const string Tenant = "tenant-1";
    private const string CdaType = "application/hl7-cda+xml";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF");

    private readonly DocumentServiceFactory _factory;

    public ClinicalSummaryEndpointTests(DocumentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
        // The scan gate has its own tests on /content; these are about the summary.
        _factory.AllowUnscanned = true;
    }

    public void Dispose() => _factory.Reset();

    private HttpClient Client(string role, string user, string tenant = Tenant)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient Examiner() => Client(ChoRolePermissions.ClaimsExaminer, "examiner-1");
    private HttpClient UmCoordinator() => Client(ChoRolePermissions.UMCoordinator, "um-1");

    private static byte[] Cda => Encoding.UTF8.GetBytes(
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <ClinicalDocument xmlns="urn:hl7-org:v3">
          <templateId root="2.16.840.1.113883.10.20.22.1.1"/>
          <id root="2.16.840.1.113883.19.5" extension="DOC-1"/>
          <title>Discharge Summary</title>
          <effectiveTime value="20260115103000-0700"/>
          <component><structuredBody><component>
            <section>
              <templateId root="2.16.840.1.113883.10.20.22.2.5"/>
              <title>Problems</title>
              <text><table><tr><td>Hypertension</td></tr></table></text>
              <entry>
                <act classCode="ACT" moodCode="EVN">
                  <templateId root="2.16.840.1.113883.10.20.22.4.3"/>
                  <statusCode code="active"/>
                  <entryRelationship typeCode="SUBJ">
                    <observation classCode="OBS" moodCode="EVN">
                      <templateId root="2.16.840.1.113883.10.20.22.4.4"/>
                      <code code="55607006" codeSystem="2.16.840.1.113883.6.96" displayName="Problem"/>
                      <effectiveTime><low value="20200115"/></effectiveTime>
                      <value xsi:type="CD" code="38341003" codeSystem="2.16.840.1.113883.6.96"
                             displayName="Hypertension"
                             xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"/>
                    </observation>
                  </entryRelationship>
                </act>
              </entry>
            </section>
          </component></structuredBody></component>
        </ClinicalDocument>
        """);

    private static MultipartFormDataContent Form(byte[] bytes, string contentType, string[] links, string[]? sensitivity = null)
    {
        var form = new MultipartFormDataContent { { new StringContent("MedicalRecord"), "Type" } };
        foreach (var link in links)
            form.Add(new StringContent(link), "Links");
        foreach (var tag in sensitivity ?? [])
            form.Add(new StringContent(tag), "Sensitivity");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", "record");
        return form;
    }

    private async Task<DocumentView> UploadAsync(
        HttpClient client, byte[] bytes, string contentType, string[] links, string[]? sensitivity = null)
    {
        var response = await client.PostAsync("/api/v1/documents", Form(bytes, contentType, links, sensitivity));
        Assert.True(response.IsSuccessStatusCode, $"upload: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<DocumentView>(Json))!;
    }

    private static string On(string kind, string id) => $"subjectKind={kind}&subjectId={id}";

    private static Task<ClinicalSummary?> SummaryAsync(HttpClient client, string id, string kind = "Claim", string subject = "CLM-1") =>
        client.GetFromJsonAsync<ClinicalSummary>($"/api/v1/documents/{id}/clinical-summary?{On(kind, subject)}", Json);

    // ── A C-CDA can be filed at all ───────────────────────────────────────

    [Fact]
    public async Task AClinicalDocumentCanBeUploaded()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);

        Assert.Equal(CdaType, doc.ContentType);
    }

    [Fact]
    public async Task SomethingThatIsNotXmlCannotBeFiledAsAClinicalDocument()
    {
        // The declared type is what the reader trusts later, so a PDF labelled
        // as a C-CDA is refused at the door rather than parsed at read time.
        var response = await Examiner().PostAsync("/api/v1/documents", Form(Pdf, CdaType, ["Claim:CLM-1"]));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ── Reading the summary ───────────────────────────────────────────────

    [Fact]
    public async Task TheSummaryNamesTheSectionsAndMapsTheProblems()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1", "Member:M-1"]);

        var summary = await SummaryAsync(Examiner(), doc.Id);

        Assert.Equal("Discharge Summary", summary!.Title);
        Assert.True(summary.MemberLinked);
        var section = Assert.Single(summary.Sections);
        Assert.Equal("Problems", section.Kind);
        Assert.Equal(1, section.EntryCount);
        Assert.Contains("\"resourceType\":\"Condition\"", Assert.Single(section.Resources));
    }

    [Fact]
    public async Task ADocumentLinkedToNoMemberIsReadButAttributedToNobody()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);

        var summary = await SummaryAsync(Examiner(), doc.Id);

        Assert.False(summary!.MemberLinked);
        Assert.Empty(Assert.Single(summary.Sections).Resources);
        Assert.Contains("Hypertension", Assert.Single(summary.Sections).NarrativeXml);
    }

    [Fact]
    public async Task ADocumentChoReadsNoStructureFromSaysSo_RatherThanReturningAnEmptySummary()
    {
        var doc = await UploadAsync(Examiner(), Pdf, "application/pdf", ["Claim:CLM-1"]);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/clinical-summary?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ASubjectTheDocumentIsNotLinkedTo_OpensNothing()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/clinical-summary?{On("Claim", "CLM-2")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ARoleWithoutDocumentsRead_IsRefused()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);

        var response = await Client(ChoRolePermissions.FinanceApprover, "finance-1")
            .GetAsync($"/api/v1/documents/{doc.Id}/clinical-summary?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ASensitiveDocumentIsNoMoreReadableAsASummaryThanAsBytes()
    {
        // The whole point of the gate is the clinical content, which is what a
        // summary is made of.
        var doc = await UploadAsync(UmCoordinator(), Cda, CdaType, ["Claim:CLM-1"], sensitivity: ["BEHAVIORAL_HEALTH"]);

        var response = await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/clinical-summary?{On("Claim", "CLM-1")}");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"sensitive summary returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task AnotherTenantCannotReadTheSummary()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);

        var other = Client(ChoRolePermissions.ClaimsExaminer, "examiner-2", "tenant-2");
        var response = await other.GetAsync($"/api/v1/documents/{doc.Id}/clinical-summary?{On("Claim", "CLM-1")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReadingTheSummaryIsAudited_AsOpeningTheDocument()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);
        _factory.Audit.Clear();

        await SummaryAsync(Examiner(), doc.Id);

        var entry = Assert.Single(_factory.Audit.Entries);
        Assert.Equal(DocumentAccessAction.Download, entry.Action);
        Assert.Equal("examiner-1", entry.Actor);
        Assert.True(entry.Allowed);
    }

    [Fact]
    public async Task ARefusedReadIsAuditedToo()
    {
        var doc = await UploadAsync(Examiner(), Cda, CdaType, ["Claim:CLM-1"]);
        _factory.Audit.Clear();

        await Examiner().GetAsync($"/api/v1/documents/{doc.Id}/clinical-summary?{On("Claim", "CLM-2")}");

        Assert.False(Assert.Single(_factory.Audit.Entries).Allowed);
    }
}
