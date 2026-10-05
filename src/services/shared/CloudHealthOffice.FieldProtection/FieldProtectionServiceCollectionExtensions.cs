using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FieldProtection;

/// <summary>Which key ring <see cref="FieldProtectionServiceCollectionExtensions.AddChoFieldProtection"/> chose.</summary>
public enum FieldProtectionKeyRing
{
    /// <summary>Azure Blob Storage, keys wrapped by a Key Vault key: shared by every pod.</summary>
    AzureBlobWithKeyVault,

    /// <summary>A local directory, keys unencrypted on disk: Development and Testing only.</summary>
    LocalDirectory,

    /// <summary>Nothing configured outside Development: protected fields cannot be written.</summary>
    None
}

/// <summary>
/// Configuration section <c>FieldProtection</c>:
/// <list type="bullet">
///   <item><c>ApplicationName</c>: the Data Protection application name. Every
///   pod of a service must use the same one (default: the name passed to
///   <c>AddChoFieldProtection</c>).</item>
///   <item><c>KeyRing:BlobUri</c>: the blob that holds the key ring, e.g.
///   <c>https://{account}.blob.core.windows.net/dataprotection/sponsor-service/keys.xml</c>.</item>
///   <item><c>KeyRing:KeyVaultKeyId</c>: the Key Vault key that wraps the
///   key ring, e.g. <c>https://{vault}.vault.azure.net/keys/sponsor-service-dp</c>.</item>
///   <item><c>KeyRing:ManagedIdentityClientId</c>: optional user-assigned
///   identity for both (otherwise <c>DefaultAzureCredential</c>'s chain).</item>
///   <item><c>KeyRing:LocalDirectory</c>: Development/Testing only; where the
///   local key ring lives.</item>
///   <item><c>RejectPlaintext</c>: when true, reading a protected field that
///   still holds a value stored before encryption fails
///   (<see cref="FieldProtectionException"/>) instead of returning it. Turn it
///   on once the service's <c>--encrypt-bank-accounts</c> command reports
///   nothing left in plaintext. Default false (turning it on before the
///   migration would break reads of existing rows). Outside Development, a
///   host with it off logs a startup warning.</item>
///   <item><c>RejectUnbound</c>: when true, reading a record field that is
///   still <c>enc:v1:</c> (encrypted before record binding) through a context
///   overload fails (<see cref="FieldProtectionException"/>), so a ciphertext
///   copied between records is never accepted. Turn it on, like
///   RejectPlaintext, once <c>--encrypt-bank-accounts</c> reports nothing left
///   to bind. Default false.</item>
/// </list>
/// </summary>
public static class FieldProtectionServiceCollectionExtensions
{
    public const string SectionName = "FieldProtection";

    /// <summary>
    /// Registers <see cref="IFieldProtector"/> for <paramref name="applicationName"/>
    /// on ASP.NET Core Data Protection, with the key ring persisted to Azure
    /// Blob Storage and protected by a Key Vault key
    /// (<c>PersistKeysToAzureBlobStorage</c> + <c>ProtectKeysWithAzureKeyVault</c>),
    /// so every pod shares the keys. In Development and Testing without that
    /// configuration, a local file key ring is used. Anywhere else without it,
    /// <see cref="UnconfiguredFieldProtector"/> is registered: writes of
    /// protected fields fail rather than store plaintext or pod-local keys.
    /// Half a configuration (blob without key, or key without blob) fails at
    /// startup.
    /// </summary>
    public static FieldProtectionKeyRing AddChoFieldProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string applicationName,
        TokenCredential? credential = null)
    {
        var section = configuration.GetSection(SectionName);
        var appName = section["ApplicationName"] is { Length: > 0 } configured ? configured : applicationName;
        var blobUri = section["KeyRing:BlobUri"];
        var keyId = section["KeyRing:KeyVaultKeyId"];
        var localDirectory = section["KeyRing:LocalDirectory"];
        var rejectPlaintext = section.GetValue("RejectPlaintext", false);
        var rejectUnbound = section.GetValue("RejectUnbound", false);

        if (!rejectPlaintext && !environment.IsDevelopment())
        {
            services.AddSingleton<IHostedService>(sp => new FieldProtectionStartupWarning(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger(FieldProtectionStartupWarning.Category), appName));
        }

        var hasBlob = !string.IsNullOrWhiteSpace(blobUri);
        var hasKey = !string.IsNullOrWhiteSpace(keyId);
        if (hasBlob != hasKey)
        {
            throw new InvalidOperationException(
                "FieldProtection:KeyRing needs both BlobUri and KeyVaultKeyId: a key ring in blob storage must be " +
                "protected by a Key Vault key, and a Key Vault key needs a shared key ring.");
        }

        FieldProtectionKeyRing ring;
        if (hasBlob)
        {
            credential ??= CredentialFrom(section["KeyRing:ManagedIdentityClientId"]);
            services.AddDataProtection()
                .SetApplicationName(appName)
                .PersistKeysToAzureBlobStorage(new Uri(blobUri!), credential)
                .ProtectKeysWithAzureKeyVault(new Uri(keyId!), credential);
            ring = FieldProtectionKeyRing.AzureBlobWithKeyVault;
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            var directory = string.IsNullOrWhiteSpace(localDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudHealthOffice", "field-protection-keys", appName)
                : localDirectory;
            Directory.CreateDirectory(directory);
            services.AddDataProtection()
                .SetApplicationName(appName)
                .PersistKeysToFileSystem(new DirectoryInfo(directory));
            ring = FieldProtectionKeyRing.LocalDirectory;
        }
        else
        {
            services.TryAddSingleton<IFieldProtector>(new UnconfiguredFieldProtector(rejectPlaintext));
            return FieldProtectionKeyRing.None;
        }

        services.TryAddSingleton<IFieldProtector>(sp =>
            new DataProtectionFieldProtector(sp.GetRequiredService<IDataProtectionProvider>(), appName, rejectPlaintext, rejectUnbound));
        return ring;
    }

    /// <summary>Logs, once at startup, that legacy plaintext is still accepted on read.</summary>
    internal sealed class FieldProtectionStartupWarning : IHostedService
    {
        public const string Category = "CloudHealthOffice.FieldProtection";
        public static readonly EventId Event = new(4818, "FieldProtectionRejectPlaintextOff");

        private readonly ILogger _logger;
        private readonly string _applicationName;

        public FieldProtectionStartupWarning(ILogger logger, string applicationName)
        {
            _logger = logger;
            _applicationName = applicationName;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogWarning(Event,
                "FieldProtection:RejectPlaintext is off for {Application}: a protected field that still holds plaintext " +
                "(stored before encryption) is read and used as is. Run --encrypt-bank-accounts until it reports nothing " +
                "left, then set FieldProtection:RejectPlaintext=true (see docs/security/bank-account-data.md).",
                _applicationName);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static TokenCredential CredentialFrom(string? managedIdentityClientId)
        => string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId });
}
