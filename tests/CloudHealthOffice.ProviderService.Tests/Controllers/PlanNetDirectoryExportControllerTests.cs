using System.Text.Json.Nodes;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// G-36 — the export feed fhir-service reads to build a public Plan-Net
/// directory: the payer is required, only active networks are listed, the
/// providers feed pages with a relative next link, and a caller without a
/// tenant gets nothing.
/// </summary>
public class PlanNetDirectoryExportControllerTests
{
    private const string TenantId = "tenant-a";
    private const string NetworkId = "net-gold";

    private readonly InMemoryProviderRepository _providerRepo = new() { TenantId = TenantId };
    private readonly InMemoryOrganizationRepository _orgRepo = new() { TenantId = TenantId };
    private readonly PlanNetDirectoryExportController _controller;

    public PlanNetDirectoryExportControllerTests()
    {
        _controller = new PlanNetDirectoryExportController(
            _providerRepo, _orgRepo,
            new PlanNetDirectoryProjector(
                new FhirPractitionerProjector(), new FhirPractitionerRoleProjector(), new FhirOrganizationProjector()));
        SetTenant(TenantId);
    }

    private void SetTenant(string? tenantId)
    {
        var ctx = new DefaultHttpContext();
        if (tenantId != null) ctx.Items["TenantId"] = tenantId;
        _controller.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    private async Task AddNetworkAsync(string id, OrganizationVersionState state = OrganizationVersionState.Active)
    {
        var draft = await _orgRepo.CreateDraftAsync(new Organization
        {
            TenantId = TenantId,
            Id = id,
            OrganizationId = id,
            VersionId = id,
            VersionNumber = 1,
            VersionState = OrganizationVersionState.Draft,
            Name = $"Network {id}",
            NetworkType = NetworkType.HMO,
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Identifiers = [new OrganizationIdentifier { System = "urn:cho:network", Value = id }],
            LastUpdatedDate = DateTime.UtcNow,
        });
        draft.VersionState = state;
        await _orgRepo.ActivateAndSupersedeAsync(draft, null);
    }

    private Task AddPractitionerAsync(string npi) => _providerRepo.CreateAsync(new Provider
    {
        TenantId = TenantId,
        Id = $"v-{npi}",
        ProviderId = $"p-{npi}",
        VersionId = $"v-{npi}",
        VersionNumber = 1,
        VersionState = ProviderVersionState.Active,
        Status = ProviderStatus.Active,
        NPI = npi,
        ProviderType = ProviderType.Individual,
        FirstName = "Pat",
        LastName = $"Doe{npi[^2..]}",
        TaxonomyCode = "207R00000X",
        Address = "5 Elm St",
        City = "Hartford",
        State = "CT",
        ZipCode = "06103",
        NetworkParticipations =
        [
            new NetworkParticipation
            {
                NetworkId = NetworkId,
                LineOfBusiness = LineOfBusiness.Commercial,
                EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        ],
        LastUpdatedDate = DateTime.UtcNow,
    });

    private static JsonObject Bundle(IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.StatusCode.Should().Be(200);
        content.ContentType.Should().Be("application/fhir+json");
        var bundle = JsonNode.Parse(content.Content!)!.AsObject();
        bundle["type"]!.GetValue<string>().Should().Be("collection");
        return bundle;
    }

    private static IEnumerable<JsonObject> Resources(JsonObject bundle)
        => bundle["entry"]!.AsArray().Select(e => e!["resource"]!.AsObject());

    private static string? Next(JsonObject bundle)
        => bundle["link"]?.AsArray().SingleOrDefault(l => l!["relation"]!.GetValue<string>() == "next")?["url"]?.GetValue<string>();

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Both_feeds_require_the_payer(string? payer)
    {
        (await _controller.Networks(payer, default)).Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
        (await _controller.Providers(payer)).Should().BeOfType<ContentResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Networks_lists_active_networks_managed_by_the_payer()
    {
        await AddNetworkAsync(NetworkId);
        await AddNetworkAsync("net-closed", OrganizationVersionState.Suspended);

        var networks = Resources(Bundle(await _controller.Networks("payer", default))).ToList();

        networks.Select(n => n["id"]!.GetValue<string>()).Should().Equal(NetworkId);
        networks[0]["partOf"]!["reference"]!.GetValue<string>().Should().Be("Organization/payer");
    }

    [Fact]
    public async Task Providers_pages_with_a_relative_next_link_until_a_short_page()
    {
        await AddNetworkAsync(NetworkId);
        await AddPractitionerAsync("1234567893");
        await AddPractitionerAsync("1111111111");
        await AddPractitionerAsync("1245319599");

        var first = Bundle(await _controller.Providers("payer", skip: 0, count: 2));
        var second = Bundle(await _controller.Providers("payer", skip: 2, count: 2));

        Next(first).Should().Be("providers?payer=payer&skip=2&count=2");
        Next(second).Should().BeNull();
        var practitioners = Resources(first).Concat(Resources(second))
            .Where(r => r["resourceType"]!.GetValue<string>() == "Practitioner")
            .Select(r => r["id"]!.GetValue<string>());
        practitioners.Should().BeEquivalentTo("1234567893", "1111111111", "1245319599");
    }

    [Fact]
    public async Task Providers_carry_their_roles_in_published_networks()
    {
        await AddNetworkAsync(NetworkId);
        await AddPractitionerAsync("1234567893");

        var types = Resources(Bundle(await _controller.Providers("payer")))
            .Select(r => r["resourceType"]!.GetValue<string>());

        types.Should().BeEquivalentTo("Practitioner", "Location", "PractitionerRole");
    }

    [Fact]
    public async Task A_caller_without_a_tenant_gets_an_empty_feed()
    {
        await AddNetworkAsync(NetworkId);
        await AddPractitionerAsync("1234567893");
        SetTenant(null);

        var bundle = Bundle(await _controller.Providers("payer"));

        bundle["entry"]!.AsArray().Should().BeEmpty();
        Next(bundle).Should().BeNull();
    }
}
