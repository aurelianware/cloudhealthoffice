using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    private static readonly FieldProtectionContext Here = new("tenant-a", "record-1", "accountNumber");

    [Fact]
    public void WithAContext_IsBound_AndRoundTrips()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "sponsor-service");

        var stored = protector.Protect("000123456789", Here);

        stored.Should().StartWith("enc:v2:").And.NotContain("000123456789");
        protector.IsProtected(stored).Should().BeTrue();
        FieldCiphertext.IsBound(stored).Should().BeTrue();
        protector.Unprotect(stored, Here).Should().Be("000123456789");
        protector.Protect(stored, Here).Should().Be(stored, "protecting twice changes nothing");
        protector.Protect(null, Here).Should().BeNull();
        protector.Protect("", Here).Should().Be("");
    }

    [Theory]
    [InlineData("tenant-b", "record-1", "accountNumber")]   // another tenant
    [InlineData("tenant-a", "record-2", "accountNumber")]   // another record
    [InlineData("tenant-a", "record-1", "routingNumber")]   // another field
    [InlineData("", "record-1", "accountNumber")]           // no tenant
    public void ABoundValue_CopiedElsewhere_DoesNotDecrypt(string tenant, string record, string field)
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "sponsor-service");
        var stored = protector.Protect("000123456789", Here);

        protector.Invoking(p => p.Unprotect(stored, new FieldProtectionContext(tenant, record, field)))
            .Should().Throw<FieldProtectionException>().Which.Message.Should().NotContain("000123456789");
    }

    [Fact]
    public void ABoundValue_NeedsItsContext()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "sponsor-service");
        var stored = protector.Protect("000123456789", Here);

        protector.Invoking(p => p.Unprotect(stored)).Should().Throw<FieldProtectionException>();
    }

    [Fact]
    public void AnUnboundV1Value_StillReadsThroughTheContextOverload()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "sponsor-service");
        var v1 = protector.Protect("000123456789");

        v1.Should().StartWith("enc:v1:");
        protector.Unprotect(v1, Here).Should().Be("000123456789");
    }

    [Fact]
    public void RejectPlaintext_RefusesLegacyPlaintext_ButNotEmptyOrCiphertext()
    {
        var keys = new EphemeralDataProtectionProvider();
        var protector = new DataProtectionFieldProtector(keys, "sponsor-service", rejectPlaintext: true);

        protector.Invoking(p => p.Unprotect("987654321012", Here)).Should().Throw<FieldProtectionException>()
            .Which.Message.Should().Contain("RejectPlaintext").And.NotContain("987654321012");
        protector.Invoking(p => p.Unprotect("987654321012")).Should().Throw<FieldProtectionException>();
        protector.Unprotect(null, Here).Should().BeNull();
        protector.Unprotect("", Here).Should().Be("");
        protector.Unprotect(protector.Protect("000123456789", Here), Here).Should().Be("000123456789");
        protector.Unprotect(protector.Protect("000123456789")).Should().Be("000123456789");

        new UnconfiguredFieldProtector(rejectPlaintext: true).Invoking(p => p.Unprotect("987654321012", Here))
            .Should().Throw<FieldProtectionException>();
    }

    [Fact]
    public void RejectPlaintext_IsReadFromConfiguration_AndOffByDefault()
    {
        var dir = TempDir();
        try
        {
            var (_, off) = Build("Development", new Dictionary<string, string?> { ["FieldProtection:KeyRing:LocalDirectory"] = dir });
            off.GetRequiredService<IFieldProtector>().Unprotect("987654321012", Here).Should().Be("987654321012");

            var (_, on) = Build("Development", new Dictionary<string, string?>
            {
                ["FieldProtection:KeyRing:LocalDirectory"] = dir,
                ["FieldProtection:RejectPlaintext"] = "true",
            });
            on.GetRequiredService<IFieldProtector>().Invoking(p => p.Unprotect("987654321012", Here))
                .Should().Throw<FieldProtectionException>();

            var (_, unconfigured) = Build("Production", new Dictionary<string, string?> { ["FieldProtection:RejectPlaintext"] = "true" });
            unconfigured.GetRequiredService<IFieldProtector>().Invoking(p => p.Unprotect("987654321012"))
                .Should().Throw<FieldProtectionException>();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
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
    // ── RejectUnbound: enc:v1 record fields ─────────────────────────────

    [Fact]
    public void RejectUnbound_RefusesV1ThroughTheContextOverload_ButNotBoundOrContextFreeOrPlaintext()
    {
        var keys = new EphemeralDataProtectionProvider();
        var lenient = new DataProtectionFieldProtector(keys, "sponsor-service");
        var strict = new DataProtectionFieldProtector(keys, "sponsor-service", rejectUnbound: true);
        var v1 = lenient.Protect("000123456789");

        strict.Invoking(p => p.Unprotect(v1, Here)).Should().Throw<FieldProtectionException>()
            .Which.Message.Should().Contain("RejectUnbound").And.NotContain("000123456789");

        strict.Unprotect(strict.Protect("000123456789", Here), Here).Should().Be("000123456789");
        strict.Unprotect(v1).Should().Be("000123456789", "the context-free overload's own format (held files, migrations)");
        strict.Unprotect("987654321012", Here).Should().Be("987654321012", "plaintext is RejectPlaintext's business");
        strict.Unprotect(null, Here).Should().BeNull();
        lenient.Unprotect(v1, Here).Should().Be("000123456789", "off by default");
    }

    [Fact]
    public void RejectUnbound_IsReadFromConfiguration_AndOffByDefault()
    {
        var dir = TempDir();
        try
        {
            var config = new Dictionary<string, string?> { ["FieldProtection:KeyRing:LocalDirectory"] = dir };
            var (_, off) = Build("Development", config);
            var v1 = off.GetRequiredService<IFieldProtector>().Protect("000123456789");
            off.GetRequiredService<IFieldProtector>().Unprotect(v1, Here).Should().Be("000123456789");

            var (_, on) = Build("Development", new Dictionary<string, string?>(config) { ["FieldProtection:RejectUnbound"] = "true" });
            on.GetRequiredService<IFieldProtector>().Invoking(p => p.Unprotect(v1, Here))
                .Should().Throw<FieldProtectionException>();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ── RejectPlaintext off: a startup warning outside Development ──────

    private sealed class Capture : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
        public void Dispose() { }
    }

    private static async Task<List<string>> StartupWarningsAsync(string environment, Dictionary<string, string?> config)
    {
        var capture = new Capture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(capture));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        services.AddChoFieldProtection(configuration, new Env(environment), "sponsor-service");
        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
            await hosted.StartAsync(CancellationToken.None);
        return capture.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("RejectPlaintext"))
            .Select(e => e.Message).ToList();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task RejectPlaintextOff_OutsideDevelopment_LogsAStartupWarning(string environment)
    {
        var warnings = await StartupWarningsAsync(environment, new Dictionary<string, string?>());

        warnings.Should().ContainSingle().Which.Should().Contain("RejectPlaintext is off").And.Contain("sponsor-service");
    }

    [Fact]
    public async Task RejectPlaintextOn_OrDevelopment_LogsNoWarning()
    {
        (await StartupWarningsAsync("Production", new Dictionary<string, string?> { ["FieldProtection:RejectPlaintext"] = "true" }))
            .Should().BeEmpty();

        var dir = TempDir();
        try
        {
            (await StartupWarningsAsync("Development", new Dictionary<string, string?> { ["FieldProtection:KeyRing:LocalDirectory"] = dir }))
                .Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
