using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.EligibilityService.Tests;

public class GatewayClaimStatusDemoTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;
    private readonly HttpClient _client;

    public GatewayClaimStatusDemoTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-alpha");
    }

    [Fact]
    public async Task DevelopmentEndpoint_InquiresAndListsSyntheticStatus()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-STATUS-1001",
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 10,
            serviceDateFrom = "2026-01-15",
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon", dateOfBirth = "2000-01-01" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 10, serviceDateFrom = "2026-01-15" }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transmissionId));

        var check = await _client.PostAsync($"/api/dev/gateway/claims/{transmissionId}/status", null);
        check.EnsureSuccessStatusCode();
        using var checkDoc = JsonDocument.Parse(await check.Content.ReadAsStringAsync());
        Assert.True(checkDoc.RootElement.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("InProcess", checkDoc.RootElement.GetProperty("result").GetProperty("status").GetString());

        var history = await _client.GetAsync($"/api/dev/gateway/claims/{transmissionId}/status");
        history.EnsureSuccessStatusCode();
        using var historyDoc = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, historyDoc.RootElement.ValueKind);
        Assert.Equal(1, historyDoc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task History_TenantMismatchHeader_IsRejected()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-STATUS-1002",
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 10,
            serviceDateFrom = "2026-01-15",
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon", dateOfBirth = "2000-01-01" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 10, serviceDateFrom = "2026-01-15" }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transmissionId));

        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-beta");
        var history = await other.GetAsync($"/api/dev/gateway/claims/{transmissionId}/status");
        Assert.Equal(HttpStatusCode.BadRequest, history.StatusCode);
    }
}
