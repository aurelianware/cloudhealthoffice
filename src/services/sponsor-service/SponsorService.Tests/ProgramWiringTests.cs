using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SponsorService.Repositories;
using SponsorService.Services;

namespace SponsorService.Tests;

/// <summary>
/// Program.cs as deployed with MongoDB: sponsors go through the encrypting
/// repository, bank accounts through the Mongo repository and the dual-control
/// service. Nothing here connects to a database.
/// </summary>
public class ProgramWiringTests : IClassFixture<ProgramWiringTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cho-sponsorbank-wiring-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Read while Program.cs registers services, so it goes in as a host setting.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100");
            builder.UseSetting("FieldProtection:KeyRing:LocalDirectory", KeyDirectory);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(KeyDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private readonly Factory _factory;

    public ProgramWiringTests(Factory factory) => _factory = factory;

    [Fact]
    public void Sponsors_AreReadAndWrittenThroughTheEncryptingRepository()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<ISponsorRepository>().Should().BeOfType<ProtectedSponsorRepository>();
        sp.GetRequiredService<ISponsorBankAccountRepository>().Should().BeOfType<MongoSponsorBankAccountRepository>();
        sp.GetRequiredService<ISponsorBankAccountService>().Should().BeOfType<SponsorBankAccountService>();
        sp.GetRequiredService<IFieldProtector>().Should().BeOfType<DataProtectionFieldProtector>();
    }
}
