using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProviderService.Controllers;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>Program.cs puts the encryption decorators around the real Mongo stores.</summary>
public class ProviderBankAccountEncryptionWiringTests : IClassFixture<ProviderBankAccountEncryptionWiringTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cho-providerbank-wiring-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("FieldProtection:KeyRing:LocalDirectory", KeyDirectory);
            builder.ConfigureServices(services =>
            {
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)
                                                           && d.ImplementationType?.Namespace?.StartsWith("ProviderService") == true).ToList())
                    services.Remove(hosted);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(KeyDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private readonly Factory _factory;

    public ProviderBankAccountEncryptionWiringTests(Factory factory) => _factory = factory;

    [Fact]
    public void Provider_rows_and_bank_account_records_are_stored_through_the_encryption_decorators()
    {
        using var scope = _factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IProviderRepository>().Should().BeOfType<ProtectedProviderRepository>();
        scope.ServiceProvider.GetRequiredService<IProviderBankAccountRepository>().Should().BeOfType<ProtectedProviderBankAccountRepository>();
        scope.ServiceProvider.GetRequiredService<IFieldProtector>().Should().BeOfType<DataProtectionFieldProtector>();
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "provider-service";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task The_migration_command_refuses_to_start_without_a_key_ring()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MongoDb:ConnectionString"] = "mongodb://fake-host:27017" })
            .Build();

        var exit = await global::ProviderService.Migrations.EncryptProviderBankAccounts.RunAsync(
            new[] { global::ProviderService.Migrations.EncryptProviderBankAccounts.Switch }, configuration, new Env("Production"));

        exit.Should().Be(1);
    }
}
