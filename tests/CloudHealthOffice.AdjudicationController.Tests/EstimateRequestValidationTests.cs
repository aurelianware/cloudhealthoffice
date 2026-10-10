using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BenefitPlanService.Models.Estimate;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;

namespace CloudHealthOffice.AdjudicationController.Tests;

/// <summary>
/// /api/v1/adjudication/estimate request validation for the institutional inputs:
/// a line may carry a revenue code instead of a procedure code (room and board),
/// but needs one of them; the length of stay must not be negative; DRG and length
/// of stay reach the estimate service as sent.
/// </summary>
public class EstimateRequestValidationTests : IClassFixture<AdjudicationControllerTests.Factory>
{
    private const string Tenant = "estimate-validation-tenant";
    private readonly AdjudicationControllerTests.Factory _factory;

    public EstimateRequestValidationTests(AdjudicationControllerTests.Factory factory)
    {
        _factory = factory;
        _factory.EstimateService
            .EstimateAsync(Arg.Any<string>(), Arg.Any<PaymentEstimateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentEstimateResponse { RequestId = "est-validation" });
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("dev-user", Tenant, new[] { ChoRolePermissions.TenantAdmin }));
        return client;
    }

    private static object Body(object[] lines, int? lengthOfStay = null) => new
    {
        memberId = "MBR-1",
        benefitPlanId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        providerNpi = "1234567893",
        serviceDate = "2026-03-01",
        claimType = "Institutional",
        billType = "111",
        drgCode = "470",
        lengthOfStay,
        lines,
    };

    [Fact]
    public async Task RevenueCodeOnlyLine_IsAccepted_AndInstitutionalInputsReachTheService()
    {
        using var client = Client();

        var resp = await client.PostAsJsonAsync("/api/v1/adjudication/estimate", Body(
            [new { lineNumber = 1, revenueCode = "0120", chargeAmount = 9000m, units = 3 }], lengthOfStay: 3));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await _factory.EstimateService.Received().EstimateAsync(
            Tenant,
            Arg.Is<PaymentEstimateRequest>(r =>
                r.DrgCode == "470" && r.LengthOfStay == 3
                && r.Lines.Count == 1 && r.Lines[0].RevenueCode == "0120" && string.IsNullOrEmpty(r.Lines[0].ProcedureCode)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LineWithNeitherProcedureNorRevenueCode_Is400()
    {
        using var client = Client();

        var resp = await client.PostAsJsonAsync("/api/v1/adjudication/estimate", Body(
            [new { lineNumber = 1, chargeAmount = 100m }]));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("ProcedureCode or a RevenueCode", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NegativeLengthOfStay_Is400()
    {
        using var client = Client();

        var resp = await client.PostAsJsonAsync("/api/v1/adjudication/estimate", Body(
            [new { lineNumber = 1, procedureCode = "99223", chargeAmount = 100m }], lengthOfStay: -1));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("LengthOfStay", await resp.Content.ReadAsStringAsync());
    }
}
