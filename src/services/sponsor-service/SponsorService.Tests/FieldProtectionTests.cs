using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace SponsorService.Tests;

/// <summary>The shared field protector (CloudHealthOffice.FieldProtection) and its key-ring selection.</summary>
public class FieldProtectionTests
{
    private sealed class Env : IHostEnvironment
    {
        public Env(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static (FieldProtectionKeyRing Ring, IServiceProvider Services) Build(string environment, Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var ring = services.AddChoFieldProtection(configuration, new Env(environment), "sponsor-service");
        return (ring, services.BuildServiceProvider());
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "cho-fp-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Protect_RoundTrips_WithAPrefix_AndNoPlaintext()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "sponsor-service");

        var stored = protector.Protect("000123456789");

        stored.Should().StartWith("enc:v1:").And.NotContain("000123456789");
        protector.IsProtected(stored).Should().BeTrue();
        protector.Unprotect(stored).Should().Be("000123456789");
        protector.Protect(stored).Should().Be(stored, "protecting twice changes nothing");
        protector.Protect(null).Should().BeNull();
        protector.Protect("").Should().Be("");
    }

    [Fact]
    public void LegacyPlaintext_IsReturnedAsIs()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "sponsor-service");

        protector.IsProtected("987654321012").Should().BeFalse();
        protector.Unprotect("987654321012").Should().Be("987654321012");
    }

    [Fact]
    public void AnotherPurpose_OrATamperedValue_CannotBeRead()
    {
        var provider = new EphemeralDataProtectionProvider();
        var stored = new DataProtectionFieldProtector(provider, "sponsor-service").Protect("000123456789")!;

        var other = new DataProtectionFieldProtector(provider, "provider-service");
        other.Invoking(p => p.Unprotect(stored)).Should().Throw<FieldProtectionException>()
            .Which.Message.Should().NotContain("000123456789");

        var mine = new DataProtectionFieldProtector(provider, "sponsor-service");
        mine.Invoking(p => p.Unprotect(stored[..^4] + "AAAA")).Should().Throw<FieldProtectionException>();
    }

    [Fact]
    public void Development_UsesALocalKeyRing_SharedByInstancesWithTheSameDirectory()
    {
        var dir = TempDir();
        try
        {
            var config = new Dictionary<string, string?> { ["FieldProtection:KeyRing:LocalDirectory"] = dir };
            var (ring, first) = Build("Development", config);
            ring.Should().Be(FieldProtectionKeyRing.LocalDirectory);
            var stored = first.GetRequiredService<IFieldProtector>().Protect("000123456789");

            // A second pod (process) with the same key ring reads it.
            var (_, second) = Build("Development", config);
            second.GetRequiredService<IFieldProtector>().Unprotect(stored).Should().Be("000123456789");
            Directory.GetFiles(dir, "key-*.xml").Should().NotBeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Production_WithoutAKeyRing_RefusesToWriteProtectedFields()
    {
        var (ring, services) = Build("Production", new Dictionary<string, string?>());

        ring.Should().Be(FieldProtectionKeyRing.None);
        var protector = services.GetRequiredService<IFieldProtector>();
        protector.Should().BeOfType<UnconfiguredFieldProtector>();
        protector.Invoking(p => p.Protect("000123456789")).Should().Throw<FieldProtectionException>();
        protector.Unprotect("legacy-plaintext").Should().Be("legacy-plaintext");
        protector.Invoking(p => p.Unprotect("enc:v1:abc")).Should().Throw<FieldProtectionException>();
    }

    [Theory]
    [InlineData("https://acct.blob.core.windows.net/dp/sponsor/keys.xml", null)]
    [InlineData(null, "https://vault.vault.azure.net/keys/sponsor-dp")]
    public void HalfAKeyRingConfiguration_FailsAtStartup(string? blob, string? key)
    {
        var act = () => Build("Production", new Dictionary<string, string?>
        {
            ["FieldProtection:KeyRing:BlobUri"] = blob,
            ["FieldProtection:KeyRing:KeyVaultKeyId"] = key,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*BlobUri and KeyVaultKeyId*");
    }

    [Fact]
    public void BlobAndKeyVault_AreConfiguredWhenBothAreGiven()
    {
        var (ring, services) = Build("Production", new Dictionary<string, string?>
        {
            ["FieldProtection:KeyRing:BlobUri"] = "https://acct.blob.core.windows.net/dp/sponsor/keys.xml",
            ["FieldProtection:KeyRing:KeyVaultKeyId"] = "https://vault.vault.azure.net/keys/sponsor-dp",
        });

        ring.Should().Be(FieldProtectionKeyRing.AzureBlobWithKeyVault);
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>>().Value;
        options.XmlRepository!.GetType().Name.Should().Contain("Blob");
        options.XmlEncryptor!.GetType().Name.Should().Contain("KeyVault");
        services.GetRequiredService<IFieldProtector>().Should().BeOfType<DataProtectionFieldProtector>();
    }
}
