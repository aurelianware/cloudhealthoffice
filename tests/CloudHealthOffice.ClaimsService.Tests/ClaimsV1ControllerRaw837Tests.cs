using CloudHealthOffice.Infrastructure.Security;
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
    // One SNIP-clean 837P claim (two lines, POS 11); see EDI/Snip/Snip837Samples.
    private static readonly string SingleClaimSample = SnipCleanClaim("CLM-RAW837-0001");

    private static string SnipCleanClaim(string claimId, Action<List<string>>? mutate = null)
    {
        var body = EDI.Snip.Snip837Samples.ProfessionalBody(claimId);
        EDI.Snip.Snip837Samples.Replace(body, "NM1*IL", "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM-RAW837");
        mutate?.Invoke(body);
        return EDI.Snip.Snip837Samples.Wrap(EDI.Snip.Snip837Samples.ProfessionalVersion, body);
    }

    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimSubmissionService _service;
    private readonly IClaimImportTransactionRepository _transactions;

    public ClaimsV1ControllerRaw837Tests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _service = factory.SubmissionService;
        _transactions = factory.ImportTransactionRepository;
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
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
    public async Task ImportRaw837_Clean_ReturnsAccepted999()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimSubmissionResult.Ok(ci.Arg<AdapterClaim>()));

        var response = await _client.PostAsync("/api/v1/claims/import/raw837", BuildFileContent(SingleClaimSample));

        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("A", result!.AcknowledgmentCode);
        Assert.Contains("AK9*A*1*1*1~", result.Acknowledgment999);
        Assert.Empty(result.SnipIssues);
    }

    [Fact]
    public async Task ImportRaw837_SnipRejectedSet_IsNotSubmittedAndIsLoggedWithSnipErrors()
    {
        var unbalanced = SnipCleanClaim("CLM-RAW837-0009",
            b => EDI.Snip.Snip837Samples.Replace(b, "CLM*", "CLM*CLM-RAW837-0009*999.00***11:B:1*Y*A*Y*Y"));

        var response = await _client.PostAsync("/api/v1/claims/import/raw837", BuildFileContent(unbalanced));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("R", result!.AcknowledgmentCode);
        Assert.Contains("IK3*CLM*16*2300*8~", result.Acknowledgment999);
        var issue = Assert.Single(result.SnipIssues);
        Assert.Equal(("L3-CLM-BALANCE", "CLM-RAW837-0009"), (issue.RuleId, issue.ClaimId));
        Assert.False(result.Results.Single().Success);
        Assert.Contains(result.Results.Single().Errors, e => e.Contains("L3-CLM-BALANCE"));

        await _service.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);
        await _transactions.Received(1).CreateAsync(Arg.Is<ClaimImportTransaction>(t =>
            t.ClaimNumber == "CLM-RAW837-0009" && t.Status == "Rejected"
            && t.Errors.Any(e => e.Contains("SNIP 3"))
            && t.TransactionSetControlNumber == "0001"
            && t.AcknowledgmentCode == "R"
            && t.Acknowledgment999ControlNumber != null
            && result.Acknowledgment999!.Contains("*" + t.Acknowledgment999ControlNumber + "*0*")));
    }

    [Fact]
    public async Task ImportRaw837_FileCutOffBeforeSe_IsRejectedNotA500()
    {
        var truncated = SingleClaimSample[..SingleClaimSample.IndexOf("SE*", StringComparison.Ordinal)];

        var response = await _client.PostAsync("/api/v1/claims/import/raw837", BuildFileContent(truncated));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("R", result!.AcknowledgmentCode);
        Assert.Contains(result.SnipIssues, i => i.RuleId == "L1-SE-MISSING");
        Assert.All(result.Results, r => Assert.False(r.Success));
        await _service.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task ImportRaw837_OneOfTwoSetsRejected_SubmitsOnlyTheAcceptedSet()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimSubmissionResult.Ok(ci.Arg<AdapterClaim>()));

        var good = EDI.Snip.Snip837Samples.ProfessionalBody("CLM-GOOD");
        var bad = EDI.Snip.Snip837Samples.ProfessionalBody("CLM-BAD");
        EDI.Snip.Snip837Samples.Replace(bad, "NM1*85", "NM1*85*2*ACME MEDICAL GROUP*****XX*1234567890");
        var edi = EDI.Snip.Snip837Samples.Wrap(EDI.Snip.Snip837Samples.ProfessionalVersion, good, bad);

        var response = await _client.PostAsync("/api/v1/claims/import/raw837", BuildFileContent(edi));

        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("P", result!.AcknowledgmentCode);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(["CLM-GOOD", "CLM-BAD"], result.Results.Select(r => r.ClaimNumber));
        await _service.Received(1).SubmitAsync(Arg.Is<AdapterClaim>(c => c.ClaimNumber == "CLM-GOOD"),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateRaw837_ReturnsIssuesAnd999WithoutSubmitting()
    {
        var response = await _client.PostAsync("/api/v1/claims/import/raw837/validate",
            BuildFileContent(SnipCleanClaim("CLM-V", b => EDI.Snip.Snip837Samples.RemoveSegment(b, "DTP*472"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<Raw837ImportResult>();
        Assert.Equal("R", result!.AcknowledgmentCode);
        Assert.Contains(result.SnipIssues, i => i.RuleId == "L4-DTP472" && i.Level == global::ClaimsService.EDI.Validation.SnipLevel.Situational);
        Assert.StartsWith("ISA*", result.Acknowledgment999);
        await _service.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);
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
    public async Task ListImportTransactions_NoTenantOrToken_Returns401_NoDefaultTenant()
    {
        // The local tenant middleware used to resolve a missing header to
        // "default-tenant" and list that tenant's imports. The tenant now
        // comes only from the token, and there is no default.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/claims/import-transactions");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await _transactions.DidNotReceiveWithAnyArgs().ListRecentAsync(default!, default);
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

    [Fact]
    public async Task ListSnipWarningTransactions_PassesFiltersToRepository()
    {
        _transactions.ListWithSnipWarningsAsync("test-tenant", "L2-2300-DTP472", 2, "SUB001", 50)
            .Returns(new List<ClaimImportTransaction>
            {
                new() { TenantId = "test-tenant", ClaimNumber = "CLM-W", SubmitterId = "SUB001" }
            });

        var response = await _client.GetAsync(
            "/api/v1/claims/import-transactions/snip-warnings?ruleId=L2-2300-DTP472&level=2&submitterId=SUB001&limit=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<ClaimImportTransaction>>();
        Assert.Equal("SUB001", Assert.Single(body!).SubmitterId);
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
