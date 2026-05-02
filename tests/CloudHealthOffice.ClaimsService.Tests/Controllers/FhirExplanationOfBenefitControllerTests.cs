using System.Net;
using System.Text.Json;
using ClaimsService.Models;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// Integration tests for the canonical FHIR R4 ExplanationOfBenefit
/// surface in claims-service (capability 5.11). Uses the shared
/// <see cref="ClaimsApiFactory"/> — the same fixture that backs the
/// v1 member-search tests — so the projector + repository wiring
/// matches the production DI graph. The repository is the substitute
/// the factory exposes; tests configure it per scenario.
/// </summary>
public class FhirExplanationOfBenefitControllerTests : IClassFixture<ClaimsApiFactory>
{
    private readonly ClaimsApiFactory _factory;
    private readonly HttpClient _client;

    public FhirExplanationOfBenefitControllerTests(ClaimsApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");

        // Reset both Returns configurations and received calls between
        // tests since IClassFixture shares the factory across the class.
        _factory.ClaimRepository.ClearSubstitute();
        _factory.ClaimRepository.GetLatestVersionAsync(default!, default)
            .ReturnsForAnyArgs((Claim?)null);
        _factory.ClaimRepository.SearchForMemberAsync(
                default!, default, default, default, default,
                default, default, default, default, default)
            .ReturnsForAnyArgs(((IReadOnlyList<Claim>)Array.Empty<Claim>(), 0));
    }

    // ── Read by id ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadEob_returns_200_with_FHIR_EOB_when_claim_found()
    {
        var claim = BuildClaim("CHAIN-1", "MEM-9", "CLM-200");
        _factory.ClaimRepository
            .GetLatestVersionAsync("CHAIN-1", Arg.Any<DateTime>())
            .Returns(claim);

        var response = await _client.GetAsync("/fhir/ExplanationOfBenefit/CHAIN-1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("resourceType").GetString().Should().Be("ExplanationOfBenefit");
        doc.RootElement.GetProperty("id").GetString().Should().Be("CHAIN-1");
        doc.RootElement.GetProperty("identifier")[0].GetProperty("value").GetString()
            .Should().Be("CLM-200");
    }

    [Fact]
    public async Task ReadEob_returns_404_OperationOutcome_when_not_found()
    {
        _factory.ClaimRepository
            .GetLatestVersionAsync("missing", Arg.Any<DateTime>())
            .Returns((Claim?)null);

        var response = await _client.GetAsync("/fhir/ExplanationOfBenefit/missing");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/fhir+json");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"resourceType\":\"OperationOutcome\"");
        body.Should().Contain("not-found");
    }

    // ── Search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchEobs_without_patient_or_id_returns_400_OperationOutcome()
    {
        var response = await _client.GetAsync("/fhir/ExplanationOfBenefit");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("patient or _id search parameter");
    }

    [Fact]
    public async Task SearchEobs_by_patient_returns_searchset_Bundle_with_total_and_entry()
    {
        var claims = new List<Claim>
        {
            BuildClaim("c1", "MEM-7", "CLM-1"),
            BuildClaim("c2", "MEM-7", "CLM-2"),
        };
        _factory.ClaimRepository
            .SearchForMemberAsync(
                "MEM-7", null, null, null, null, null, null, null,
                Arg.Any<int>(), Arg.Any<int>())
            .Returns(((IReadOnlyList<Claim>)claims, 2));

        var response = await _client.GetAsync("/fhir/ExplanationOfBenefit?patient=MEM-7&_count=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("resourceType").GetString().Should().Be("Bundle");
        doc.RootElement.GetProperty("type").GetString().Should().Be("searchset");
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("entry").GetArrayLength().Should().Be(2);

        // First entry has search.mode=match and a projected EOB resource.
        var first = doc.RootElement.GetProperty("entry")[0];
        first.GetProperty("search").GetProperty("mode").GetString().Should().Be("match");
        first.GetProperty("resource").GetProperty("resourceType").GetString()
            .Should().Be("ExplanationOfBenefit");
    }

    [Fact]
    public async Task SearchEobs_clamps_count_above_max_to_200()
    {
        await _client.GetAsync("/fhir/ExplanationOfBenefit?patient=MEM-7&_count=9999");

        await _factory.ClaimRepository.Received(1).SearchForMemberAsync(
            "MEM-7", null, null, null, null, null, null, null,
            Arg.Any<int>(), Arg.Is<int>(p => p == 200));
    }

    [Fact]
    public async Task SearchEobs_clamps_count_below_one_to_one()
    {
        await _client.GetAsync("/fhir/ExplanationOfBenefit?patient=MEM-7&_count=0");

        await _factory.ClaimRepository.Received(1).SearchForMemberAsync(
            "MEM-7", null, null, null, null, null, null, null,
            Arg.Any<int>(), Arg.Is<int>(p => p == 1));
    }

    [Fact]
    public async Task SearchEobs_defaults_count_to_50_and_page_to_1_when_omitted()
    {
        await _client.GetAsync("/fhir/ExplanationOfBenefit?patient=MEM-7");

        await _factory.ClaimRepository.Received(1).SearchForMemberAsync(
            "MEM-7", null, null, null, null, null, null, null,
            Arg.Is<int>(p => p == 1),
            Arg.Is<int>(p => p == 50));
    }

    [Fact]
    public async Task SearchEobs_strips_FHIR_typed_reference_from_patient_param()
    {
        // Callers may send `patient=Patient/MEM-7`; the controller must
        // strip the prefix before reading from the repo (which stores
        // raw member ids) and before comparing against an _id-resolved
        // claim's MemberId.
        await _client.GetAsync("/fhir/ExplanationOfBenefit?patient=Patient/MEM-7");

        await _factory.ClaimRepository.Received(1).SearchForMemberAsync(
            "MEM-7", null, null, null, null, null, null, null,
            Arg.Any<int>(), Arg.Any<int>());
    }

    [Fact]
    public async Task SearchEobs_by_id_returns_single_entry_when_found()
    {
        _factory.ClaimRepository
            .GetLatestVersionAsync("CHAIN-2", Arg.Any<DateTime>())
            .Returns(BuildClaim("CHAIN-2", "MEM-7", "CLM-X"));

        var response = await _client.GetAsync("/fhir/ExplanationOfBenefit?_id=CHAIN-2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("entry").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task SearchEobs_by_id_returns_empty_bundle_when_id_resolves_to_different_patient()
    {
        _factory.ClaimRepository
            .GetLatestVersionAsync("CHAIN-3", Arg.Any<DateTime>())
            .Returns(BuildClaim("CHAIN-3", "OTHER-MEMBER", "CLM-Y"));

        var response = await _client.GetAsync(
            "/fhir/ExplanationOfBenefit?_id=CHAIN-3&patient=MEM-7");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("total").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("entry").GetArrayLength().Should().Be(0);
    }

    private static Claim BuildClaim(string chainId, string memberId, string claimNumber) => new()
    {
        Id = chainId,
        ClaimVersionId = chainId,
        TenantId = "test-tenant",
        ClaimNumber = claimNumber,
        MemberId = memberId,
        BillingProviderNPI = "1234567890",
        BillingProviderName = "Test Provider",
        ClaimType = ClaimType.Professional,
        Status = ClaimStatus.Approved,
        SubmittedDate = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateFrom = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 100m,
        LineOfBusiness = LineOfBusiness.Commercial,
        PlaceOfServiceCode = "11",
        LastUpdatedDate = new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc),
    };
}
