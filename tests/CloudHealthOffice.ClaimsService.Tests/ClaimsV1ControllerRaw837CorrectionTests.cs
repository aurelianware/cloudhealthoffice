using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// Inbound 837 replacements (CLM05-3 = 7) and voids (8) amend the claim named
/// in 2300 REF*F8, through the adjustment and void workflows. They are never
/// submitted as new claims, which would pay a corrected claim twice or pay a
/// voided one.
/// </summary>
public class ClaimsV1ControllerRaw837CorrectionTests : IClassFixture<ClaimsApiFactory>
{
    private const string Tenant = "test-tenant";
    private const string BillingNpi = "1234567893";

    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;

    public ClaimsV1ControllerRaw837CorrectionTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        factory.SubmissionService.ClearSubstitute();
        factory.AdjustmentService.ClearSubstitute();
        factory.FinalizationService.ClearSubstitute();
        factory.ImportTransactionRepository.ClearSubstitute();
    }

    private static string Correction(string frequency, string? originalReference)
    {
        var body = EDI.Snip.Snip837Samples.ProfessionalBody("CLM-CORR-0001");
        EDI.Snip.Snip837Samples.Replace(body, "NM1*IL", "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM-RAW837");
        EDI.Snip.Snip837Samples.Replace(body, "CLM*", $"CLM*CLM-CORR-0001*150.00***11:B:{frequency}*Y*A*Y*Y");
        if (originalReference is not null)
            body.Insert(body.FindIndex(s => s.StartsWith("CLM*", StringComparison.Ordinal)) + 1, $"REF*F8*{originalReference}");
        return EDI.Snip.Snip837Samples.Wrap(EDI.Snip.Snip837Samples.ProfessionalVersion, body);
    }

    private void OriginalExists(string id, string npi = BillingNpi, ClaimStatus status = ClaimStatus.Paid)
    {
        var original = new Claim
        {
            Id = id, ClaimVersionId = id, VersionNumber = 1, TenantId = Tenant,
            ClaimNumber = "CLM-CORR-0001", MemberId = "MEM-RAW837", BillingProviderNPI = npi, Status = status,
        };
        _factory.ClaimRepository.GetByIdAsync(id).Returns(original);
        _factory.ClaimRepository.GetLatestVersionAsync(id, Arg.Any<DateTime>()).Returns(original);
    }

    private async Task<Raw837ImportResult> ImportAsync(string edi)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(edi));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "corrections.837");
        var response = await _client.PostAsync("/api/v1/claims/import/raw837", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Raw837ImportResult>())!;
    }

    private async Task NothingSubmittedAsNew() =>
        await _factory.SubmissionService.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default!, default, default, default);

    [Fact]
    public async Task AReplacement_BecomesAnAdjustmentOfTheOriginal()
    {
        OriginalExists("orig-1");
        _factory.AdjustmentService
            .CreateAdjustmentAsync("orig-1", Arg.Any<ClaimAdjustmentRequest>(), Arg.Any<string>(), Tenant,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimAdjustmentResult.Created(
                new ClaimAdjustment { NewClaimId = "orig-1-v2" },
                new AdapterClaim { Id = "orig-1-v2" }));

        var result = await ImportAsync(Correction("7", "orig-1"));

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal("orig-1-v2", result.Results[0].ClaimId);
        await _factory.AdjustmentService.Received(1).CreateAdjustmentAsync("orig-1",
            Arg.Is<ClaimAdjustmentRequest>(r => r.CorrectedClaim.OriginalClaimReference == "orig-1"
                                                && r.CorrectedClaim.ClaimFrequencyCode == "7"
                                                && r.AdjustmentReason.Contains("replacement")),
            Arg.Is<string>(k => k.StartsWith("837-correction-")), Tenant,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await NothingSubmittedAsNew();
    }

    [Fact]
    public async Task AVoid_VoidsTheOriginal()
    {
        OriginalExists("orig-2");
        _factory.FinalizationService
            .VoidAsync("orig-2", Arg.Any<ClaimVoidRequest>(), Tenant, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimVoidResult.Voided(new Claim { Id = "orig-2" }));

        var result = await ImportAsync(Correction("8", "orig-2"));

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal("orig-2", result.Results[0].ClaimId);
        await NothingSubmittedAsNew();
        await _factory.AdjustmentService.DidNotReceiveWithAnyArgs().CreateAdjustmentAsync(default!, default!, default!, default!, default!, default, default);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("8")]
    public async Task ACorrectionWithoutRefF8_IsRejected_AndNothingIsProcessed(string frequency)
    {
        var result = await ImportAsync(Correction(frequency, null));

        Assert.Equal(0, result.SucceededCount);
        Assert.False(result.Results[0].Success);
        await NothingSubmittedAsNew();
        await _factory.AdjustmentService.DidNotReceiveWithAnyArgs().CreateAdjustmentAsync(default!, default!, default!, default!, default!, default, default);
        await _factory.FinalizationService.DidNotReceiveWithAnyArgs().VoidAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task ACorrectionNamingAnUnknownClaim_IsRejected()
    {
        _factory.ClaimRepository.GetByIdAsync("no-such-claim").Returns((Claim?)null);

        var result = await ImportAsync(Correction("7", "no-such-claim"));

        Assert.False(result.Results[0].Success);
        Assert.Contains(result.Results[0].Errors, e => e.Contains("names no claim"));
        await NothingSubmittedAsNew();
    }

    [Fact]
    public async Task ACorrectionOfAnotherProvidersClaim_IsRejected()
    {
        OriginalExists("orig-other", npi: "1111111112");

        var result = await ImportAsync(Correction("8", "orig-other"));

        Assert.False(result.Results[0].Success);
        Assert.Contains(result.Results[0].Errors, e => e.Contains("different provider"));
        await _factory.FinalizationService.DidNotReceiveWithAnyArgs().VoidAsync(default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task AReplacementTheAdjustmentRulesRefuse_IsRejectedWithTheReason()
    {
        // e.g. the original is still in adjudication.
        OriginalExists("orig-3", status: ClaimStatus.Pended);
        _factory.AdjustmentService
            .CreateAdjustmentAsync("orig-3", Arg.Any<ClaimAdjustmentRequest>(), Arg.Any<string>(), Tenant,
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimAdjustmentResult.InvalidSourceState(new Claim { Id = "orig-3" }, "Predecessor must be Paid, Denied, or PartiallyPaid"));

        var result = await ImportAsync(Correction("7", "orig-3"));

        Assert.False(result.Results[0].Success);
        Assert.Contains(result.Results[0].Errors, e => e.Contains("Paid, Denied, or PartiallyPaid"));
        await NothingSubmittedAsNew();
    }
}
