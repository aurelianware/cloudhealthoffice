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
        _client = factory.CreateClient();
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

    [Fact]
    public async Task ProcessRemittance_ZeroPayment_BypassesFinalizationService()
    {
        // Zero-payment remittances stay on the legacy direct-write Denied path.
        // The finalization service must NOT be called.
        var existing = new Claim
        {
            Id = "c1",
            TenantId = "test-tenant",
            ClaimNumber = "CLM-001",
            Status = ClaimStatus.Approved,
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            MemberId = "m1",
            TotalChargeAmount = 1000m,
            ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        _factory.ClaimRepository.GetByIdAsync("c1").Returns(existing);
        _factory.ClaimRepository.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var response = await _client.PostAsJsonAsync("/api/claims/c1/remittance", Body(amount: 0m), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _finalization.DidNotReceiveWithAnyArgs().FinalizeAsync(
            default!, default!, default!, default!, default!, default!);
    }
}
