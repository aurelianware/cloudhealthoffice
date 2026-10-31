using ClaimsService.Import.Cpcds.Drop;
using ClaimsService.Models;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds.Drop;

/// <summary>
/// Which tenants' drops load: deployment configuration, unless the tenant
/// saved settings of its own, and nothing while the deployment's switch is off.
/// </summary>
public class CpcdsDropTenantsTests
{
    private readonly CpcdsDropOptions _options = new()
    {
        Enabled = true,
        Tenants = { ["configured"] = new CpcdsDropTenantOptions { SourceSystem = "qnxt" } },
    };

    private readonly InMemoryCpcdsDropTenantSettingsRepository _stored = new();

    private CpcdsDropTenants Tenants() => new(Options.Create(_options), _stored);

    private Task Save(string tenantId, bool enabled, string source = "facets")
        => _stored.SaveAsync(new CpcdsDropTenantSettings
        {
            TenantId = tenantId,
            Enabled = enabled,
            Settings = new CpcdsDropTenantOptions { SourceSystem = source },
        });

    [Fact]
    public async Task Configured_tenants_load_with_their_configuration()
    {
        (await Tenants().ListAsync()).Should().Equal("configured");
        (await Tenants().ResolveAsync("configured"))!.SourceSystem.Should().Be("qnxt");
        (await Tenants().DescribeAsync("configured")).Source.Should().Be(CpcdsDropSettingsSource.Configuration);
    }

    [Fact]
    public async Task A_tenant_that_saves_settings_loads_without_configuration()
    {
        await Save("signed-up", enabled: true);

        (await Tenants().ListAsync()).Should().Equal("configured", "signed-up");
        (await Tenants().ResolveAsync("signed-up"))!.SourceSystem.Should().Be("facets");
    }

    [Fact]
    public async Task Saved_settings_replace_configuration_including_to_turn_it_off()
    {
        await Save("configured", enabled: true, source: "healthrules");
        (await Tenants().ResolveAsync("configured"))!.SourceSystem.Should().Be("healthrules");

        await Save("configured", enabled: false);
        (await Tenants().ResolveAsync("configured")).Should().BeNull();
        (await Tenants().ListAsync()).Should().BeEmpty();

        var (settings, enabled, source) = await Tenants().DescribeAsync("configured");
        (settings, enabled, source).Should().Be((settings, false, CpcdsDropSettingsSource.Stored));
        settings.Should().NotBeNull("turning the drop off keeps what was saved");
    }

    [Fact]
    public async Task Nothing_loads_while_the_deployment_switch_is_off()
    {
        await Save("signed-up", enabled: true);
        _options.Enabled = false;

        (await Tenants().ListAsync()).Should().BeEmpty();
        (await Tenants().ResolveAsync("signed-up")).Should().BeNull();
        (await Tenants().ResolveAsync("configured")).Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_with_neither_has_no_drop()
    {
        (await Tenants().ResolveAsync("someone-else")).Should().BeNull();
        (await Tenants().DescribeAsync("someone-else")).Source.Should().Be(CpcdsDropSettingsSource.None);
    }

    [Fact]
    public async Task A_stored_tenant_id_that_cannot_name_a_folder_is_never_listed()
    {
        await Save("../escape", enabled: true);
        (await Tenants().ListAsync()).Should().NotContain("../escape");
    }

    [Fact]
    public void Settings_round_trip_through_whole_hours()
    {
        var request = new CpcdsDropSettingsRequest
        {
            Enabled = true,
            SourceSystem = "qnxt",
            LineOfBusiness = LineOfBusiness.Medicaid,
            ColumnMap = new() { ["Claim unique identifier"] = "CLM_ID" },
            ShadowAfterImport = true,
            ExpectedEveryHours = 168,
            StaleGraceHours = 12,
        };

        var options = request.ToOptions();
        options.ExpectedEvery.Should().Be(TimeSpan.FromDays(7));
        options.StaleGrace.Should().Be(TimeSpan.FromHours(12));

        CpcdsDropSettingsRequest.FromOptions(true, options).Should().BeEquivalentTo(request);
    }

    [Theory]
    [InlineData("qnxt core", 24, 6, "sourceSystem")]
    [InlineData("qnxt", 0, 6, "expectedEveryHours")]
    [InlineData("qnxt", 24 * 32, 6, "expectedEveryHours")]
    [InlineData("qnxt", 24, -1, "staleGraceHours")]
    [InlineData("qnxt", 24, 24 * 8, "staleGraceHours")]
    public void Invalid_settings_are_named(string source, int every, int grace, string field)
    {
        var errors = new CpcdsDropSettingsRequest
        {
            SourceSystem = source, ExpectedEveryHours = every, StaleGraceHours = grace,
        }.Validate();

        errors.Should().ContainSingle().Which.Should().StartWith(field);
    }

    [Fact]
    public void A_column_map_with_blank_or_huge_entries_is_refused()
    {
        new CpcdsDropSettingsRequest { ColumnMap = new() { ["Claim unique identifier"] = " " } }
            .Validate().Should().ContainSingle().Which.Should().StartWith("columnMap");
        new CpcdsDropSettingsRequest
        {
            ColumnMap = Enumerable.Range(0, 201).ToDictionary(i => $"c{i}", i => $"h{i}"),
        }.Validate().Should().ContainSingle().Which.Should().StartWith("columnMap");
    }
}
