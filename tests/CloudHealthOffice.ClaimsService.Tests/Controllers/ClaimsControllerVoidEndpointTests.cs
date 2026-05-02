using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Services;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Capability 5.12b — covers <c>POST /api/claims/{id}/void</c>, the new
/// HTTP surface that exposes <see cref="IClaimFinalizationService.VoidAsync"/>
/// (5.12a wired the service; 5.12b ships the controller endpoint per
/// Plan-First Decision 1 / Premise correction A).
/// </summary>
public class ClaimsControllerVoidEndpointTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly IClaimFinalizationService _finalization;

    public ClaimsControllerVoidEndpointTests(ClaimsApiFactory factory)
    {
        _finalization = factory.FinalizationService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _finalization.ClearReceivedCalls();
    }

    private static Claim VoidedClaim(string id = "c1") => new()
    {
        Id = id,
        TenantId = "test-tenant",
        ClaimNumber = "CLM-001",
        Status = ClaimStatus.Voided,
        VersionState = ClaimVersionState.Voided,
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "m1",
        TotalChargeAmount = 1000m,
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        AdjudicationResult = new AdjudicationResult { CheckNumber = "CHK-001", PayerPayment = 800m },
    };

    private static object Body(string reason = "operator reverse", string? reversalRunId = "rr-1") =>
        new { reason, reversalRunId };

    [Fact]
    public async Task VoidClaim_Voided_Returns200_AndForwardsReversalRunId()
    {
        _finalization.VoidAsync(
            "c1", Arg.Any<ClaimVoidRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimVoidResult.Voided(VoidedClaim()));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/void", Body(), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _finalization.Received(1).VoidAsync(
            "c1",
            Arg.Is<ClaimVoidRequest>(r => r.Reason == "operator reverse" && r.ReversalRunId == "rr-1"),
            "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VoidClaim_AlreadyVoided_Returns200_Idempotent()
    {
        _finalization.VoidAsync(
            "c1", Arg.Any<ClaimVoidRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimVoidResult.AlreadyVoided(VoidedClaim()));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/void", Body(), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VoidClaim_NotFound_Returns404()
    {
        _finalization.VoidAsync(
            "missing", Arg.Any<ClaimVoidRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimVoidResult.NotFound("Claim missing not found"));

        var response = await _client.PostAsJsonAsync("/api/claims/missing/void", Body(), Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VoidClaim_InvalidSourceState_Returns422()
    {
        var claim = VoidedClaim();
        claim.Status = ClaimStatus.Submitted;
        claim.VersionState = ClaimVersionState.Submitted;
        _finalization.VoidAsync(
            "c1", Arg.Any<ClaimVoidRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimVoidResult.InvalidSourceState(claim, "current status is Submitted"));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/void", Body(), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task VoidClaim_MissingReason_Returns400_BeforeServiceCall()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/claims/c1/void", new { reason = "", reversalRunId = "rr-1" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _finalization.DidNotReceiveWithAnyArgs().VoidAsync(
            default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task VoidClaim_NoBody_Returns400()
    {
        var response = await _client.PostAsync("/api/claims/c1/void", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
