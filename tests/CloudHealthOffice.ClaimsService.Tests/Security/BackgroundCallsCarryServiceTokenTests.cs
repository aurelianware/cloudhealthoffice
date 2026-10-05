using System.Net;
using ClaimsService.Adapters;
using ClaimsService.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.ClaimsService.Tests.Security;

/// <summary>
/// Calls claims-service makes to other CHO services with no inbound caller must
/// name their tenant, so the shared outbound handler mints a service token for
/// it. Without the tenant the call goes out with no token and the callee
/// answers 401.
/// </summary>
public class BackgroundCallsCarryServiceTokenTests
{
    private const string Tenant = "tenant-claims";
    private const string ClientId = "claims-service";

    [Fact]
    public async Task TenantPlatformLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddHttpClient(ClaimTenantConfigCache.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<ClaimTenantConfigCache>();
        }, new Dictionary<string, string?> { ["Services:TenantService"] = "http://tenant-service/api/v1" });

        await host.Services.GetRequiredService<ClaimTenantConfigCache>().GetAsync(Tenant);

        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task DiagnosisDescriptionLookup_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        using var host = new NoCallerHost(ClientId, (services, outbound) =>
        {
            services.AddMemoryCache();
            services.AddHttpClient(UpstreamClientNames.TerminologyService,
                    c => c.BaseAddress = new Uri("http://terminology-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddHttpClient(UpstreamClientNames.ReferenceDataService,
                    c => c.BaseAddress = new Uri("http://reference-data-service/"))
                .ConfigurePrimaryHttpMessageHandler(() => outbound);
            services.AddSingleton<IDiagnosisDescriptionLookup, DiagnosisDescriptionLookup>();
        });
        host.Outbound.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result":true,"display":"Some diagnosis"}""",
                System.Text.Encoding.UTF8, "application/json"),
        };

        var description = await host.Services.GetRequiredService<IDiagnosisDescriptionLookup>()
            .FindDescriptionAsync("ZZZ.999", Tenant);

        description.Should().Be("Some diagnosis");
        NoCallerHost.TokenOf(host.Outbound.Last).Should().Be((Tenant, ClientId));
    }

    [Fact]
    public async Task DiagnosisEnricher_PassesTheClaimsTenantToTheLookup()
    {
        var lookup = new RecordingLookup();
        var claim = new global::ClaimsService.Models.Claim { TenantId = Tenant };
        claim.DiagnosisCodes.Add(new global::ClaimsService.Models.DiagnosisCode { Code = "E11.9" });

        await new ClaimDiagnosisMetadataEnricher(lookup).EnrichAsync(claim);

        lookup.Tenants.Should().ContainSingle().Which.Should().Be(Tenant);
    }

    private sealed class RecordingLookup : IDiagnosisDescriptionLookup
    {
        public List<string?> Tenants { get; } = new();

        public Task<string?> FindDescriptionAsync(string? code, string? tenantId = null, CancellationToken ct = default)
        {
            Tenants.Add(tenantId);
            return Task.FromResult<string?>(null);
        }
    }
}
