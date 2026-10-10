using Azure.Identity;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>
/// Configuration section <c>NachaTransmission</c>:
/// <list type="bullet">
///   <item><c>Mode</c>: <c>Sftp</c> (default; the tenant's bank drop from
///   tenant-service) or <c>LocalFolder</c> (Development and Testing only:
///   startup fails anywhere else).</item>
///   <item><c>LocalFolder</c>: where <c>LocalFolder</c> mode writes
///   (default: the temp directory, <c>cho-nacha-outbox/{service}</c>).</item>
///   <item><c>KeyVaultUri</c>: the vault holding the tenants' SFTP credentials
///   (default: <c>SecretProvider:AzureKeyVaultUri</c>).</item>
///   <item><c>ManagedIdentityClientId</c>: optional user-assigned identity for
///   the vault (otherwise <c>DefaultAzureCredential</c>).</item>
/// </list>
/// Tenant settings come from tenant-service (<c>TenantService:BaseUrl</c>).
/// Undelivered files are held in Mongo (<c>NachaHeldFiles</c>, TTL 7 days),
/// in memory in Development/Testing without a database, and not at all
/// otherwise (payments stay Pending).
/// </summary>
public static class NachaTransmissionServiceCollectionExtensions
{
    public const string SectionName = "NachaTransmission";

    public static IServiceCollection AddChoNachaTransmission(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        string serviceName,
        ChoDatabaseProvider? databaseProvider)
    {
        var section = configuration.GetSection(SectionName);
        services.AddSingleton(new NachaTransmissionOptions { ServiceName = serviceName });
        services.TryAddSingleton(TimeProvider.System);

        var mode = section["Mode"];
        if (string.Equals(mode, "LocalFolder", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocalFolderNachaTransmitter.IsAllowed(environment))
                throw new InvalidOperationException(
                    "NachaTransmission:Mode=LocalFolder is for Development and Testing only; it would leave NACHA files " +
                    "(full account numbers) on the pod's disk. Remove it and configure the tenant's bank SFTP.");
            var folder = section["LocalFolder"] is { Length: > 0 } configured
                ? configured
                : Path.Combine(Path.GetTempPath(), "cho-nacha-outbox", serviceName);
            services.AddSingleton<INachaTransmitter>(sp => new LocalFolderNachaTransmitter(
                folder, environment, sp.GetRequiredService<ILogger<LocalFolderNachaTransmitter>>()));
        }
        else if (string.IsNullOrEmpty(mode) || string.Equals(mode, "Sftp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(HttpNachaTransmissionSettingsSource.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(configuration["TenantService:BaseUrl"] ?? "http://tenant-service");
                client.Timeout = TimeSpan.FromSeconds(10);
            });
            services.AddSingleton<INachaTransmissionSettingsSource, HttpNachaTransmissionSettingsSource>();

            var vault = section["KeyVaultUri"] is { Length: > 0 } v ? v : configuration["SecretProvider:AzureKeyVaultUri"];
            if (!string.IsNullOrWhiteSpace(vault))
            {
                var identity = section["ManagedIdentityClientId"];
                var credential = string.IsNullOrWhiteSpace(identity)
                    ? new DefaultAzureCredential()
                    : new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = identity });
                services.AddSingleton<INachaSecretReader>(new KeyVaultNachaSecretReader(new Uri(vault), credential));
            }
            else
            {
                services.AddSingleton<INachaSecretReader, UnconfiguredNachaSecretReader>();
            }

            services.AddSingleton<ISftpSessionFactory, SshNetSftpSessionFactory>();
            services.AddSingleton<INachaTransmitter>(sp => new SftpNachaTransmitter(
                sp.GetRequiredService<INachaTransmissionSettingsSource>(),
                sp.GetRequiredService<INachaSecretReader>(),
                sp.GetRequiredService<ISftpSessionFactory>(),
                sp.GetRequiredService<ILogger<SftpNachaTransmitter>>(),
                sp.GetRequiredService<TimeProvider>()));
        }
        else
        {
            throw new InvalidOperationException($"Unknown NachaTransmission:Mode '{mode}'. Use Sftp (default) or LocalFolder.");
        }

        // The read-only drop listing used to reconcile an ambiguous transmission:
        // the same instance (settings, pinned key, credentials) as the transmitter.
        services.AddSingleton<INachaRemoteFileProbe>(sp => sp.GetRequiredService<INachaTransmitter>() as INachaRemoteFileProbe
            ?? throw new InvalidOperationException("The configured NACHA transmitter cannot list the bank's drop."));

        if (databaseProvider == ChoDatabaseProvider.MongoDb)
            services.AddScoped<INachaHeldFileStore>(sp => new MongoNachaHeldFileStore(sp.GetRequiredService<IMongoDatabase>()));
        else if (databaseProvider == null && LocalFolderNachaTransmitter.IsAllowed(environment))
            services.AddSingleton<INachaHeldFileStore, InMemoryNachaHeldFileStore>();
        else
            // TODO: a Cosmos (native SDK) container for held files. Until then a
            // file that cannot be sent is not kept and its payments stay Pending.
            services.AddSingleton<INachaHeldFileStore, UnavailableNachaHeldFileStore>();

        services.AddScoped<INachaDispatcher>(sp => new NachaDispatcher(
            sp.GetRequiredService<INachaTransmitter>(),
            sp.GetRequiredService<INachaHeldFileStore>(),
            sp.GetRequiredService<IFieldProtector>(),
            sp.GetRequiredService<NachaTransmissionOptions>(),
            sp.GetRequiredService<ILogger<NachaDispatcher>>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
