using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Services;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// Canonical V1 submission endpoint coverage (capability 5.3 —
/// <c>POST /api/v1/claims</c>). Asserts:
/// <list type="bullet">
///   <item>201 with the canonical AdapterClaim on success</item>
///   <item>400 body with top-level <c>error</c> and <c>errors[]</c> fields on validation failure</item>
///   <item>501 when the tenant routes to a stub vendor adapter</item>
///   <item>actorId / correlationId resolution from HttpContext flow into the service</item>
/// </list>
/// Detailed orchestration coverage is on
/// <see cref="ClaimSubmissionServiceTests"/>; this suite exercises
/// the controller's HTTP shape only.
/// </summary>
public class ClaimsV1ControllerSubmissionTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimSubmissionService _service;

    public ClaimsV1ControllerSubmissionTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _service = factory.SubmissionService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        _service.ClearSubstitute();
    }

    [Fact]
    public async Task Submit_ValidClaim_Returns201_WithCreatedClaim()
    {
        var inbound = BuildAdapterClaim();

        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<AdapterClaim>();
                c.Id = "claim-id-1";
                c.ClaimVersionId = "claim-id-1";
                c.VersionNumber = 1;
                c.VersionState = ClaimVersionState.Submitted;
                c.Status = ClaimStatus.Submitted;
                return ClaimSubmissionResult.Ok(c);
            });

        var response = await _client.PostAsJsonAsync("/api/v1/claims", inbound);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AdapterClaim>();
        Assert.NotNull(body);
        Assert.Equal("claim-id-1", body!.Id);
        Assert.Equal(1, body.VersionNumber);
        Assert.Equal(ClaimVersionState.Submitted, body.VersionState);

        // Location header should point at the member-search GET so the
        // portal can pull the new claim by member id without a separate
        // round-trip to discover the URL.
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("memberId=" + inbound.MemberId, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Submit_ValidationFailure_Returns400_WithFieldErrors()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimSubmissionResult.ValidationFailed(new[]
            {
                new ValidationError { Field = "MemberId", Code = "Required", Message = "MemberId is required" },
                new ValidationError { Field = "ClaimLines", Code = "MinCount", Message = "Claim must have at least one service line" }
            }));

        var inbound = BuildAdapterClaim();
        inbound.MemberId = string.Empty;
        inbound.ClaimLines.Clear();

        var response = await _client.PostAsJsonAsync("/api/v1/claims", inbound);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "MemberId");
        Assert.Contains(errors, e => e.GetProperty("field").GetString() == "ClaimLines");
    }

    [Fact]
    public async Task Submit_AdapterNotImplemented_Returns501()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimSubmissionResult.AdapterNotImplemented(
                "QNXT claim adapter not yet implemented."));

        var response = await _client.PostAsJsonAsync("/api/v1/claims", BuildAdapterClaim());
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("AdapterNotImplemented", json);
    }

    [Fact]
    public async Task Submit_ForwardsActorIdFromXUserIdHeader()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        client.DefaultRequestHeaders.Add("X-User-Id", "examiner-7");

        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), "test-tenant",
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimSubmissionResult.Ok(ci.Arg<AdapterClaim>()));

        var response = await client.PostAsJsonAsync("/api/v1/claims", BuildAdapterClaim());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await _service.Received(1).SubmitAsync(
            Arg.Any<AdapterClaim>(),
            "test-tenant",
            "examiner-7",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_ForwardsCorrelationIdFromHeader()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "trace-abc-123");

        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ClaimSubmissionResult.Ok(ci.Arg<AdapterClaim>()));

        var response = await client.PostAsJsonAsync("/api/v1/claims", BuildAdapterClaim());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await _service.Received(1).SubmitAsync(
            Arg.Any<AdapterClaim>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            "trace-abc-123",
            Arg.Any<CancellationToken>());
    }

    private static AdapterClaim BuildAdapterClaim()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new AdapterClaim
        {
            ClaimNumber = "CLM-V1-0001",
            MemberId = "MEM-V1",
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 150.00m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                }
            }
        };
    }
}
