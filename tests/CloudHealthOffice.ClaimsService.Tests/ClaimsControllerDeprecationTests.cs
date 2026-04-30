using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Services;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>
/// Capability 5.3 deprecation coverage for the legacy
/// <c>POST /api/claims</c> endpoint. Asserts:
/// <list type="bullet">
///   <item><c>[Obsolete]</c> attribute is present with a migration message</item>
///   <item><c>Deprecation</c> + <c>Link</c> response headers per RFC 8594</item>
///   <item>Internal call routes through <see cref="IClaimSubmissionService"/></item>
///   <item>Response shape (domain <see cref="Claim"/>) is unchanged</item>
/// </list>
/// Sunset header is intentionally omitted — capability 5.13 schedules
/// removal and sets it then.
/// </summary>
public class ClaimsControllerDeprecationTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;
    private readonly IClaimSubmissionService _service;

    public ClaimsControllerDeprecationTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _service = factory.SubmissionService;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        _service.ClearSubstitute();
    }

    [Fact]
    public void LegacyPost_HasObsoleteAttribute_WithMigrationMessage()
    {
        var method = typeof(ClaimsController).GetMethod(nameof(ClaimsController.SubmitClaim))!;
        var obsolete = method.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.Contains("/api/v1/claims", obsolete!.Message ?? string.Empty);
        Assert.Contains("5.13", obsolete.Message ?? string.Empty);
    }

    [Fact]
    public async Task LegacyPost_OnSuccess_ReturnsDeprecationHeaders()
    {
        var claim = BuildLegacyClaim();
        StubServiceSuccess();

        var response = await _client.PostAsJsonAsync("/api/claims", claim);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.True(response.Headers.TryGetValues("Deprecation", out var dep));
        Assert.Equal("true", dep!.First());

        Assert.True(response.Headers.TryGetValues("Link", out var link));
        Assert.Contains("/api/v1/claims", link!.First());
        Assert.Contains("rel=\"successor-version\"", link.First());

        Assert.False(response.Headers.Contains("Sunset"),
            "Sunset header should be omitted until capability 5.13 schedules removal");
    }

    [Fact]
    public async Task LegacyPost_OnFailure_StillReturnsDeprecationHeaders()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ClaimSubmissionResult.ValidationFailed(new[]
            {
                new ValidationError { Field = "MemberId", Code = "Required", Message = "MemberId is required" }
            }));

        var response = await _client.PostAsJsonAsync("/api/claims", BuildLegacyClaim());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("Deprecation"));
        Assert.True(response.Headers.Contains("Link"));
    }

    [Fact]
    public async Task LegacyPost_RoutesThroughSubmissionService_WithMappedAdapterClaim()
    {
        AdapterClaim? captured = null;
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<AdapterClaim>();
                captured.Id = "round-trip-id";
                captured.ClaimVersionId = "round-trip-id";
                captured.VersionNumber = 1;
                captured.VersionState = ClaimVersionState.Submitted;
                return ClaimSubmissionResult.Ok(captured);
            });

        var claim = BuildLegacyClaim();
        var response = await _client.PostAsJsonAsync("/api/claims", claim);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal(claim.MemberId, captured!.MemberId);
        Assert.Equal(claim.BillingProviderNPI, captured.BillingProviderNPI);
        Assert.Equal(claim.ClaimLines.Count, captured.ClaimLines.Count);
    }

    [Fact]
    public async Task LegacyPost_PreservesResponseShape_DomainClaim()
    {
        StubServiceSuccess();

        var response = await _client.PostAsJsonAsync("/api/claims", BuildLegacyClaim());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Round-trip: response body must deserialize as the legacy domain
        // Claim shape (NOT AdapterClaim) — that's the contract pre-existing
        // callers depend on.
        var domain = await response.Content.ReadFromJsonAsync<Claim>();
        Assert.NotNull(domain);
        Assert.Equal("round-trip-id", domain!.Id);
        Assert.Equal(ClaimStatus.Submitted, domain.Status);
        Assert.Equal(ClaimVersionState.Submitted, domain.VersionState);
        Assert.Equal(1, domain.VersionNumber);
    }

    private void StubServiceSuccess()
    {
        _service
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<AdapterClaim>();
                c.Id = "round-trip-id";
                c.ClaimVersionId = "round-trip-id";
                c.VersionNumber = 1;
                c.VersionState = ClaimVersionState.Submitted;
                c.Status = ClaimStatus.Submitted;
                return ClaimSubmissionResult.Ok(c);
            });
    }

    private static Claim BuildLegacyClaim()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        return new Claim
        {
            TenantId = "test-tenant",
            ClaimNumber = "CLM-LEGACY-001",
            MemberId = "MEM-LEGACY",
            BillingProviderNPI = "1234567890",
            LineOfBusiness = LineOfBusiness.Commercial,
            ClaimType = ClaimType.Professional,
            PlaceOfServiceCode = "11",
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            ClaimLines = new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ChargeAmount = 150m,
                    Units = 1,
                    ServiceDateFrom = serviceDate,
                    ServiceDateTo = serviceDate,
                }
            }
        };
    }
}
