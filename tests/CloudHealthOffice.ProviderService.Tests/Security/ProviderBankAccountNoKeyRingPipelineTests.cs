using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// A deployment without a key ring (outside Development/Testing,
/// AddChoFieldProtection registers <see cref="UnconfiguredFieldProtector"/>):
/// bank numbers are never stored, in plaintext or with pod-local keys; the
/// write answers 503 before anything is written.
/// </summary>
public class ProviderBankAccountNoKeyRingPipelineTests : IClassFixture<ProviderBankAccountNoKeyRingPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public Mock<IProviderRepository> Providers { get; } = new();
        public Mock<IProviderVersioningService> Versioning { get; } = new();
        public InMemoryProviderBankAccountRepository BankAccounts { get; set; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Services:TenantService", "http://tenant-service.cloudhealthoffice/api/v1");
            builder.UseSetting("ProviderVerification:BaseUrl", "http://provider-verification-service");
            builder.ConfigureServices(services =>
            {
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType?.Namespace?.StartsWith("ProviderService") == true)
                             .ToList())
                {
                    services.Remove(hosted);
                }

                // What AddChoFieldProtection registers outside Development without a key ring.
                services.RemoveAll<IFieldProtector>();
                services.AddSingleton<IFieldProtector, UnconfiguredFieldProtector>();
                services.RemoveAll<IProviderRepository>();
                services.AddSingleton(_ => Providers.Object);
                services.RemoveAll<IProviderVersioningService>();
                services.AddSingleton(_ => Versioning.Object);
                services.RemoveAll<IProviderBankAccountRepository>();
                services.AddScoped<IProviderBankAccountRepository>(sp => new ProtectedProviderBankAccountRepository(
                    BankAccounts, sp.GetRequiredService<IFieldProtector>(),
                    sp.GetRequiredService<ILogger<ProtectedProviderBankAccountRepository>>()));
            });
        }
    }

    private const string Tenant = "tenant-1";
    private const string Npi = "1234567890";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Factory _factory;

    public ProviderBankAccountNoKeyRingPipelineTests(Factory factory)
    {
        _factory = factory;
        _factory.Providers.Reset();
        _factory.Versioning.Reset();
        _factory.BankAccounts = new InMemoryProviderBankAccountRepository();
        _factory.Providers.Setup(r => r.GetByNPIAsync(Npi)).ReturnsAsync(() => new Provider
        {
            Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi,
            VersionId = "v-1", VersionState = ProviderVersionState.Active,
        });
    }

    private HttpClient As(string role)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1", role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static object Account => new
    {
        eftEnabled = true, preferredDisbursementMethod = "NachaCredit", routingNumber = "021000089", accountNumber = "999988887777"
    };

    [Fact]
    public async Task Proposing_a_bank_account_is_503_and_nothing_is_stored()
    {
        var response = await As(ChoRolePermissions.ProviderRelations).PutAsJsonAsync($"/api/v1/providers/npi/{Npi}/bank-account", Account, Json);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("FieldProtection:KeyRing");
        _factory.BankAccounts.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Creating_a_provider_with_a_bank_account_is_503_before_anything_is_written()
    {
        var response = await As(ChoRolePermissions.ProviderRelations).PostAsJsonAsync("/api/v1/providers", new
        {
            npi = "2222222222", providerType = "Individual", firstName = "Jane", lastName = "Doe",
            taxonomyCode = "207R00000X", primarySpecialty = "Internal Medicine",
            bankAccount = Account,
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
        _factory.Versioning.Verify(v => v.CreateDraftAsync(It.IsAny<Provider>(), It.IsAny<string>()), Times.Never);
        _factory.BankAccounts.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task The_full_read_of_an_account_without_encrypted_values_still_works()
    {
        // An approved account stored before encryption (plaintext in the
        // store, approved through dual control): readable without a key ring.
        await ApprovedBankAccounts.SeedAsync(_factory.BankAccounts,
            new Provider { Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi },
            new ProviderBankAccount { EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "111122223333" });
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("capitation-service", Tenant));

        var response = await client.GetAsync($"/api/v1/internal/providers/npi/{Npi}/bank-account");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accountNumber").GetString()
            .Should().Be("111122223333");
    }
}
