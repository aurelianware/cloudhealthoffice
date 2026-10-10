using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI.Ta1;

/// <summary>Claims host with the duplicate-ISA13 check on (the shared factory turns it off).</summary>
public sealed class Ta1ClaimsApiFactory : ClaimsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["X12Interchange:DuplicateWindowDays"] = "365",
            }));
    }
}

/// <summary>
/// TA1 on the raw 837 intake: envelope rejections answer with a TA1 and no
/// 999, ISA14 = 1 asks for a TA1 on an accepted envelope, duplicates are
/// refused, and stored TA1s can be fetched again.
/// </summary>
public class Raw837Ta1Tests : IClassFixture<Ta1ClaimsApiFactory>
{
    private readonly HttpClient _client;
    private readonly IClaimSubmissionService _service;

    public Raw837Ta1Tests(Ta1ClaimsApiFactory factory)
    {
        _service = factory.SubmissionService;
        _service.ClearSubstitute();
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimSubmissionResult.Ok(ci.Arg<AdapterClaim>()));
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    /// <summary>A SNIP-clean 837P with its own ISA13 (so tests do not collide) and the given ISA14.</summary>
    private static string Sample(string isa13, char isa14 = '0', string? iea02 = null)
    {
        var body = Snip.Snip837Samples.ProfessionalBody("CLM-TA1-" + isa13[^4..]);
        var edi = Snip.Snip837Samples.Wrap(Snip.Snip837Samples.ProfessionalVersion, body);
        return edi.Replace("*000000101*0*T*:", $"*{isa13}*{isa14}*T*:", StringComparison.Ordinal)
                  .Replace("IEA*1*000000101", $"IEA*1*{iea02 ?? isa13}", StringComparison.Ordinal);
    }

    private static string NextControl() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    [Fact]
    public async Task AckRequested_ValidEnvelope_ReturnsAcceptedTa1And999_AndStoresTheTa1()
    {
        var control = NextControl();
        var response = await _client.PostAsync("/api/v1/claims/import/raw837", File(Sample(control, '1')));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("A", result!.InterchangeAcknowledgmentCode);
        Assert.Contains($"TA1*{control}*260115*1200*A*000~", result.AcknowledgmentTa1);
        Assert.NotNull(result.Acknowledgment999);
        var id = Assert.Single(result.Ta1AcknowledgmentIds);

        var stored = await _client.GetFromJsonAsync<InterchangeAcknowledgmentRecord>($"/api/v1/claims/interchange/ta1/{id}");
        Assert.Equal(control, stored!.AcknowledgedControlNumber);
        Assert.Equal("837", stored.TransactionType);
        Assert.Equal(Ta1Direction.Generated, stored.Direction);

        var edi = await _client.GetStringAsync($"/api/v1/claims/interchange/ta1/{id}/edi");
        Assert.Equal(result.AcknowledgmentTa1, edi);

        var listed = await _client.GetFromJsonAsync<List<InterchangeAcknowledgmentRecord>>($"/api/v1/claims/interchange/ta1?controlNumber={control}");
        Assert.Single(listed!);
    }

    [Fact]
    public async Task NoAckRequested_ValidEnvelope_HasNoTa1()
    {
        var response = await _client.PostAsync("/api/v1/claims/import/raw837", File(Sample(NextControl())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("A", result!.InterchangeAcknowledgmentCode);
        Assert.Null(result.AcknowledgmentTa1);
        Assert.Empty(result.Ta1AcknowledgmentIds);
        Assert.NotNull(result.Acknowledgment999);
        Assert.Equal(1, result.SucceededCount);
    }

    [Fact]
    public async Task TrailerControlNumberMismatch_RejectsWithTa1_AndNo999()
    {
        var control = NextControl();
        var response = await _client.PostAsync("/api/v1/claims/import/raw837", File(Sample(control, '0', iea02: "000000999")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("R", result!.InterchangeAcknowledgmentCode);
        Assert.Contains($"TA1*{control}*260115*1200*R*001~", result.AcknowledgmentTa1);
        Assert.Null(result.Acknowledgment999);
        Assert.Empty(result.SnipIssues);
        Assert.Single(result.Ta1AcknowledgmentIds);
        await _service.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task SameInterchangeTwice_SecondIsRejectedAsDuplicate()
    {
        var edi = Sample(NextControl());

        var first = await _client.PostAsync("/api/v1/claims/import/raw837", File(edi));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        _service.ClearReceivedCalls();
        var second = await _client.PostAsync("/api/v1/claims/import/raw837", File(edi));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var result = await second.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal(["025"], result!.InterchangeNoteCodes);
        Assert.Contains("*R*025~", result.AcknowledgmentTa1);
        Assert.Null(result.Acknowledgment999);
        await _service.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task Validate_DoesNotClaimTheControlNumber()
    {
        var edi = Sample(NextControl(), '1');

        var validated = await _client.PostAsync("/api/v1/claims/import/raw837/validate", File(edi));
        var preview = await validated.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("A", preview!.InterchangeAcknowledgmentCode);
        Assert.Empty(preview.Ta1AcknowledgmentIds);

        var imported = await _client.PostAsync("/api/v1/claims/import/raw837", File(edi));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
    }

    private static MultipartFormDataContent File(string edi)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(edi));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "ta1.837");
        return content;
    }
}
