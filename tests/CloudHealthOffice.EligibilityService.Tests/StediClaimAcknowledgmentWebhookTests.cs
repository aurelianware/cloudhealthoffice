using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.EligibilityService.Tests;

public class StediClaimAcknowledgmentWebhookTests : IClassFixture<EligibilityApiFactory>
{
    private readonly EligibilityApiFactory _factory;
    private readonly HttpClient _client;

    public StediClaimAcknowledgmentWebhookTests(EligibilityApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-alpha");
    }

    [Fact]
    public async Task Webhook_WithoutCredential_IsUnauthorized_WithoutTenantHeader()
    {
        using var naked = _factory.CreateClient();
        var response = await naked.PostAsync(
            "/api/integrations/stedi/claim-responses",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_WrongCredential_IsUnauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/stedi/claim-responses")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "wrong");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_ValidCredential_Routes835ToRemittance()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/integrations/stedi/claim-responses")
        {
            Content = new StringContent(
                """
                {
                  "id": "evt-835",
                  "detail-type": "transaction.processed.v2",
                  "detail": {
                    "transactionId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                    "direction": "INBOUND",
                    "x12": { "metadata": { "transaction": { "transactionSetIdentifier": "835" } } }
                  }
                }
                """,
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "test-webhook-secret");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ignored").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("processed").GetBoolean());
        Assert.Equal("835", doc.RootElement.GetProperty("transactionSet").GetString());
    }

    [Fact]
    public async Task SyntheticAcceptedThenRejected_StaySeparateFromAdjudication()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-P-1001",
            claimVersion = 1,
            claimType = "Professional",
            frequencyCode = "1",
            payerId = "60054",
            placeOfServiceCode = "11",
            totalCharge = 109.20,
            billingProvider = new { npi = "1999999984", organizationName = "Therapy Associates" },
            subscriber = new { memberId = "U7777788888", firstName = "John", lastName = "Anon" },
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "90837", units = 1, chargeAmount = 109.20 }
            }
        });
        submit.EnsureSuccessStatusCode();
        using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var transmissionId = submitDoc.RootElement.GetProperty("result").GetProperty("transmissionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transmissionId));

        var inject = await _client.PostAsJsonAsync(
            $"/api/dev/gateway/claims/{transmissionId}/277ca",
            new
            {
                acknowledgmentId = "synthetic-ack-001",
                status = "Accepted",
                claimControlNumber = "synthetic-pcn-001",
                originalSubmissionId = "synthetic-sub-001"
            });
        inject.EnsureSuccessStatusCode();
        using var injectDoc = JsonDocument.Parse(await inject.Content.ReadAsStringAsync());
        Assert.Equal("Accepted", injectDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            nameof(GatewayClaimTransmissionStatus.AcknowledgmentAccepted),
            injectDoc.RootElement.GetProperty("transmissionStatus").GetString());
        Assert.False(injectDoc.RootElement.GetProperty("replay").GetBoolean());

        var replay = await _client.PostAsJsonAsync(
            $"/api/dev/gateway/claims/{transmissionId}/277ca",
            new { acknowledgmentId = "synthetic-ack-001", status = "Accepted" });
        replay.EnsureSuccessStatusCode();
        using var replayDoc = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.True(replayDoc.RootElement.GetProperty("replay").GetBoolean());

        var tx = await _client.GetAsync($"/api/dev/gateway/transmissions/{transmissionId}");
        tx.EnsureSuccessStatusCode();
        using var txDoc = JsonDocument.Parse(await tx.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(GatewayClaimTransmissionStatus.AcknowledgmentAccepted),
            txDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("tenant-alpha", txDoc.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task SyntheticRejected_DoesNotMarkPaid()
    {
        var submit = await _client.PostAsJsonAsync("/api/dev/gateway/claims", new
        {
            tenantId = "tenant-alpha",
            claimId = "CLM-P-1002",
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

        var inject = await _client.PostAsJsonAsync(
            $"/api/dev/gateway/claims/{transmissionId}/277ca",
            new
            {
                acknowledgmentId = "synthetic-ack-rej",
                status = "Rejected",
                errors = new[]
                {
                    new
                    {
                        categoryCode = "A3",
                        statusCode = "164",
                        description = "Entity's contract/member number.",
                        entityCode = "IL",
                        category = "InvalidSubscriber"
                    }
                }
            });
        inject.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await inject.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            nameof(GatewayClaimTransmissionStatus.AcknowledgmentRejected),
            doc.RootElement.GetProperty("transmissionStatus").GetString());
    }
}
