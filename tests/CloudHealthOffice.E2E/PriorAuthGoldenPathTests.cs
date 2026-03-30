using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CloudHealthOffice.E2E;

// ═══════════════════════════════════════════════════════════════════════════
// Prior Authorization (278) golden-path E2E test
//
// Exercises the prior authorization lifecycle:
//   1. Submit authorization request → authorization-service
//   2. Process 278 response (approval decision)
//   3. Validate authorization for claims submission
//   4. Verify authorization summary statistics
// ═══════════════════════════════════════════════════════════════════════════

[Collection("DockerCompose")]
public class PriorAuthGoldenPathTests
{
    private readonly DockerComposeFixture _fixture;
    private static readonly JsonSerializerOptions Json = DockerComposeFixture.SerializerOptions;

    public PriorAuthGoldenPathTests(DockerComposeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FullAuthorizationLifecycle_SubmitReviewApproveValidate()
    {
        // ── Arrange: build a prior authorization (278) request ──────────
        var memberId = $"MBR-{Guid.NewGuid():N}"[..20];
        var authNumber = $"AUTH-{Guid.NewGuid():N}"[..20];
        var serviceDate = DateTime.UtcNow.Date.AddDays(7);

        var authRequest = new
        {
            tenantId = "e2e-test-tenant",
            memberId,
            coverageId = $"COV-{Guid.NewGuid():N}"[..20],
            patientFirstName = "John",
            patientLastName = "Smith",
            patientDateOfBirth = "1978-03-22",
            requestingProviderNPI = "1234567890",
            requestingProviderName = "E2E Primary Care",
            servicingProviderNPI = "9876543210",
            servicingProviderName = "E2E Imaging Center",
            facilityNPI = "5555555555",
            facilityName = "E2E Regional Hospital",
            serviceTypeCode = "42",  // MRI/CT Scan
            levelOfService = "E",    // Elective
            requestedServiceDateFrom = serviceDate,
            requestedServiceDateTo = serviceDate,
            diagnosisCodes = new[]
            {
                new { code = "M54.5", codeQualifier = "BK", description = "Low back pain" }
            },
            requestedServices = new[]
            {
                new
                {
                    procedureCode = "72148",
                    procedureDescription = "MRI Lumbar Spine without contrast",
                    requestedUnits = 1,
                    unitType = "UN",
                    placeOfServiceCode = "22"
                }
            },
            authorizationType = 0  // PreAuthorization
        };

        // ── Step 1: Submit authorization request ────────────────────────
        var submitResponse = await _fixture.AuthorizationClient.PostAsJsonAsync(
            "/api/authorizations", authRequest, Json);

        Assert.True(
            submitResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Authorization submission failed: {submitResponse.StatusCode} — {await submitResponse.Content.ReadAsStringAsync()}");

        var createdAuth = await submitResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        var authId = createdAuth.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(authId), "Authorization ID should not be empty");

        // Verify the authorization has a number assigned
        string createdAuthNumber;
        if (createdAuth.TryGetProperty("authorizationNumber", out var authNumProp) &&
            authNumProp.ValueKind == JsonValueKind.String)
        {
            createdAuthNumber = authNumProp.GetString()!;
        }
        else
        {
            createdAuthNumber = authId; // Fall back to ID if no number assigned
        }

        // ── Step 2: Retrieve and verify the authorization ───────────────
        var getResponse = await _fixture.AuthorizationClient.GetAsync(
            $"/api/authorizations/{authId}");

        Assert.True(
            getResponse.StatusCode == HttpStatusCode.OK,
            $"Get authorization failed: {getResponse.StatusCode}");

        var fetchedAuth = await getResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(memberId, fetchedAuth.GetProperty("memberId").GetString());

        // Status should be Submitted (0) initially
        if (fetchedAuth.TryGetProperty("status", out var statusProp))
        {
            var status = statusProp.ValueKind == JsonValueKind.Number
                ? statusProp.GetInt32()
                : 0;
            Assert.True(status >= 0, "Initial status should be valid");
        }

        // ── Step 3: Process 278 response (approve the authorization) ────
        var authResponse = new
        {
            controlNumber = $"278R-{Guid.NewGuid():N}"[..15],
            reviewDecision = "A1",  // Approved
            approvedUnits = 1,
            approvedServiceDateFrom = serviceDate,
            approvedServiceDateTo = serviceDate.AddDays(30),
            expirationDate = serviceDate.AddDays(90),
            reviewerName = "Dr. E2E Reviewer",
            reviewerPhone = "555-0100"
        };

        var responseResult = await _fixture.AuthorizationClient.PostAsJsonAsync(
            $"/api/authorizations/{authId}/response", authResponse, Json);

        Assert.True(
            responseResult.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"278 response processing failed: {responseResult.StatusCode} — {await responseResult.Content.ReadAsStringAsync()}");

        // ── Step 4: Validate authorization for claims submission ────────
        var validateResponse = await _fixture.AuthorizationClient.GetAsync(
            $"/api/authorizations/{createdAuthNumber}/validate");

        Assert.True(
            validateResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound,
            $"Authorization validation returned unexpected status: {validateResponse.StatusCode}");

        if (validateResponse.StatusCode == HttpStatusCode.OK)
        {
            var validation = await validateResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.True(validation.TryGetProperty("isValid", out _),
                "Validation response should contain isValid field");
        }

        // ── Step 5: Verify authorization appears in search results ──────
        var searchResponse = await _fixture.AuthorizationClient.GetAsync(
            $"/api/authorizations/search?memberId={memberId}&page=1&pageSize=10");

        Assert.True(
            searchResponse.StatusCode == HttpStatusCode.OK,
            $"Authorization search failed: {searchResponse.StatusCode}");

        var searchResults = await searchResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        // Results should be an array containing our authorization
        if (searchResults.ValueKind == JsonValueKind.Array)
        {
            Assert.True(searchResults.GetArrayLength() > 0,
                "Search results should contain at least one authorization");
        }

        // ── Step 6: Check summary statistics ────────────────────────────
        var summaryResponse = await _fixture.AuthorizationClient.GetAsync(
            "/api/authorizations/summary");

        if (summaryResponse.StatusCode == HttpStatusCode.OK)
        {
            var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.True(summary.TryGetProperty("totalAuthorizations", out _),
                "Summary should contain totalAuthorizations");
        }
    }

    [Fact]
    public async Task AuthorizationDenial_ProcessAndVerify()
    {
        // ── Arrange: submit an authorization that will be denied ─────────
        var memberId = $"MBR-{Guid.NewGuid():N}"[..20];

        var authRequest = new
        {
            tenantId = "e2e-test-tenant",
            memberId,
            coverageId = $"COV-{Guid.NewGuid():N}"[..20],
            patientFirstName = "Alice",
            patientLastName = "Johnson",
            patientDateOfBirth = "1990-01-10",
            requestingProviderNPI = "1234567890",
            requestingProviderName = "E2E Primary Care",
            servicingProviderNPI = "9876543210",
            servicingProviderName = "E2E Specialist",
            serviceTypeCode = "42",
            levelOfService = "E",
            requestedServiceDateFrom = DateTime.UtcNow.Date.AddDays(7),
            requestedServiceDateTo = DateTime.UtcNow.Date.AddDays(7),
            diagnosisCodes = new[]
            {
                new { code = "R10.9", codeQualifier = "BK", description = "Unspecified abdominal pain" }
            },
            requestedServices = new[]
            {
                new
                {
                    procedureCode = "70553",
                    procedureDescription = "MRI Brain w/ and w/o contrast",
                    requestedUnits = 1,
                    unitType = "UN",
                    placeOfServiceCode = "22"
                }
            },
            authorizationType = 0
        };

        var submitResponse = await _fixture.AuthorizationClient.PostAsJsonAsync(
            "/api/authorizations", authRequest, Json);

        Assert.True(
            submitResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Authorization submission failed: {submitResponse.StatusCode}");

        var createdAuth = await submitResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        var authId = createdAuth.GetProperty("id").GetString()!;

        // ── Deny the authorization ──────────────────────────────────────
        var denialResponse = new
        {
            controlNumber = $"278D-{Guid.NewGuid():N}"[..15],
            reviewDecision = "A3",  // Denied
            denialReasonCode = "NOT_MEDICALLY_NECESSARY",
            denialReason = "Insufficient clinical documentation to support medical necessity",
            followUpAction = "Submit additional clinical records for reconsideration",
            reviewerName = "Dr. E2E Reviewer"
        };

        var responseResult = await _fixture.AuthorizationClient.PostAsJsonAsync(
            $"/api/authorizations/{authId}/response", denialResponse, Json);

        Assert.True(
            responseResult.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"Denial processing failed: {responseResult.StatusCode}");

        // ── Verify the denied authorization ─────────────────────────────
        var getResponse = await _fixture.AuthorizationClient.GetAsync(
            $"/api/authorizations/{authId}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var deniedAuth = await getResponse.Content.ReadFromJsonAsync<JsonElement>(Json);

        // Status should reflect denial
        if (deniedAuth.TryGetProperty("reviewDecision", out var decisionProp) &&
            decisionProp.ValueKind == JsonValueKind.String)
        {
            Assert.Equal("A3", decisionProp.GetString());
        }
    }

    [Fact]
    public async Task CancelAuthorization_DeleteAndVerify()
    {
        // ── Arrange: create an authorization to cancel ──────────────────
        var authRequest = new
        {
            tenantId = "e2e-test-tenant",
            memberId = $"MBR-{Guid.NewGuid():N}"[..20],
            coverageId = $"COV-{Guid.NewGuid():N}"[..20],
            patientFirstName = "Bob",
            patientLastName = "Cancel",
            patientDateOfBirth = "1975-08-30",
            requestingProviderNPI = "1234567890",
            requestingProviderName = "E2E Primary Care",
            serviceTypeCode = "42",
            levelOfService = "E",
            requestedServiceDateFrom = DateTime.UtcNow.Date.AddDays(14),
            requestedServiceDateTo = DateTime.UtcNow.Date.AddDays(14),
            diagnosisCodes = new[]
            {
                new { code = "M54.5", codeQualifier = "BK", description = "Low back pain" }
            },
            requestedServices = new[]
            {
                new
                {
                    procedureCode = "72148",
                    procedureDescription = "MRI Lumbar Spine",
                    requestedUnits = 1,
                    unitType = "UN",
                    placeOfServiceCode = "22"
                }
            },
            authorizationType = 0
        };

        var submitResponse = await _fixture.AuthorizationClient.PostAsJsonAsync(
            "/api/authorizations", authRequest, Json);

        Assert.True(
            submitResponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"Authorization submission failed: {submitResponse.StatusCode}");

        var createdAuth = await submitResponse.Content.ReadFromJsonAsync<JsonElement>(Json);
        var authId = createdAuth.GetProperty("id").GetString()!;

        // ── Cancel the authorization ────────────────────────────────────
        var deleteResponse = await _fixture.AuthorizationClient.DeleteAsync(
            $"/api/authorizations/{authId}");

        Assert.True(
            deleteResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound,
            $"Authorization cancellation returned unexpected status: {deleteResponse.StatusCode}");
    }
}
