using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ClaimsScrubbingService.Models;

namespace ClaimsScrubbingService.Services;

public interface IBlobArchiveService
{
    Task ArchiveClaimResultAsync(X12837Claim claim, ClaimValidationResult result);
}

public class BlobArchiveService : IBlobArchiveService
{
    private readonly ILogger<BlobArchiveService> _logger;
    private readonly BlobContainerClient _container;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BlobArchiveService(ILogger<BlobArchiveService> logger, IConfiguration configuration)
    {
        _logger = logger;

        var connectionString = configuration["Storage:ConnectionString"];
        var accountName      = configuration["Storage:AccountName"];
        var containerName    = configuration["Storage:ContainerName"] ?? "claims-archive";

        BlobServiceClient serviceClient;

        if (!string.IsNullOrEmpty(connectionString))
        {
            serviceClient = new BlobServiceClient(connectionString);
        }
        else if (!string.IsNullOrEmpty(accountName))
        {
            var credential = new Azure.Identity.DefaultAzureCredential();
            serviceClient = new BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                credential);
        }
        else
        {
            throw new InvalidOperationException(
                "Either Storage:ConnectionString or Storage:AccountName must be configured for BlobArchiveService.");
        }

        _container = serviceClient.GetBlobContainerClient(containerName);
    }

    /// <summary>
    /// Archives claim + validation result to blob storage.
    /// Path pattern: {claimType}/{status}/{yyyy}/{MM}/{dd}/{claimId}.json
    /// </summary>
    public async Task ArchiveClaimResultAsync(X12837Claim claim, ClaimValidationResult result)
    {
        try
        {
            var now     = DateTime.UtcNow;
            var blobPath = $"{claim.ClaimType}/{result.Status}/{now:yyyy}/{now:MM}/{now:dd}/{claim.ClaimId}.json";

            var content = JsonSerializer.Serialize(new
            {
                claim,
                validationResult = result,
                archivedAt       = DateTime.UtcNow.ToString("O")
            }, JsonOptions);

            var blobClient = _container.GetBlobClient(blobPath);
            var bytes      = Encoding.UTF8.GetBytes(content);

            using var stream = new MemoryStream(bytes);
            await blobClient.UploadAsync(stream, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive claim {ClaimId} to blob storage", claim.ClaimId);
            // Non-fatal: do not rethrow
        }
    }
}
