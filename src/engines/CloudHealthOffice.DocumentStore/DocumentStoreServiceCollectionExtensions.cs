using Amazon;
using Amazon.S3;
using Azure.Storage.Blobs;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.DocumentStore;

/// <summary>Which storage backs <see cref="IDocumentStore"/>.</summary>
public enum DocumentStoreProvider
{
    Azure,
    Gcs,
    S3,
    InMemory
}

/// <summary>
/// Registers the configured <see cref="IDocumentStore"/>. Every service that
/// stores documents uses this, so one setting moves a deployment between
/// clouds.
///
/// <code>
///   DocumentStore:Provider          Azure | Gcs | S3 | InMemory
///
///   Azure  DocumentStore:Azure:ConnectionString
///          (falls back to BlobStorage:ConnectionString, the older key)
///   Gcs    DocumentStore:Gcs:Bucket       required; created by Terraform
///          DocumentStore:Gcs:Endpoint     optional; an emulator (unauthenticated, no upload links)
///   S3     DocumentStore:S3:Bucket        required
///          DocumentStore:S3:Region        optional; the SDK's default otherwise
///          DocumentStore:S3:ServiceUrl    optional; an S3-compatible endpoint such as MinIO
///          DocumentStore:S3:ForcePathStyle optional; true for MinIO
/// </code>
///
/// Fails closed: with no provider set, a deployment that still has
/// <c>BlobStorage:ConnectionString</c> gets Azure (what it had before this
/// setting existed); otherwise the store cannot be created and the service
/// does not start. <c>InMemory</c> is refused outside Development, so
/// documents never silently live in a pod's memory.
///
/// The store is created on first resolution, from the host's final
/// configuration; services resolve it once at startup to fail fast.
/// </summary>
public static class DocumentStoreServiceCollectionExtensions
{
    public const string SectionName = "DocumentStore";
    public const string LegacyAzureConnectionStringKey = "BlobStorage:ConnectionString";

    public static IServiceCollection AddChoDocumentStore(this IServiceCollection services, string environmentName)
    {
        services.AddSingleton<IDocumentStore>(sp => Create(
            sp.GetRequiredService<IConfiguration>(),
            environmentName,
            sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
        return services;
    }

    /// <summary>Create the configured store. Throws <see cref="InvalidOperationException"/> when the configuration is incomplete.</summary>
    public static IDocumentStore Create(IConfiguration configuration, string environmentName, ILoggerFactory loggerFactory)
    {
        var provider = ResolveProvider(configuration, environmentName);
        var section = configuration.GetSection(SectionName);

        return provider switch
        {
            DocumentStoreProvider.Azure => CreateAzure(configuration, section, loggerFactory),
            DocumentStoreProvider.Gcs => CreateGcs(section, loggerFactory),
            DocumentStoreProvider.S3 => CreateS3(section, loggerFactory),
            DocumentStoreProvider.InMemory => new InMemoryDocumentStore(),
            _ => throw new InvalidOperationException($"Unknown document store provider {provider}.")
        };
    }

    public static DocumentStoreProvider ResolveProvider(IConfiguration configuration, string environmentName)
    {
        var configured = configuration[$"{SectionName}:Provider"];
        DocumentStoreProvider provider;

        if (string.IsNullOrWhiteSpace(configured))
        {
            if (string.IsNullOrWhiteSpace(configuration[LegacyAzureConnectionStringKey]))
            {
                throw new InvalidOperationException(
                    "No document store is configured. Set DocumentStore:Provider to Azure, Gcs or S3 "
                    + "(or InMemory in Development) with that provider's settings.");
            }
            provider = DocumentStoreProvider.Azure;
        }
        else if (!Enum.TryParse(configured.Trim(), ignoreCase: true, out provider)
                 || !Enum.IsDefined(provider))
        {
            throw new InvalidOperationException(
                $"DocumentStore:Provider '{configured}' is not one of Azure, Gcs, S3, InMemory.");
        }

        if (provider == DocumentStoreProvider.InMemory
            && !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DocumentStore:Provider InMemory is for Development only: documents would be lost on restart.");
        }

        return provider;
    }

    private static AzureBlobDocumentStore CreateAzure(IConfiguration configuration, IConfigurationSection section, ILoggerFactory loggerFactory)
    {
        var connectionString = FirstNonEmpty(section["Azure:ConnectionString"], configuration[LegacyAzureConnectionStringKey])
            ?? throw new InvalidOperationException(
                "DocumentStore:Provider is Azure but no connection string is set "
                + "(DocumentStore:Azure:ConnectionString or BlobStorage:ConnectionString).");

        return new AzureBlobDocumentStore(
            new BlobServiceClient(connectionString),
            loggerFactory.CreateLogger<AzureBlobDocumentStore>());
    }

    private static GcsDocumentStore CreateGcs(IConfigurationSection section, ILoggerFactory loggerFactory)
    {
        var bucket = Required(section, "Gcs:Bucket");
        var endpoint = section["Gcs:Endpoint"];

        StorageClient client;
        UrlSigner? signer = null;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            // An emulator (fake-gcs-server): no credentials, so nothing can sign links.
            client = new StorageClientBuilder
            {
                BaseUri = endpoint.TrimEnd('/') + "/storage/v1/",
                UnauthenticatedAccess = true
            }.Build();
        }
        else
        {
            var credential = GoogleCredential.GetApplicationDefault();
            client = StorageClient.Create(credential);
            signer = TryCreateSigner(credential, loggerFactory.CreateLogger<GcsDocumentStore>());
        }

        return new GcsDocumentStore(client, signer, bucket, loggerFactory.CreateLogger<GcsDocumentStore>());
    }

    private static UrlSigner? TryCreateSigner(GoogleCredential credential, ILogger logger)
    {
        try
        {
            return UrlSigner.FromCredential(credential);
        }
        catch (InvalidOperationException ex)
        {
            // A user login (gcloud auth application-default) cannot sign. Uploads
            // through the service still work; pre-signed uploads answer that
            // they are unavailable.
            logger.LogWarning(ex, "Google credentials cannot sign URLs; pre-signed document uploads are unavailable.");
            return null;
        }
    }

    private static S3DocumentStore CreateS3(IConfigurationSection section, ILoggerFactory loggerFactory)
    {
        var bucket = Required(section, "S3:Bucket");
        var config = new AmazonS3Config();

        var region = section["S3:Region"];
        if (!string.IsNullOrWhiteSpace(region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        var serviceUrl = section["S3:ServiceUrl"];
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
        }

        if (bool.TryParse(section["S3:ForcePathStyle"], out var forcePathStyle))
        {
            config.ForcePathStyle = forcePathStyle;
        }

        return new S3DocumentStore(new AmazonS3Client(config), bucket, loggerFactory.CreateLogger<S3DocumentStore>());
    }

    private static string Required(IConfigurationSection section, string key) =>
        FirstNonEmpty(section[key])
        ?? throw new InvalidOperationException($"{SectionName}:{key} is required for the configured document store.");

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
