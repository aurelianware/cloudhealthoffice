using CloudHealthOffice.Infrastructure.Security;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Services;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

public class ClaimsControllerFinalizationTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly IClaimFinalizationService _finalization;
    private readonly ClaimsApiFactory _factory;

    public ClaimsControllerFinalizationTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _finalization = factory.FinalizationService;
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _finalization.ClearReceivedCalls();
    }

    private static Claim PaidClaim(string id = "c1") => new()
    {
        Id = id,
        TenantId = "test-tenant",
        ClaimNumber = "CLM-001",
        Status = ClaimStatus.Paid,
        VersionState = ClaimVersionState.Paid,
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "m1",
        TotalChargeAmount = 1000m,
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        AdjudicationResult = new AdjudicationResult { CheckNumber = "CHK-001", PayerPayment = 800m }
    };

    private static object Body(decimal amount = 800m) => new
    {
        controlNumber = "PR-1",
        checkNumber = "CHK-001",
        paymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
        paymentAmount = amount,
        paymentRunId = "run-1",
        eraEnvelopeId = "env-1"
    };

    [Fact]
    public async Task ProcessRemittance_HappyPath_Returns200()
    {
        _finalization.FinalizeAsync(
            "c1", Arg.Any<ClaimFinalizationRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimFinalizationResult.Finalized(PaidClaim()));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _finalization.Received(1).FinalizeAsync(
            "c1",
            Arg.Is<ClaimFinalizationRequest>(r => r.CheckNumber == "CHK-001" && r.PaymentRunId == "run-1"),
            "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessRemittance_AlreadyFinalized_Returns200()
    {
        _finalization.FinalizeAsync(
            "c1", Arg.Any<ClaimFinalizationRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimFinalizationResult.AlreadyFinalized(PaidClaim()));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ProcessRemittance_NotFound_Returns404()
    {
        _finalization.FinalizeAsync(
            "missing", Arg.Any<ClaimFinalizationRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimFinalizationResult.NotFound("Claim missing not found"));

        var response = await _client.PostAsJsonAsync("/api/claims/missing/remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProcessRemittance_Conflict_Returns409()
    {
        _finalization.FinalizeAsync(
            "c1", Arg.Any<ClaimFinalizationRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimFinalizationResult.Conflict(PaidClaim(), "already paid under check CHK-EXISTING"));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ProcessRemittance_InvalidSourceState_Returns422()
    {
        var claim = PaidClaim();
        claim.Status = ClaimStatus.Submitted;
        _finalization.FinalizeAsync(
            "c1", Arg.Any<ClaimFinalizationRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimFinalizationResult.InvalidSourceState(claim, "current status is Submitted"));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    private void FinalizeReturns(string id, ClaimFinalizationResult result) =>
        _finalization.FinalizeAsync(
            id, Arg.Any<ClaimFinalizationRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private static Claim ClaimIn(string id, ClaimStatus status, ClaimVersionState state)
    {
        var claim = PaidClaim(id);
        claim.Status = status;
        claim.VersionState = state;
        claim.AdjudicationResult = new AdjudicationResult { PayerPayment = 0m };
        return claim;
    }

    /// <summary>
    /// An Approved claim whose allowed amount went entirely to the member's
    /// deductible pays $0. It is payable, and finalizes like a paid claim:
    /// through the finalization service, with the check number, PayerPayment 0.
    /// </summary>
    [Fact]
    public async Task ProcessRemittance_ZeroPayment_FinalizesThroughFinalizationService()
    {
        var paid = ClaimIn("zp-approved", ClaimStatus.Paid, ClaimVersionState.Paid);
        FinalizeReturns("zp-approved", ClaimFinalizationResult.Finalized(paid));
        _factory.ClaimRepository.ClearReceivedCalls();

        var response = await _client.PostAsJsonAsync("/api/claims/zp-approved/remittance", Body(amount: 0m), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Claim>(Json);
        Assert.Equal(ClaimStatus.Paid, body!.Status);
        await _finalization.Received(1).FinalizeAsync(
            "zp-approved",
            Arg.Is<ClaimFinalizationRequest>(r =>
                r.CheckNumber == "CHK-001" && r.PayerPayment == 0m
                && r.PaymentRunId == "run-1" && r.EdiControlNumber == "PR-1"),
            "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // No legacy direct write (the old path replaced the claim as Denied).
        await _factory.ClaimRepository.DidNotReceive().UpdateAsync(Arg.Is<Claim>(c => c.Id == "zp-approved"));
    }

    /// <summary>A $0 remittance with no check / EFT trace is keyed on its control number.</summary>
    [Fact]
    public async Task ProcessRemittance_ZeroPayment_NoCheckNumber_UsesControlNumber()
    {
        FinalizeReturns("zp-nocheck", ClaimFinalizationResult.Finalized(ClaimIn("zp-nocheck", ClaimStatus.Paid, ClaimVersionState.Paid)));

        var response = await _client.PostAsJsonAsync("/api/claims/zp-nocheck/remittance", new
        {
            controlNumber = "PR-77",
            paymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            paymentAmount = 0m,
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _finalization.Received(1).FinalizeAsync(
            "zp-nocheck",
            Arg.Is<ClaimFinalizationRequest>(r => r.CheckNumber == "PR-77" && r.PayerPayment == 0m),
            "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A repeat of the same $0 remittance is the same idempotent no-op as a paid one.</summary>
    [Fact]
    public async Task ProcessRemittance_ZeroPayment_AlreadyFinalized_Returns200()
    {
        FinalizeReturns("zp-repeat", ClaimFinalizationResult.AlreadyFinalized(ClaimIn("zp-repeat", ClaimStatus.Paid, ClaimVersionState.Paid)));

        var response = await _client.PostAsJsonAsync("/api/claims/zp-repeat/remittance", Body(amount: 0m), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A Pended claim is not payable: a $0 remittance must not deny it (422, unchanged).</summary>
    [Fact]
    public async Task ProcessRemittance_ZeroPayment_PendedClaim_Returns422_AndDoesNotWrite()
    {
        var pended = ClaimIn("zp-pended", ClaimStatus.Pended, ClaimVersionState.Submitted);
        FinalizeReturns("zp-pended", ClaimFinalizationResult.InvalidSourceState(pended, "current status is Pended"));
        _factory.ClaimRepository.ClearReceivedCalls();

        var response = await _client.PostAsJsonAsync("/api/claims/zp-pended/remittance", Body(amount: 0m), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Pended", await response.Content.ReadAsStringAsync());
        Assert.Equal(ClaimStatus.Pended, pended.Status);
        await _factory.ClaimRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    /// <summary>A claim the pipeline denied stays Denied: the $0 remittance is its 835, acknowledged unchanged.</summary>
    [Fact]
    public async Task ProcessRemittance_ZeroPayment_DeniedClaim_StaysDenied()
    {
        var denied = ClaimIn("zp-denied", ClaimStatus.Denied, ClaimVersionState.Denied);
        FinalizeReturns("zp-denied", ClaimFinalizationResult.InvalidSourceState(denied, "current status is Denied"));
        _factory.ClaimRepository.ClearReceivedCalls();

        var response = await _client.PostAsJsonAsync("/api/claims/zp-denied/remittance", Body(amount: 0m), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Claim>(Json);
        Assert.Equal(ClaimStatus.Denied, body!.Status);
        await _factory.ClaimRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!);
    }

    /// <summary>A paid remittance for a Denied claim is still refused (only the $0 one is acknowledged).</summary>
    [Fact]
    public async Task ProcessRemittance_PaidAmount_DeniedClaim_Returns422()
    {
        FinalizeReturns("p-denied", ClaimFinalizationResult.InvalidSourceState(
            ClaimIn("p-denied", ClaimStatus.Denied, ClaimVersionState.Denied), "current status is Denied"));

        var response = await _client.PostAsJsonAsync("/api/claims/p-denied/remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ProcessRemittance_NegativeAmount_Returns400_WithoutFinalizing()
    {
        var response = await _client.PostAsJsonAsync("/api/claims/neg/remittance", Body(amount: -5m), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _finalization.DidNotReceive().FinalizeAsync(
            "neg", Arg.Any<ClaimFinalizationRequest>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessRemittance_PaidAmount_NoCheckNumber_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/claims/nocheck/remittance", new
        {
            controlNumber = "PR-1",
            paymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
            paymentAmount = 10m,
        }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _finalization.DidNotReceive().FinalizeAsync(
            "nocheck", Arg.Any<ClaimFinalizationRequest>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
