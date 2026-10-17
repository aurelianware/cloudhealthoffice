using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.E2E;

// ═══════════════════════════════════════════════════════════════════════════
// Docker-compose service fixture
//
// Waits for claims-service (:5001), benefit-plan-service (:5002), and
// payment-service (:5003) to respond healthy before any test runs.
// ═══════════════════════════════════════════════════════════════════════════

public class DockerComposeFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public HttpClient ClaimsClient { get; private set; } = null!;
    public HttpClient BenefitPlanClient { get; private set; } = null!;
    public HttpClient PaymentClient { get; private set; } = null!;
    public HttpClient EligibilityClient { get; private set; } = null!;
    public HttpClient AuthorizationClient { get; private set; } = null!;

    private const string TenantId = "e2e-test-tenant";
    private const int MaxWaitSeconds = 120;

    public async Task InitializeAsync()
    {
        ClaimsClient = CreateClient("http://localhost:5001");
        BenefitPlanClient = CreateClient("http://localhost:5002");
        PaymentClient = CreateClient("http://localhost:5003");
        EligibilityClient = CreateClient("http://localhost:5007");
        AuthorizationClient = CreateClient("http://localhost:5005");

        await WaitForServiceAsync(ClaimsClient, "claims-service", "/health");
        await WaitForServiceAsync(BenefitPlanClient, "benefit-plan-service", "/health");
        await WaitForServiceAsync(PaymentClient, "payment-service", "/health");
        await WaitForServiceAsync(EligibilityClient, "eligibility-service", "/health");
        await WaitForServiceAsync(AuthorizationClient, "authorization-service", "/health");
    }

    public Task DisposeAsync()
    {
        ClaimsClient.Dispose();
        BenefitPlanClient.Dispose();
        PaymentClient.Dispose();
        EligibilityClient.Dispose();
        AuthorizationClient.Dispose();
        return Task.CompletedTask;
    }

    private static HttpClient CreateClient(string baseUrl)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.Add("X-Tenant-ID", TenantId);
        return client;
    }

    private static async Task WaitForServiceAsync(HttpClient client, string serviceName, string healthPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(MaxWaitSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync(healthPath);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Service not up yet
            }
            catch (OperationCanceledException)
            {
                // Per-request timeout — keep waiting for the service to start
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException(
            $"{serviceName} at {client.BaseAddress}{healthPath} did not become healthy within {MaxWaitSeconds}s. " +
            "Ensure the docker-compose stack is running: docker compose up -d");
    }

    public static JsonSerializerOptions SerializerOptions => JsonOptions;
}

[CollectionDefinition("DockerCompose")]
public class DockerComposeCollection : ICollectionFixture<DockerComposeFixture> { }

// ═══════════════════════════════════════════════════════════════════════════
// Golden-path claim-to-payment E2E test
//
// Exercises the claim lifecycle around adjudication:
//   1. Submit 837P claim → claims-service
//   2. Create 835 ERA payment → payment-service
//   3. Download 835 and verify payment amounts
//
// Adjudication itself is the claims-service pipeline, run on submission.
// This test used to call benefit-plan-service's synchronous
// /api/v1/adjudication/adjudicate dry run between steps 1 and 2; that
// endpoint was removed (docs/architecture/adjudicate-endpoint-decision.md).
// Asserting the pipeline's persisted outcome here would need member,
// coverage, provider and network fixtures this suite does not seed, so the
// payment amounts below are fixed.
// ═══════════════════════════════════════════════════════════════════════════

[Collection("DockerCompose")]
public class GoldenPathAdjudicationTests
{
    private readonly DockerComposeFixture _fixture;
    private static readonly JsonSerializerOptions Json = DockerComposeFixture.SerializerOptions;

    public GoldenPathAdjudicationTests(DockerComposeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GoldenPath_SubmitClaimAndPay835()
    {
        // ── Arrange: build a professional claim (837P) ──────────────────
        var serviceDate = DateTime.UtcNow.Date;
        var claimNumber = $"E2E-{Guid.NewGuid():N}".Substring(0, 20);
        // Use unique IDs per run so Redis-backed accumulator state doesn't bleed
        // between runs and cost-sharing assertions remain deterministic.
        var memberId = $"MBR-{Guid.NewGuid():N}".Substring(0, 20);
        var subscriberId = $"SUB-{Guid.NewGuid():N}".Substring(0, 20);
        var providerNpi = "1234567890";

        var claim = new
        {
            tenantId = "e2e-test-tenant",
            claimNumber,
            memberId,
            subscriberId,
            billingProviderNPI = providerNpi,
            billingProviderName = "E2E Family Medicine",
            placeOfServiceCode = "11",
            claimType = 1,  // Professional (837P)
            lineOfBusiness = 1,  // Commercial
            claimFrequencyCode = "1",
            totalChargeAmount = 175.00m,
            serviceDateFrom = serviceDate,
            serviceDateTo = serviceDate,
            diagnosisCodes = new[]
            {
                new { code = "Z00.00", codeQualifier = "ABK", pointerNumber = 1, description = "General adult medical exam" }
            },
            claimLines = new[]
            {
                new
                {
                    lineNumber = 1,
                    procedureCode = "99213",
                    procedureDescription = "Office/outpatient visit, est patient, level 3",
                    modifiers = Array.Empty<string>(),
                    diagnosisPointers = new[] { 1 },
                    units = 1m,
                    chargeAmount = 150.00m,
                    serviceDateFrom = serviceDate,
                    serviceDateTo = serviceDate,
                    placeOfServiceCode = "11"
                },
                new
                {
                    lineNumber = 2,
                    procedureCode = "36415",
                    procedureDescription = "Venipuncture for collection of specimen(s)",
                    modifiers = Array.Empty<string>(),
                    diagnosisPointers = new[] { 1 },
                    units = 1m,
                    chargeAmount = 25.00m,
                    serviceDateFrom = serviceDate,
                    serviceDateTo = serviceDate,
                    placeOfServiceCode = "11"
                }
            },
            status = 1  // Submitted
        };

        // ── Step 1: Submit claim to claims-service ──────────────────────
        var submitResponse = await _fixture.ClaimsClient.PostAsJsonAsync("/api/claims", claim, Json);

        Assert.True(
            submitResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Claim submission failed: {submitResponse.StatusCode} — {await submitResponse.Content.ReadAsStringAsync()}");

        var createdClaim = await submitResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        var claimId = createdClaim.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(claimId), "Claim ID should not be empty");

        // ── Payment amounts (fixed; see the class comment) ──────────────
        const decimal billedAmount = 175.00m;
        const decimal line1Payment = 96.00m;   // 99213: $120 allowed − $24 coinsurance
        const decimal line2Payment = 16.00m;   // 36415: $20 allowed − $4 coinsurance
        const decimal planPayment = line1Payment + line2Payment;
        const decimal memberResponsibility = 28.00m;

        // ── Step 2: Create 835 ERA payment via payment-service ──────────
        var checkNumber = $"E2E-CHK-{Guid.NewGuid():N}".Substring(0, 20);
        var payment = new
        {
            tenantId = "e2e-test-tenant",
            checkNumber,
            paymentMethod = "CHK",
            totalPaymentAmount = planPayment,
            paymentDate = DateTime.UtcNow,
            payerName = "E2E Health Plan",
            payerId = "E2E-PAYER-001",
            payeeName = "E2E Family Medicine",
            payeeNPI = providerNpi,
            claimPayments = new[]
            {
                new
                {
                    claimId,
                    patientControlNumber = claimNumber,
                    claimStatusCode = "1",  // Processed as primary
                    chargeAmount = billedAmount,
                    paymentAmount = planPayment,
                    patientResponsibilityAmount = memberResponsibility,
                    memberId,
                    renderingProviderNPI = providerNpi,
                    claimReceivedDate = DateTime.UtcNow,
                    serviceLines = new[]
                    {
                        new
                        {
                            lineNumber = 1,
                            procedureCode = "99213",
                            chargeAmount = 150.00m,
                            paymentAmount = line1Payment,
                            units = 1m,
                            serviceDateFrom = serviceDate,
                            serviceDateTo = serviceDate,
                            adjustments = Array.Empty<object>()
                        },
                        new
                        {
                            lineNumber = 2,
                            procedureCode = "36415",
                            chargeAmount = 25.00m,
                            paymentAmount = line2Payment,
                            units = 1m,
                            serviceDateFrom = serviceDate,
                            serviceDateTo = serviceDate,
                            adjustments = Array.Empty<object>()
                        }
                    },
                    claimAdjustments = Array.Empty<object>()
                }
            },
            status = 0  // Received
        };

        var paymentResponse = await _fixture.PaymentClient.PostAsJsonAsync("/api/payments", payment, Json);

        Assert.True(
            paymentResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Payment creation failed: {paymentResponse.StatusCode} — {await paymentResponse.Content.ReadAsStringAsync()}");

        var createdPayment = await paymentResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        var paymentId = createdPayment.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(paymentId), "Payment ID should not be empty");

        // ── Step 3: Download 835 ERA and verify ─────────────────────────
        var eraResponse = await _fixture.PaymentClient.GetAsync($"/api/payments/{paymentId}/835");

        Assert.True(
            eraResponse.StatusCode == HttpStatusCode.OK,
            $"835 download failed: {eraResponse.StatusCode} — {await eraResponse.Content.ReadAsStringAsync()}");

        var ediContent = await eraResponse.Content.ReadAsStringAsync();

        // Basic X12 835 structure validation
        Assert.Contains("ISA*", ediContent);          // Interchange header
        Assert.Contains("GS*HP*", ediContent);        // Functional group (HP = 835)
        Assert.Contains("ST*835*", ediContent);        // Transaction set header
        Assert.Contains("BPR*", ediContent);           // Financial information
        Assert.Contains($"TRN*1*{checkNumber}*", ediContent);  // Trace number with check
        Assert.Contains("CLP*", ediContent);           // Claim payment info
        Assert.Contains("SVC*", ediContent);           // Service line payment
        Assert.Contains("SE*", ediContent);            // Transaction set trailer

        // Verify the 835 contains our procedure codes
        Assert.Contains("99213", ediContent);
        Assert.Contains("36415", ediContent);

        // Verify payment amount is present in BPR segment
        var bprLine = ediContent.Split('~').First(s => s.TrimStart().StartsWith("BPR"));
        Assert.Contains(planPayment.ToString("F2"), bprLine);
    }
}
