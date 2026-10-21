using Xunit;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Storage.Blobs;
using Google;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.DocumentStore.Tests;

// One class per provider, all running DocumentStoreContractTests. The emulator
// classes skip (not fail) unless their variable is set; CI sets all three
// (.github/workflows/test-dotnet.yml starts the emulators).

public class InMemoryDocumentStoreContractTests : DocumentStoreContractTests
{
    protected override bool SignsUploadLinks => false;

    protected override Task<IDocumentStore> CreateStoreAsync() =>
        Task.FromResult<IDocumentStore>(new InMemoryDocumentStore());
}

/// <summary>Azurite. <c>CHO_TEST_AZURITE</c> is a connection string, e.g. <c>UseDevelopmentStorage=true</c>.</summary>
[Trait("Category", "Integration")]
public class AzureBlobDocumentStoreContractTests : DocumentStoreContractTests
{
    public const string EnvVar = "CHO_TEST_AZURITE";

    // The development account key signs SAS links, as a real account key would.
    protected override bool SignsUploadLinks => true;

    protected override Task<IDocumentStore> CreateStoreAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(connectionString), $"Set {EnvVar} to run the Azure Blob contract tests.");

        return Task.FromResult<IDocumentStore>(new AzureBlobDocumentStore(
            new BlobServiceClient(connectionString),
            NullLogger<AzureBlobDocumentStore>.Instance));
    }
}

/// <summary>fake-gcs-server. <c>CHO_TEST_GCS_ENDPOINT</c> is its base URL, e.g. <c>http://127.0.0.1:4443</c>.</summary>
[Trait("Category", "Integration")]
public class GcsDocumentStoreContractTests : DocumentStoreContractTests
{
    public const string EnvVar = "CHO_TEST_GCS_ENDPOINT";

    // The emulator runs without credentials, so nothing can sign links; signing
    // with a compute credential is Google's IAM signBlob, covered by the
    // client library, not by an emulator.
    protected override bool SignsUploadLinks => false;

    // fake-gcs-server takes the content type from a resumable upload's final
    // PUT (which the client library sends as application/octet-stream), where
    // Google Cloud Storage uses the type given when the session starts.
    protected override bool KeepsUploadContentType => false;

    protected override async Task<IDocumentStore> CreateStoreAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(endpoint), $"Set {EnvVar} to run the Google Cloud Storage contract tests.");

        var client = await new StorageClientBuilder
        {
            BaseUri = endpoint!.TrimEnd('/') + "/storage/v1/",
            UnauthenticatedAccess = true
        }.BuildAsync();

        const string bucket = "cho-contract-tests";
        try
        {
            await client.CreateBucketAsync("cho-test-project", bucket);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Conflict)
        {
            // Created by an earlier test class.
        }

        return new GcsDocumentStore(client, signer: null, bucket, NullLogger<GcsDocumentStore>.Instance);
    }
}

/// <summary>MinIO. <c>CHO_TEST_S3_ENDPOINT</c> is its URL, e.g. <c>http://127.0.0.1:9000</c> (credentials minioadmin/minioadmin unless CHO_TEST_S3_ACCESS_KEY/SECRET_KEY are set).</summary>
[Trait("Category", "Integration")]
public class S3DocumentStoreContractTests : DocumentStoreContractTests
{
    public const string EnvVar = "CHO_TEST_S3_ENDPOINT";

    protected override bool SignsUploadLinks => true;

    protected override async Task<IDocumentStore> CreateStoreAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable(EnvVar);
        Skip.If(string.IsNullOrWhiteSpace(endpoint), $"Set {EnvVar} to run the S3 contract tests.");

        var client = new AmazonS3Client(
            new BasicAWSCredentials(
                Environment.GetEnvironmentVariable("CHO_TEST_S3_ACCESS_KEY") ?? "minioadmin",
                Environment.GetEnvironmentVariable("CHO_TEST_S3_SECRET_KEY") ?? "minioadmin"),
            new AmazonS3Config
            {
                ServiceURL = endpoint,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1"
            });

        const string bucket = "cho-contract-tests";
        try
        {
            await client.PutBucketAsync(bucket);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Created by an earlier test class.
        }

        return new S3DocumentStore(client, bucket, NullLogger<S3DocumentStore>.Instance);
    }
}
