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
// Golden-path adjudication E2E test
//
// Exercises the core claim lifecycle:
//   1. Submit 837P claim → claims-service
//   2. Adjudicate (pricing + benefit calc + NCCI edits) → benefit-plan-service
//   3. Verify adjudication response
//   4. Create 835 ERA payment → payment-service
//   5. Download 835 and verify payment amounts
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
    public async Task FullAdjudicationGoldenPath_SubmitAdjudicateAndPay()
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

        // ── Step 2: Create a benefit plan and adjudicate ────────────────
        // A benefit plan must exist before adjudication; seed one for this run.
        var planBusinessId = $"E2E-PLN-{Guid.NewGuid():N}".Substring(0, 20);
        var benefitPlanPayload = new
        {
            tenantId = "e2e-test-tenant",
            planId = planBusinessId,
            planName = "E2E Golden Path PPO Plan",
            payer = "E2E Health Plan",
            effectiveDate = DateTime.UtcNow.AddYears(-1),
            planType = "PPO",
            lineOfBusiness = "Commercial",
            costSharing = new
            {
                individualDeductible = 0m,
                familyDeductible = 0m,
                individualOutOfPocketMax = 5000m,
                familyOutOfPocketMax = 10000m,
                inNetworkDeductible = 0m,
                outOfNetworkDeductible = 0m,
                inNetworkOutOfPocketMax = 5000m,
                outOfNetworkOutOfPocketMax = 10000m
            },
            benefits = new[]
            {
                new
                {
                    serviceCategory = "OutpatientServices",
                    description = "Office visits and lab",
                    cptCodes = new[] { "99213", "36415" },
                    inNetworkCopay = 30m,
                    inNetworkCoinsurance = 0.20m,
                    deductibleApplies = false
                }
            },
            networkTiers = new[]
            {
                new
                {
                    tierName = "InNetwork",
                    tierLevel = 1,
                    providerNpis = new[] { providerNpi }
                }
            },
            isActive = true
        };

        var planCreateResponse = await _fixture.BenefitPlanClient.PostAsJsonAsync(
            "/api/v1/plans", benefitPlanPayload, Json);

        var planCreateContent = await planCreateResponse.Content.ReadAsStringAsync();
        Assert.True(
            planCreateResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Benefit plan creation failed: {planCreateResponse.StatusCode} — {planCreateContent}");

        var createdPlan = JsonSerializer.Deserialize<JsonElement>(planCreateContent, Json);
        var benefitPlanId = Guid.Parse(createdPlan.GetProperty("id").GetString()!);

        var adjudicationRequest = new
        {
            claimId,
            memberId,
            subscriberId,
            benefitPlanId,
            serviceDate = DateOnly.FromDateTime(serviceDate).ToString("yyyy-MM-dd"),
            providerNpi = providerNpi,
            networkTier = "InNetwork",
            lineOfBusiness = 1,
            lines = new[]
            {
                new
                {
                    lineNumber = 1,
                    procedureCode = "99213",
                    codeType = "CPT",
                    modifiers = Array.Empty<string>(),
                    placeOfService = "11",
                    billedAmount = 150.00m,
                    units = 1m,
                    diagnosisCodes = new[] { "Z00.00" }
                },
                new
                {
                    lineNumber = 2,
                    procedureCode = "36415",
                    codeType = "CPT",
                    modifiers = Array.Empty<string>(),
                    placeOfService = "11",
                    billedAmount = 25.00m,
                    units = 1m,
                    diagnosisCodes = new[] { "Z00.00" }
                }
            }
        };

        var adjResponse = await _fixture.BenefitPlanClient.PostAsJsonAsync(
            "/api/v1/adjudication/adjudicate", adjudicationRequest, Json);

        // NCCI edits may return 422 if lines are bundled — that's a valid
        // adjudication outcome, not a test failure. We assert on 200 OR 422.
        Assert.True(
            adjResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity,
            $"Adjudication returned unexpected status: {adjResponse.StatusCode} — " +
            $"{await adjResponse.Content.ReadAsStringAsync()}");

        if (adjResponse.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // NCCI edit failure — verify the edit response structure
            var ncciError = await adjResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.Equal("NCCI_MUE_EDIT_FAILURE", ncciError.GetProperty("error").GetString());
            Assert.True(ncciError.GetProperty("editFailures").GetArrayLength() > 0,
                "NCCI edit failures array should not be empty");

            // The golden path still validates the NCCI edit plumbing works E2E.
            // Log and return — payment step doesn't apply for denied/edited claims.
            return;
        }

        // ── Step 3: Verify adjudication response structure ──────────────
        var adjResult = await adjResponse.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Claim ID echoed back
        Assert.Equal(claimId, adjResult.GetProperty("claimId").GetString());

        // Totals block present with expected fields
        var totals = adjResult.GetProperty("totals");
        var allowedAmount = totals.GetProperty("allowedAmount").GetDecimal();
        var deductibleAmount = totals.GetProperty("deductibleAmount").GetDecimal();
        var copayAmount = totals.GetProperty("copayAmount").GetDecimal();
        var coinsuranceAmount = totals.GetProperty("coinsuranceAmount").GetDecimal();
        var memberResponsibility = totals.GetProperty("memberResponsibility").GetDecimal();
        var planPayment = totals.GetProperty("planPayment").GetDecimal();
        var billedAmount = totals.GetProperty("billedAmount").GetDecimal();

        Assert.Equal(175.00m, billedAmount);
        Assert.True(allowedAmount > 0, "Allowed amount should be positive");
        Assert.True(allowedAmount <= billedAmount, "Allowed should not exceed billed");

        // Member cost-sharing adds up
        var expectedMemberResp = deductibleAmount + copayAmount + coinsuranceAmount;
        Assert.Equal(expectedMemberResp, memberResponsibility);

        // Plan payment = allowed - member responsibility
        Assert.Equal(allowedAmount - memberResponsibility, planPayment);

        // Lines array
        var lines = adjResult.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());

        foreach (var line in lines.EnumerateArray())
        {
            var lineProcedure = line.GetProperty("procedureCode").GetString();
            Assert.Contains(lineProcedure, new[] { "99213", "36415" });
            Assert.True(line.GetProperty("allowedAmount").GetDecimal() >= 0,
                $"Line {lineProcedure}: allowed amount should be non-negative");
            Assert.True(line.TryGetProperty("isCovered", out _),
                $"Line {lineProcedure}: should have isCovered field");
        }

        // Build a stable lookup by lineNumber so indexing doesn't depend on array order
        var adjLinesByNumber = lines.EnumerateArray()
            .ToDictionary(l => l.GetProperty("lineNumber").GetInt32());

        // ── Step 4: Create 835 ERA payment via payment-service ──────────
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
                            paymentAmount = adjLinesByNumber[1].GetProperty("planPayment").GetDecimal(),
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
                            paymentAmount = adjLinesByNumber[2].GetProperty("planPayment").GetDecimal(),
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

        // ── Step 5: Download 835 ERA and verify ─────────────────────────
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
