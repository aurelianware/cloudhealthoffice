using CloudHealthOffice.Infrastructure.Security;
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
        _client = factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
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

    // ── DELETE {id} and PUT {id}/status (Voided) void through VoidAsync ──

    private void VoidReturns(string id, ClaimVoidResult result) =>
        _finalization.VoidAsync(
            id, Arg.Any<ClaimVoidRequest>(), "test-tenant",
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    [Fact]
    public async Task Delete_VoidsThroughVoidAsync_Returns204()
    {
        VoidReturns("del-1", ClaimVoidResult.Voided(VoidedClaim("del-1")));

        var response = await _client.DeleteAsync("/api/claims/del-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await _finalization.Received(1).VoidAsync(
            "del-1", Arg.Is<ClaimVoidRequest>(r => r.Reason.Contains("DELETE") && r.ReversalRunId == null),
            "test-tenant", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_AlreadyVoided_Returns204_Idempotent()
    {
        VoidReturns("del-2", ClaimVoidResult.AlreadyVoided(VoidedClaim("del-2")));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/claims/del-2")).StatusCode);
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        VoidReturns("del-missing", ClaimVoidResult.NotFound("Claim del-missing not found"));

        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("/api/claims/del-missing")).StatusCode);
    }

    /// <summary>A claim VoidAsync cannot void (here Approved, not yet paid) is refused unchanged.</summary>
    [Fact]
    public async Task Delete_InvalidSourceState_Returns422()
    {
        var claim = VoidedClaim("del-approved");
        claim.Status = ClaimStatus.Approved;
        claim.VersionState = ClaimVersionState.Adjudicated;
        VoidReturns("del-approved", ClaimVoidResult.InvalidSourceState(claim, "current Status=Approved"));

        var response = await _client.DeleteAsync("/api/claims/del-approved");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Approved", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutStatusVoided_VoidsThroughVoidAsync_WithTheNotesAsReason()
    {
        VoidReturns("put-1", ClaimVoidResult.Voided(VoidedClaim("put-1")));

        var response = await _client.PutAsJsonAsync(
            "/api/claims/put-1/status", new { status = (int)ClaimStatus.Voided, notes = "duplicate submission" }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _finalization.Received(1).VoidAsync(
            "put-1", Arg.Is<ClaimVoidRequest>(r => r.Reason == "duplicate submission"),
            "test-tenant", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutStatusVoided_InvalidSourceState_Returns422()
    {
        var claim = VoidedClaim("put-pended");
        claim.Status = ClaimStatus.Pended;
        claim.VersionState = ClaimVersionState.Submitted;
        VoidReturns("put-pended", ClaimVoidResult.InvalidSourceState(claim, "current Status=Pended"));

        var response = await _client.PutAsJsonAsync(
            "/api/claims/put-pended/status", new { status = (int)ClaimStatus.Voided }, Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await _finalization.Received(1).VoidAsync(
            "put-pended", Arg.Is<ClaimVoidRequest>(r => !string.IsNullOrWhiteSpace(r.Reason)),
            "test-tenant", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
