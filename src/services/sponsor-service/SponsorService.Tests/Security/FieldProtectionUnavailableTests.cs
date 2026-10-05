using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SponsorService.Models;
using SponsorService.Repositories;
using SponsorService.Tests.Fakes;

namespace SponsorService.Tests.Security;

/// <summary>
/// A deployment without a key ring (outside Development): bank numbers and
/// billing account numbers are never stored, encrypted with pod-local keys or
/// in plaintext; the write answers 503.
/// </summary>
public class FieldProtectionUnavailableTests : IClassFixture<FieldProtectionUnavailableTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public InMemorySponsorBankAccountRepository Accounts { get; } = new();
        public InMemorySponsorRepository Sponsors { get; } = new();
        public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cho-sponsorbank-unavail-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("FieldProtection:KeyRing:LocalDirectory", KeyDirectory);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["CosmosDb:Endpoint"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                // What AddChoFieldProtection registers outside Development without a key ring.
                services.RemoveAll<IFieldProtector>();
                services.AddSingleton<IFieldProtector, UnconfiguredFieldProtector>();
                services.RemoveAll<ISponsorBankAccountRepository>();
                services.AddSingleton<ISponsorBankAccountRepository>(Accounts);
                services.RemoveAll<ISponsorRepository>();
                services.AddScoped<ISponsorRepository>(sp => new ProtectedSponsorRepository(
                    Sponsors, sp.GetRequiredService<IFieldProtector>(), sp.GetRequiredService<ILogger<ProtectedSponsorRepository>>()));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(KeyDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Tenant = "tenant-1";
    private readonly Factory _factory;

    public FieldProtectionUnavailableTests(Factory factory)
    {
        _factory = factory;
        _factory.Accounts.Clear();
        _factory.Sponsors.Clear();
        _factory.Sponsors.Put(new Sponsor { Id = "s-1", TenantId = Tenant, GroupNumber = "GRP-100", EmployerName = "Acme Co" });
    }

    private HttpClient As(string role)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-1", role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task ProposingABankAccount_Is503_AndNothingIsStored()
    {
        var response = await As(ChoRolePermissions.Finance).PostAsJsonAsync("/api/v1/sponsors/GRP-100/bank-account-changes", new
        {
            eftEnabled = true, preferredMethod = "Nacha", routingNumber = "021000021", accountNumber = "000123456789"
        });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.Accounts.RawJson(Tenant, "GRP-100").Should().BeNull();
    }

    [Fact]
    public async Task ASponsorWithABillingAccountNumber_Is503_AndNothingIsStored()
    {
        var response = await As(ChoRolePermissions.EnrollmentSpecialist).PostAsJsonAsync("/api/v1/sponsors", new
        {
            groupNumber = "GRP-200", employerName = "Beta Co", effectiveDate = "2026-01-01T00:00:00Z",
            billingInfo = new { premiumAmount = 1m, billingAccountNumber = "BA-1234567" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _factory.Sponsors.RawByGroup(Tenant, "GRP-200").Should().BeNull();
    }

    [Fact]
    public async Task SponsorsWithoutProtectedFields_StillWork()
    {
        var response = await As(ChoRolePermissions.EnrollmentSpecialist).PostAsJsonAsync("/api/v1/sponsors", new
        {
            groupNumber = "GRP-300", employerName = "Gamma Co", effectiveDate = "2026-01-01T00:00:00Z",
            billingInfo = new { premiumAmount = 1m }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
