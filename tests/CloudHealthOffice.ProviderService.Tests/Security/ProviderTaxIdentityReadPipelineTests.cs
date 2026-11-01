using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Moq;
using ProviderService.Models;
using Xunit;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// The service-only tax identity read payment-service uses to total 1099-MISC
/// box 6 by TIN: payment-service's token gets the TIN and legal name; users
/// and other services are refused; the TIN is not logged.
/// </summary>
public class ProviderTaxIdentityReadPipelineTests : IClassFixture<ProviderPayeeAccountReadPipelineTests.Factory>
{
    private const string Tenant = "tenant-1";
    private const string Npi = "1234567890";
    private const string Path = "/api/v1/internal/providers/npi/" + Npi + "/tax-identity";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ProviderPayeeAccountReadPipelineTests.Factory _factory;

    public ProviderTaxIdentityReadPipelineTests(ProviderPayeeAccountReadPipelineTests.Factory factory)
    {
        _factory = factory;
        _factory.Providers.Reset();
        _factory.Logs.Clear();
        _factory.Providers.Setup(r => r.GetByNPIAsync(Npi)).ReturnsAsync(new Provider
        {
            Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi,
            ProviderType = ProviderType.Organization, OrganizationName = "Gulf Coast Pediatrics PA",
            TaxId = "74-1234567",
            VersionId = "v-1", VersionState = ProviderVersionState.Active,
        });
    }

    private HttpClient Service(string clientId)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant));
        return client;
    }

    [Fact]
    public async Task PaymentService_ReadsTheTinAndLegalName_AndTheTinIsNotLogged()
    {
        var response = await Service("payment-service").GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        body.GetProperty("taxId").GetString().Should().Be("74-1234567");
        body.GetProperty("taxIdType").GetString().Should().Be("EIN");
        body.GetProperty("legalName").GetString().Should().Be("Gulf Coast Pediatrics PA");
        _factory.Logs.Messages.Should().NotContain(m => m.Contains("74-1234567") || m.Contains("741234567"));
    }

    [Theory]
    [InlineData("capitation-service")]
    [InlineData("claims-service")]
    public async Task OtherServices_AreRefused(string clientId)
    {
        (await Service(clientId).GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AUser_IsRefused_WhateverTheirRole()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("finance-1", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        (await client.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
