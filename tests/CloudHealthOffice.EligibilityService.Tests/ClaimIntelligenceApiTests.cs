using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class ClaimIntelligenceApiTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;
    private readonly HttpClient _client;

    public ClaimIntelligenceApiTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-alpha");
    }

    [Fact]
    public async Task Get_PaidClaim_ReturnsLifecycleAndFinancialSummary()
    {
        var transmissionId = await SubmitAsync("CLM-INTEL-PAID");
        await _client.PostAsJsonAsync($"/api/dev/gateway/claims/{transmissionId}/277ca", new
        {
            acknowledgmentId = "ack-intel-paid",
            status = "Accepted",
            claimControlNumber = "INTEL-CCN-1"
        });
        var inject = await _client.PostAsJsonAsync("/api/dev/gateway/remittance", new
        {
            remittanceId = "era-intel-paid",
            gateway = "Mock",
            paymentAmount = 320,
            claims = new[]
            {
                new
                {
                    payerClaimControlNumber = "INTEL-CCN-1",
                    patientControlNumber = "CLM-INTEL-PAID",
                    chargedAmount = 500,
                    paidAmount = 320,
                    patientResponsibilityAmount = 80,
                    claimStatusCode = "1"
                }
            }
        });
        inject.EnsureSuccessStatusCode();

        var response = await _client.GetAsync("/api/claims/CLM-INTEL-PAID/intelligence");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(nameof(ClaimIntelligenceLifecycleStatus.Paid), doc.RootElement.GetProperty("lifecycleStatus").GetString());
        Assert.Equal("Accepted", doc.RootElement.GetProperty("transactions").GetProperty("277CA").GetProperty("status").GetString());
        Assert.Equal(320, doc.RootElement.GetProperty("financial").GetProperty("paidAmount").GetDecimal());
        Assert.True(doc.RootElement.GetProperty("financial").GetProperty("hasRemittance").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("timeline").GetArrayLength() >= 2);
    }

    [Fact]
    public async Task Get_OtherTenantHeader_IsNotFound()
    {
        await SubmitAsync("CLM-INTEL-ISO");
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-beta");
        var response = await other.GetAsync("/api/claims/CLM-INTEL-ISO/intelligence");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_MissingTenant_IsBadRequest()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync("/api/claims/CLM-INTEL-ISO/intelligence");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownClaim_IsNotFound()
    {
        var response = await _client.GetAsync("/api/claims/CLM-DOES-NOT-EXIST/intelligence");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<string> SubmitAsync(string claimId)
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId,
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 500,
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "D2740", units = 1, chargeAmount = 500 }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString()!;
    }
}
