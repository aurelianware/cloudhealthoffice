using System.Text.Json;
using Azure.Storage.Blobs;
using Confluent.Kafka;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Models.Events;
using EncounterSubmissionService.Services;
using EncounterSubmissionService.StateRoutes;

namespace EncounterSubmissionService.Workers;

/// <summary>
/// Background worker that runs every 4 hours (configurable):
///   1. Flags submissions where deadline is within 7 days → DeadlineWarning (all states)
///   2. Publishes encounter-deadline-warning Kafka event for each warning
///   3. Florida: auto-batches submissions due within 48 hours via BuildFmmisSubmissionBatch
///      and writes the FMMIS staging file to Azure Blob (unchanged)
///   4. Texas / California: batches every pending encounter on each enabled route, per
///      tenant, into 837P / 837I files stored with the batch (and copied to blob staging
///      when configured). Encounters that fail validation are held back for rework.
/// </summary>
public class EncounterSubmissionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EncounterSubmissionWorker> _logger;
    private readonly IConfiguration _configuration;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public EncounterSubmissionWorker(
        IServiceProvider serviceProvider,
        ILogger<EncounterSubmissionWorker> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalHours = _configuration.GetValue("Worker:IntervalHours", 4);
        var interval = TimeSpan.FromHours(intervalHours);

        _logger.LogInformation(
            "Encounter submission worker started — interval {IntervalHours}h", intervalHours);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during encounter submission worker cycle");
            }
        }
    }

    /// <summary>
    /// Single worker cycle. Public for unit testing.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Encounter submission worker cycle starting");

        using var scope = _serviceProvider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IEncounterSubmissionService>();

        var warningDays = _configuration.GetValue("Worker:DeadlineWarningDays", 7);
        var urgentHours = _configuration.GetValue("Worker:UrgentBatchHours", 48);

        // ── Step 1: Flag deadline warnings ───────────────────────────
        var approaching = (await service.GetApproachingDeadlineAsync(warningDays)).ToList();
        var warningCount = 0;

        foreach (var submission in approaching)
        {
            if (submission.Status == EncounterSubmissionStatus.Pending)
            {
                await service.FlagDeadlineWarningAsync(submission);
                await PublishDeadlineWarningAsync(submission);
                warningCount++;
            }
        }

        if (warningCount > 0)
        {
            _logger.LogWarning(
                "Flagged {WarningCount} encounter submissions as DeadlineWarning", warningCount);
        }

        // ── Step 2: Auto-batch urgent submissions (due within 48h) ───
        var urgentCutoff = DateTime.UtcNow.AddHours(urgentHours);
        var urgentSubmissions = approaching
            .Where(s => s.SubmissionDeadline <= urgentCutoff &&
                        IsFlorida(s) &&
                        s.Status is EncounterSubmissionStatus.Pending
                        or EncounterSubmissionStatus.DeadlineWarning)
            .ToList();

        if (urgentSubmissions.Count == 0)
        {
            _logger.LogInformation("No urgent Florida submissions requiring auto-batch");
        }

        // Group by tenant for batch processing
        var byTenant = urgentSubmissions.GroupBy(s => s.TenantId);

        foreach (var tenantGroup in byTenant)
        {
            var tenantId = tenantGroup.Key;
            var tenantSubmissions = tenantGroup.ToList();

            _logger.LogInformation(
                "Auto-batching {Count} urgent submissions for tenant {TenantId}",
                tenantSubmissions.Count, tenantId);

            try
            {
                var file = await service.BuildFmmisSubmissionBatchAsync(tenantSubmissions, tenantId);

                // Write batch file to Azure Blob staging
                await WriteToBlobStagingAsync(file, tenantId);

                _logger.LogInformation(
                    "Batch {BatchId} staged for tenant {TenantId}: {FileName} ({TransactionCount} transactions)",
                    file.BatchId, tenantId, file.FileName, file.TransactionCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to build/stage FMMIS batch for tenant {TenantId} with {Count} submissions",
                    tenantId, tenantSubmissions.Count);
            }
        }

        // ── Step 3: state X12 routes (Texas, California) ─────────────
        await RunStateRoutesAsync(
            scope.ServiceProvider.GetRequiredService<IStateEncounterRouteRegistry>(),
            scope.ServiceProvider.GetRequiredService<IStateEncounterBatchService>(),
            ct);
    }

    private static bool IsFlorida(EncounterSubmission s) =>
        string.IsNullOrWhiteSpace(s.StateCode)
        || string.Equals(s.StateCode, StateEncounterRoute.Florida.StateCode, StringComparison.OrdinalIgnoreCase);

    // ── State X12 routes (Texas, California) ─────────────────────────

    /// <summary>
    /// Batches every pending encounter on each enabled X12 route, tenant by tenant.
    /// Public for unit testing.
    /// </summary>
    public async Task RunStateRoutesAsync(
        IStateEncounterRouteRegistry registry, IStateEncounterBatchService stateService, CancellationToken ct = default)
    {
        var maxPerBatch = _configuration.GetValue("Worker:StateBatchMaxEncounters", 5000);

        foreach (var route in StateEncounterRoute.All.Where(r => r.Format == EncounterRouteFormat.X12_837))
        {
            if (registry.GetOptions(route.StateCode) is not { Enabled: true }) continue;

            var batchable = await stateService.GetBatchableAsync(route.StateCode, maxPerBatch * 10);
            foreach (var tenantGroup in batchable.GroupBy(s => s.TenantId))
            {
                var tenantId = tenantGroup.Key;
                if (registry.Resolve(route.StateCode, route.LineOfBusiness, tenantId, out var reason) is null)
                {
                    _logger.LogWarning(
                        "{Count} {State} encounters for tenant {TenantId} are waiting but the route cannot be used: {Reason}",
                        tenantGroup.Count(), route.StateCode, tenantId, reason);
                    continue;
                }

                foreach (var chunk in tenantGroup.Chunk(maxPerBatch))
                {
                    try
                    {
                        var result = await stateService.BuildBatchAsync(route, chunk, tenantId, ct);
                        foreach (var file in result.Files)
                            await WriteStateFileToBlobStagingAsync(route, registry, file);

                        _logger.LogInformation(
                            "{State} batch {BatchId} for tenant {TenantId}: {Files} file(s), {Batched} encounters, {Held} held back",
                            route.StateCode, result.BatchId, tenantId, result.Files.Count,
                            result.BatchedSubmissionIds.Count, result.HeldBack.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed to build {State} encounter batch for tenant {TenantId} with {Count} submissions",
                            route.StateCode, tenantId, chunk.Length);
                    }
                }
            }
        }
    }

    private async Task WriteStateFileToBlobStagingAsync(
        StateEncounterRoute route, IStateEncounterRouteRegistry registry, StateEncounterFile file)
    {
        var connectionString = _configuration["AzureStorage:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogInformation(
                "Blob staging not configured; {FileName} is kept with the batch in encounter_state_files", file.FileName);
            return;
        }

        var containerName = registry.GetOptions(route.StateCode)?.StagingContainer
            ?? $"{route.FilePrefix.ToLowerInvariant()}-staging";
        var container = new BlobServiceClient(connectionString).GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();
        var blob = container.GetBlobClient($"{file.TenantId}/{file.FileName}");
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(file.Content));
        await blob.UploadAsync(stream, overwrite: false);
    }

    // ── Kafka Deadline Warning Publisher ──────────────────────────────

    private async Task PublishDeadlineWarningAsync(EncounterSubmission submission)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogDebug("Kafka not configured — skipping deadline warning event");
            return;
        }

        var topic = _configuration["Kafka:DeadlineWarningTopic"] ?? "encounter-deadline-warning";

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers
        };

        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            producerConfig.SaslUsername = saslUsername;
            producerConfig.SaslPassword = _configuration["Kafka:SaslPassword"];
            producerConfig.SaslMechanism = SaslMechanism.ScramSha512;
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        try
        {
            using var producer = new ProducerBuilder<string, string>(producerConfig).Build();

            var eventPayload = JsonSerializer.Serialize(new EncounterDeadlineWarningEvent
            {
                SubmissionId = submission.Id,
                ClaimId = submission.ClaimId,
                TenantId = submission.TenantId,
                Deadline = submission.SubmissionDeadline,
                DaysRemaining = (submission.SubmissionDeadline - DateTime.UtcNow).TotalDays
            }, JsonOptions);

            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = submission.TenantId,
                Value = eventPayload
            });

            _logger.LogInformation(
                "Published deadline warning for submission {Id}, claim {ClaimId} to {Topic}",
                submission.Id, submission.ClaimId, topic);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish deadline warning for submission {Id}", submission.Id);
        }
    }

    // ── Azure Blob Staging ───────────────────────────────────────────

    private async Task WriteToBlobStagingAsync(FmmisSubmissionFileDto file, string tenantId)
    {
        var connectionString = _configuration["AzureStorage:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogWarning(
                "AzureStorage:ConnectionString not configured — " +
                "writing batch {FileName} to log only", file.FileName);
            return;
        }

        var containerName = _configuration["AzureStorage:StagingContainer"] ?? "fmmis-staging";

        var blobClient = new BlobServiceClient(connectionString);
        var container = blobClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();

        var blobPath = $"{tenantId}/{file.FileName}";
        var blob = container.GetBlobClient(blobPath);

        using var stream = new MemoryStream(file.Content);
        await blob.UploadAsync(stream, overwrite: true);

        _logger.LogInformation(
            "Uploaded FMMIS batch to blob staging: {Container}/{BlobPath} ({Bytes} bytes)",
            containerName, blobPath, file.Content.Length);
    }
}
