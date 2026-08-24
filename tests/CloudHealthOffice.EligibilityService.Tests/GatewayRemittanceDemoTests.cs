using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class GatewayRemittanceDemoTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;
    private readonly HttpClient _client;

    public GatewayRemittanceDemoTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-alpha");
    }

    [Fact]
    public async Task DevelopmentEndpoint_MatchesSyntheticEraWithoutPosting()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-ERA-1001",
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 500,
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 500 }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transmissionId));

        await _client.PostAsJsonAsync($"/api/dev/gateway/claims/{transmissionId}/277ca", new
        {
            acknowledgmentId = "ack-era-1001",
            status = "Accepted",
            claimControlNumber = "ERA-CCN-1001"
        });

        var inject = await _client.PostAsJsonAsync("/api/dev/gateway/remittance", new
        {
            remittanceId = "era-demo-1001",
            gateway = "Mock",
            paymentAmount = 320,
            paymentMethodCode = "ACH",
            claims = new[]
            {
                new
                {
                    payerClaimControlNumber = "ERA-CCN-1001",
                    patientControlNumber = "CLM-ERA-1001",
                    claimStatusCode = "1",
                    chargedAmount = 500,
                    paidAmount = 320,
                    patientResponsibilityAmount = 80
                }
            }
        });
        inject.EnsureSuccessStatusCode();
        using var injectDoc = JsonDocument.Parse(await inject.Content.ReadAsStringAsync());
        Assert.Equal(nameof(RemittanceLifecycleStatus.AvailableForPosting),
            injectDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, injectDoc.RootElement.GetProperty("matchedClaimCount").GetInt32());

        var tx = await _client.GetAsync($"/api/dev/gateway/transmissions/{transmissionId}");
        tx.EnsureSuccessStatusCode();
        using var txDoc = JsonDocument.Parse(await tx.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(GatewayClaimTransmissionStatus.AcknowledgmentAccepted),
            txDoc.RootElement.GetProperty("status").GetString());

        var history = await _client.GetAsync($"/api/dev/gateway/claims/{transmissionId}/remittance");
        history.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task History_TenantMismatch_IsRejected()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-ERA-1002",
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 10,
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 10 }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();

        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-beta");
        var history = await other.GetAsync($"/api/dev/gateway/claims/{transmissionId}/remittance");
        Assert.Equal(HttpStatusCode.BadRequest, history.StatusCode);
    }
}
