using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// Coverage for <c>POST /api/v1/claims/import/raw837</c> — the evaluator
/// on-ramp for dropping a raw X12 837 file — and its sibling
/// <c>GET /api/v1/claims/import-transactions</c>, which was added
/// alongside a <see cref="ClaimImportTransaction"/> log so a rejected
/// or accepted import is visible after the fact, not just in the
/// synchronous response.
/// </summary>
public class ClaimsV1ControllerRaw837Tests : IClassFixture<ClaimsApiFactory>
{
    // One CLM segment, professional, POS 11, CPT 99213 — same shape as
    // scripts/smoke/834-to-837-e2e-smoke.sh's payload.
    private const string SingleClaimSample =
        "ISA*00*          *00*          *ZZ*SENDER         *ZZ*RECEIVER       *260101*0000*^*00501*000000001*0*P*:~GS*HC*SENDER*RECEIVER*20260101*0000*1*X*005010X222A1~ST*837*0001*005010X222A1~BHT*0019*18*CLM-RAW837-0001*20260101*0000*CH~NM1*41*2*SUBMITTER*****46*SENDER~PER*IC*SUBMITTER*TE*0000000000~NM1*40*2*RECEIVER*****46*RECEIVER~HL*1**20*1~NM1*85*2*MEDICAL GROUP*****XX*1234567890~N3*ADDRESS ON FILE~N4*CITY*CA*94102~HL*2*1*22*0~SBR*P*18*****CI~NM1*IL*1*SMITH*JOHN****MI*MEM-RAW837~NM1*PR*2*PAYER*****PI*PAYERID~CLM*CLM-RAW837-0001*150.00***11:B:1*Y*A*Y*Y~DTP*472*RD8*20260101-20260101~HI*ABK:J06.9~LX*1~SV1*HC:99213*150.00*UN*1*11**1~DTP*472*RD8*20260101-20260101~SE*17*0001~GE*1*1~IEA*1*000000001~";

    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimSubmissionService _service;
    private readonly IClaimImportTransactionRepository _transactions;

    public ClaimsV1ControllerRaw837Tests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _service = factory.SubmissionService;
        _transactions = factory.ImportTransactionRepository;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        _service.ClearSubstitute();
        _transactions.ClearSubstitute();
    }

    [Fact]
    public async Task ImportRaw837_ValidClaim_SubmitsAndPersistsAcceptedTransaction()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<AdapterClaim>();
                c.Id = "claim-id-raw837";
                return ClaimSubmissionResult.Ok(c);
            });

        var response = await _client.PostAsync(
            "/api/v1/claims/import/raw837", BuildFileContent(SingleClaimSample));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.SucceededCount);
        Assert.Equal("claim-id-raw837", result.Results[0].ClaimId);

        await _transactions.Received(1).CreateAsync(Arg.Is<ClaimImportTransaction>(t =>
            t.TenantId == "test-tenant"
            && t.ClaimNumber == "CLM-RAW837-0001"
            && t.ClaimId == "claim-id-raw837"
            && t.MemberId == "MEM-RAW837"
            && t.Status == "Accepted"
            && t.Errors.Count == 0));
    }

    [Fact]
    public async Task ImportRaw837_ValidationFailure_PersistsRejectedTransactionWithErrors()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimSubmissionResult.ValidationFailed(new[]
            {
                new ValidationError { Field = "MemberId", Code = "NotFound", Message = "Member not recognized" }
            }));

        var response = await _client.PostAsync(
            "/api/v1/claims/import/raw837", BuildFileContent(SingleClaimSample));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal(0, result!.SucceededCount);
        Assert.False(result.Results[0].Success);

        await _transactions.Received(1).CreateAsync(Arg.Is<ClaimImportTransaction>(t =>
            t.Status == "Rejected"
            && t.ClaimId == null
            && t.Errors.Any(e => e.Contains("Member not recognized"))));
    }

    [Fact]
    public async Task ImportRaw837_MultipleClaims_SubmitsConcurrentlyAndPreservesFileOrder()
    {
        var secondClaim = SingleClaimSample.Replace(
            "CLM-RAW837-0001",
            "CLM-RAW837-0002",
            StringComparison.Ordinal);
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var claim = call.Arg<AdapterClaim>();
                if (Interlocked.Increment(ref started) == 2)
                {
                    bothStarted.TrySetResult();
                }

                await release.Task;
                claim.Id = $"id-{claim.ClaimNumber}";
                return ClaimSubmissionResult.Ok(claim);
            });

        var responseTask = _client.PostAsync(
            "/api/v1/claims/import/raw837",
            BuildFileContent($"{SingleClaimSample}{secondClaim}", "batch.837"));

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();

        var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.NotNull(result);
        Assert.Equal(2, result!.SucceededCount);
        Assert.Collection(
            result.Results,
            first => Assert.Equal("CLM-RAW837-0001", first.ClaimNumber),
            second => Assert.Equal("CLM-RAW837-0002", second.ClaimNumber));
    }

    [Fact]
    public async Task ImportRaw837_NoFile_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            "/api/v1/claims/import/raw837", new MultipartFormDataContent());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _transactions.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task ImportRaw837_MalformedEdi_ReturnsBadRequest_AndDoesNotPersistAnything()
    {
        var response = await _client.PostAsync(
            "/api/v1/claims/import/raw837", BuildFileContent("NOT AN 837 FILE"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _transactions.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    [Fact]
    public async Task ListImportTransactions_NoTenantHeader_FallsBackToDefaultTenant()
    {
        // Claims-service's tenant middleware runs in lenient mode
        // (RequireTenantId=false) — a missing header resolves to
        // "default-tenant" rather than blocking the request, same as
        // SearchMemberClaims's existing TryGetTenantId() usage in this
        // controller. TryGetTenantId() only returns empty when the
        // middleware itself is configured strict, which this service isn't.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/claims/import-transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _transactions.Received(1).ListRecentAsync("default-tenant", 100);
    }

    [Fact]
    public async Task ListImportTransactions_ReturnsRepositoryResult()
    {
        _transactions.ListRecentAsync("test-tenant", 100).Returns(new List<ClaimImportTransaction>
        {
            new() { TenantId = "test-tenant", ClaimNumber = "CLM-1", Status = "Accepted" }
        });

        var response = await _client.GetAsync("/api/v1/claims/import-transactions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ClaimImportTransaction>>();
        Assert.NotNull(body);
        Assert.Single(body!);
        Assert.Equal("CLM-1", body![0].ClaimNumber);
    }

    private static MultipartFormDataContent BuildFileContent(string ediContent, string fileName = "test.837")
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(ediContent));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", fileName);
        return content;
    }
}
