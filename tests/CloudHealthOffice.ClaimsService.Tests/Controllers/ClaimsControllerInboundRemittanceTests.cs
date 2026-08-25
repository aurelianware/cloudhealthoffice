using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

public class ClaimsControllerInboundRemittanceTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private readonly ClaimsApiFactory _factory;

    public ClaimsControllerInboundRemittanceTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        factory.FinalizationService.ClearReceivedCalls();
    }

    private static object Body(string remittanceId = "era-1") => new
    {
        remittanceId,
        paymentAmount = 320m,
        patientResponsibility = 80m,
        paymentDate = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Claim OpenClaim(string inboundRemittanceId = "") => new()
    {
        Id = "c1",
        TenantId = "test-tenant",
        ClaimNumber = "CLM-001",
        Status = ClaimStatus.Submitted,
        VersionState = ClaimVersionState.Submitted,
        BillingProviderNPI = "1234567890",
        LineOfBusiness = LineOfBusiness.Commercial,
        MemberId = "m1",
        TotalChargeAmount = 500m,
        ServiceDateFrom = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        InboundRemittanceId = string.IsNullOrEmpty(inboundRemittanceId) ? null : inboundRemittanceId
    };

    [Fact]
    public async Task MissingClaim_Returns404_WithoutFinalize()
    {
        _factory.ClaimRepository.GetByIdAsync("missing").Returns((Claim?)null);

        var response = await _client.PostAsJsonAsync("/api/claims/missing/inbound-remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await _factory.FinalizationService.DidNotReceiveWithAnyArgs().FinalizeAsync(
            default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task ExistingClaim_RecordsPaymentWithoutPaymentRun()
    {
        var claim = OpenClaim();
        _factory.ClaimRepository.GetByIdAsync("c1").Returns(claim);
        _factory.ClaimRepository.UpdateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        var response = await _client.PostAsJsonAsync("/api/claims/c1/inbound-remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("era-1", claim.InboundRemittanceId);
        Assert.Equal(320m, claim.AdjudicationResult!.PayerPayment);
        Assert.Equal(80m, claim.AdjudicationResult.PatientResponsibility);
        await _factory.FinalizationService.DidNotReceiveWithAnyArgs().FinalizeAsync(
            default!, default!, default!, default!, default!, default!);
    }

    [Fact]
    public async Task SameRemittance_Returns409()
    {
        _factory.ClaimRepository.GetByIdAsync("c1").Returns(OpenClaim("era-1"));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/inbound-remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await _factory.ClaimRepository.DidNotReceive().UpdateAsync(Arg.Any<Claim>());
    }

    [Fact]
    public async Task DifferentRemittanceAlreadyPosted_Returns422()
    {
        _factory.ClaimRepository.GetByIdAsync("c1").Returns(OpenClaim("era-other"));

        var response = await _client.PostAsJsonAsync("/api/claims/c1/inbound-remittance", Body(), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await _factory.FinalizationService.DidNotReceiveWithAnyArgs().FinalizeAsync(
            default!, default!, default!, default!, default!, default!);
    }
}
