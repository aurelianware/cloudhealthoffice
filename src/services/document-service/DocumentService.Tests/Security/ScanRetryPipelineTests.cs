using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using DocumentService.Controllers;
using DocumentService.Models;

namespace DocumentService.Tests.Security;

public sealed class ScanRetryPipelineTests : IClassFixture<DocumentServiceFactory>, IDisposable
{
    private readonly DocumentServiceFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nsynthetic scan retry\n%%EOF");
    private const string Subject = "subjectKind=Claim&subjectId=CLM-RETRY";

    public ScanRetryPipelineTests(DocumentServiceFactory factory) { _factory = factory; factory.Reset(); }
    public void Dispose() => _factory.Reset();
    private HttpClient Client(string tenant = "tenant-1", string role = ChoRolePermissions.ClaimsExaminer)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("retry-user", role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }
    private string RetryUrl(string id) => $"/api/v1/documents/{id}/scan-retry?{Subject}";
    private async Task<CatalogDocument> FailedDocument()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Other"), "Type");
        form.Add(new StringContent("Claim:CLM-RETRY"), "Links");
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", "synthetic.pdf");
        var response = await Client().PostAsync("/api/v1/documents", form);
        response.EnsureSuccessStatusCode();
        var document = Assert.Single(_factory.Documents.All);
        document.Scan = new() { Status = ScanStatus.Failed, Engine = "test" };
        _factory.Documents.Seed(document);
        return document;
    }

    [Fact]
    public async Task Clean_retry_preserves_bytes_links_and_hash_and_audits_the_actor()
    {
        var original = await FailedDocument();
        _factory.Scanner.Scan = async (stream, ct) =>
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            Assert.Equal(Pdf, buffer.ToArray());
            return new() { Status = ScanStatus.Clean, Engine = "test", At = DateTime.UtcNow };
        };
        var response = await Client().PostAsync(RetryUrl(original.Id), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = Assert.Single(_factory.Documents.All);
        Assert.Equal(ScanStatus.Clean, stored.Scan.Status);
        Assert.Equal(original.StoragePath, stored.StoragePath);
        Assert.Equal(original.Sha256, stored.Sha256);
        Assert.Equal(original.Links, stored.Links);
        Assert.Equal(Pdf, _factory.Storage.Inner.GetBytes(DocumentsController.Container, stored.StoragePath));
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.ScanRetry && e.Actor == "retry-user" && e.Allowed && e.SubjectId == "CLM-RETRY");
        var download = await Client().GetAsync($"/api/v1/documents/{original.Id}/content?{Subject}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Theory]
    [InlineData(ScanStatus.Failed)]
    [InlineData(ScanStatus.Pending)]
    [InlineData(ScanStatus.Infected)]
    public async Task Nonclean_retry_never_opens_the_file_even_with_unscanned_content_enabled(ScanStatus verdict)
    {
        var document = await FailedDocument();
        _factory.AllowUnscanned = true;
        _factory.Scanner.Scan = (_, _) => Task.FromResult(new DocumentScan { Status = verdict, Engine = "test" });
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsync(RetryUrl(document.Id), null)).StatusCode);
        var stored = Assert.Single(_factory.Documents.All);
        Assert.Equal(verdict == ScanStatus.Pending ? ScanStatus.Failed : verdict, stored.Scan.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Client().GetAsync($"/api/v1/documents/{document.Id}/content?{Subject}")).StatusCode);
    }

    [Theory]
    [InlineData(ScanStatus.Clean, false, false)]
    [InlineData(ScanStatus.Pending, false, false)]
    [InlineData(ScanStatus.Infected, false, false)]
    [InlineData(ScanStatus.Failed, true, false)]
    [InlineData(ScanStatus.Failed, false, true)]
    public async Task Ineligible_documents_are_refused_without_scanning(ScanStatus status, bool hold, bool pending)
    {
        var document = await FailedDocument();
        document.Scan.Status = status;
        document.Retention.LegalHold.Set = hold;
        document.PendingUploadPath = pending ? "staging/test" : null;
        _factory.Documents.Seed(document);
        var calls = 0;
        _factory.Scanner.Scan = (_, _) => { calls++; return Task.FromResult(new DocumentScan()); };
        Assert.Equal(HttpStatusCode.Conflict, (await Client().PostAsync(RetryUrl(document.Id), null)).StatusCode);
        Assert.Equal(0, calls);
        Assert.Contains(_factory.Audit.Entries, e => e.Action == DocumentAccessAction.ScanRetry && !e.Allowed);
    }

    [Fact]
    public async Task Tenant_subject_sensitivity_and_write_permissions_are_enforced()
    {
        var document = await FailedDocument();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync(RetryUrl(document.Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client("tenant-2").PostAsync(RetryUrl(document.Id), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsync(RetryUrl(document.Id).Replace("CLM-RETRY", "CLM-OTHER"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(role: ChoRolePermissions.FinanceApprover).PostAsync(RetryUrl(document.Id), null)).StatusCode);
        document.Sensitivity.Add("GENETIC");
        _factory.Documents.Seed(document);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync(RetryUrl(document.Id), null)).StatusCode);
        Assert.Equal(ScanStatus.Failed, Assert.Single(_factory.Documents.All).Scan.Status);
    }

    [Fact]
    public async Task Audit_failure_prevents_scanning_or_a_status_change()
    {
        var document = await FailedDocument();
        _factory.Audit.Fail = true;
        var calls = 0;
        _factory.Scanner.Scan = (_, _) => { calls++; return Task.FromResult(new DocumentScan { Status = ScanStatus.Clean }); };
        var response = await Client().PostAsync(RetryUrl(document.Id), null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal(ScanStatus.Failed, Assert.Single(_factory.Documents.All).Scan.Status);
    }

    [Fact]
    public async Task Scanner_io_failure_leaves_the_document_failed()
    {
        var document = await FailedDocument();
        _factory.Scanner.Scan = (_, _) => throw new IOException("scanner unavailable");
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsync(RetryUrl(document.Id), null)).StatusCode);
        Assert.Equal(ScanStatus.Failed, Assert.Single(_factory.Documents.All).Scan.Status);
    }

    [Fact]
    public async Task Concurrent_retries_keep_downloads_blocked_and_cannot_overwrite_the_winner()
    {
        var document = await FailedDocument();
        var enteredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        _factory.Scanner.Scan = async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                enteredFirst.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
                return new() { Status = ScanStatus.Infected, Engine = "first" };
            }
            enteredSecond.SetResult();
            await releaseSecond.Task.WaitAsync(ct);
            return new() { Status = ScanStatus.Clean, Engine = "second" };
        };
        var first = Client().PostAsync(RetryUrl(document.Id), null);
        await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Client().PostAsync(RetryUrl(document.Id), null);
        await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _factory.AllowUnscanned = true;
        Assert.Equal(HttpStatusCode.Conflict, (await Client().GetAsync($"/api/v1/documents/{document.Id}/content?{Subject}")).StatusCode);
        releaseFirst.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        releaseSecond.SetResult();
        Assert.Equal(HttpStatusCode.Conflict, (await second).StatusCode);
        Assert.Equal(ScanStatus.Infected, Assert.Single(_factory.Documents.All).Scan.Status);
    }
}
