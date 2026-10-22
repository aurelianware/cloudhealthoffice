using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Edi.Files;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// Every raw 837 (upload or SFTP) gets an EDI file record
/// (edi-operations.md §3–4), readable at api/v1/claims/edi/files.
/// </summary>
public class ClaimsRaw837EdiFileTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;

    public ClaimsRaw837EdiFileTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        factory.SubmissionService.ClearSubstitute();
        factory.ImportTransactionRepository.ClearSubstitute();
        factory.SubmissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<AdapterClaim>();
                c.Id = "claim-" + c.ClaimNumber;
                return ClaimSubmissionResult.Ok(c);
            });
    }

    private static string Claim(string claimId)
    {
        var body = EDI.Snip.Snip837Samples.ProfessionalBody(claimId);
        EDI.Snip.Snip837Samples.Replace(body, "NM1*IL", "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM-EDIFILE");
        return EDI.Snip.Snip837Samples.Wrap(EDI.Snip.Snip837Samples.ProfessionalVersion, body);
    }

    private static MultipartFormDataContent Upload(string edi, string? source = null, string? path = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(edi));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "in.837");
        if (source is not null) content.Add(new StringContent(source), "source");
        if (path is not null) content.Add(new StringContent(path), "sourcePath");
        return content;
    }

    private Task<EdiFile?> StoredAsync(string id) =>
        _factory.Services.GetRequiredService<InMemoryEdiFileStore>().GetAsync("test-tenant", id);

    [Fact]
    public async Task AcceptedFile_IsRecordedAndLinkedToItsClaims()
    {
        var response = await _client.PostAsync("/api/v1/claims/import/raw837",
            Upload(Claim("CLM-EDIFILE-1"), source: "Sftp", path: "/inbound/claims/in.837"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<Raw837ImportResult>())!;
        Assert.NotNull(result.EdiFileId);
        var file = (await StoredAsync(result.EdiFileId!))!;
        Assert.Equal("837", file.TransactionType);
        Assert.Equal(EdiFileSource.Sftp, file.Source);
        Assert.Equal("/inbound/claims/in.837", file.SourcePath);
        Assert.Equal(EdiFileStatus.Applied, file.Status);
        Assert.Equal(1, file.AcceptedRecords);
        Assert.Equal(0, file.RejectedRecords);
        Assert.Equal(result.AcknowledgmentCode, file.Ack999Code);
        Assert.Equal(result.Acknowledgment999ControlNumber, file.Ack999ControlNumber);
        Assert.NotEmpty(file.InterchangeControlNumbers);
        Assert.NotEmpty(file.TransactionSetControlNumbers);

        await _factory.ImportTransactionRepository.Received(1).CreateAsync(
            Arg.Is<ClaimImportTransaction>(t => t.ClaimNumber == "CLM-EDIFILE-1" && t.EdiFileId == file.Id));

        var listed = await _client.GetFromJsonAsync<List<EdiFile>>($"/api/v1/claims/edi/files?controlNumber={file.InterchangeControlNumbers[0]}&type=837");
        Assert.Contains(listed!, f => f.Id == file.Id);
        var one = await _client.GetFromJsonAsync<EdiFile>($"/api/v1/claims/edi/files/{file.Id}");
        Assert.Equal(file.Id, one!.Id);
    }

    [Fact]
    public async Task RejectedEnvelope_IsRecordedAsRejected()
    {
        var edi = Claim("CLM-EDIFILE-2");
        var broken = edi[..edi.LastIndexOf("IEA*", StringComparison.Ordinal)] + "IEA*1*999999999~";

        var response = await _client.PostAsync("/api/v1/claims/import/raw837", Upload(broken));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<Raw837ImportResult>())!;
        var file = (await StoredAsync(result.EdiFileId!))!;
        Assert.Equal(EdiFileStatus.Rejected, file.Status);
        Assert.Equal("R", file.Ta1AckCode);
        Assert.Equal(EdiFileSource.Upload, file.Source);
        Assert.NotEmpty(file.Errors);
    }

    [Fact]
    public async Task AnotherTenant_CannotReadTheFile()
    {
        var response = await _client.PostAsync("/api/v1/claims/import/raw837", Upload(Claim("CLM-EDIFILE-3")));
        var id = (await response.Content.ReadFromJsonAsync<Raw837ImportResult>())!.EdiFileId;

        var other = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        other.DefaultRequestHeaders.Add("X-Tenant-ID", "other-tenant");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/claims/edi/files/{id}")).StatusCode);
        Assert.DoesNotContain(await other.GetFromJsonAsync<List<EdiFile>>("/api/v1/claims/edi/files") ?? [], f => f.Id == id);
    }
}
