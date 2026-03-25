using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Driver;

namespace CapitationService.Migrations;

/// <summary>
/// One-time migration: splits capitation_contracts documents into
/// ProviderContract (master) + CapitationRateConfig (child) records.
///
/// Usage: dotnet run --project src/services/capitation-service -- --migrate-split
///
/// Prerequisites (see Pre-Flight Checklist):
///   1. provider-contracts-service running at configured URL
///   2. MongoDB backup completed
///   3. Document count captured
/// </summary>
public static class SplitCapitationContracts
{
    public static async Task<int> RunAsync(string mongoConnectionString, string databaseName, string providerContractsServiceUrl)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("  CapitationContract → ProviderContract + CapitationRateConfig");
        Console.WriteLine("  Migration Script — Cloud Health Office");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine();

        // ── Step 1: Pre-flight ──────────────────────────────────────────
        Console.WriteLine("[PRE-FLIGHT] Checking provider-contracts-service...");
        using var httpClient = new HttpClient { BaseAddress = new Uri(providerContractsServiceUrl) };
        httpClient.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            var response = await httpClient.GetAsync("/api/v1/contracts");
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"ABORT: provider-contracts-service returned {response.StatusCode} at {providerContractsServiceUrl}.");
                Console.Error.WriteLine("See pre-flight checklist in CHO-Finance-Spec-v2_1.md.");
                return 1;
            }
            Console.WriteLine($"[PRE-FLIGHT] OK — provider-contracts-service reachable at {providerContractsServiceUrl}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ABORT: provider-contracts-service not reachable at {providerContractsServiceUrl}.");
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("See pre-flight checklist in CHO-Finance-Spec-v2_1.md.");
            return 1;
        }

        // ── Step 2: Connect to MongoDB ──────────────────────────────────
        var client = new MongoClient(mongoConnectionString);
        var db = client.GetDatabase(databaseName);
        var oldCollection = db.GetCollection<OldCapitationContract>("capitation_contracts");
        var rateConfigCollection = db.GetCollection<NewCapitationRateConfig>("capitation_rate_configs");

        var preMigrationCount = await oldCollection.CountDocumentsAsync(FilterDefinition<OldCapitationContract>.Empty);
        Console.WriteLine($"[INFO] Pre-migration document count: {preMigrationCount}");

        if (preMigrationCount == 0)
        {
            Console.WriteLine("[INFO] No documents to migrate. Exiting.");
            return 0;
        }

        var documents = await oldCollection.Find(FilterDefinition<OldCapitationContract>.Empty).ToListAsync();

        // ── Step 3 & 4: Migrate ─────────────────────────────────────────
        var migrationLog = new List<MigrationLogEntry>();
        int succeeded = 0;
        int failed = 0;
        int total = documents.Count;

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        foreach (var (doc, index) in documents.Select((d, i) => (d, i)))
        {
            var logEntry = new MigrationLogEntry { OldId = doc.Id };
            Console.Write($"Migrating {index + 1}/{total}: {doc.ContractNumber}... ");

            try
            {
                // Create ProviderContract via API
                var providerContract = new
                {
                    tenantId = doc.TenantId,
                    contractNumber = doc.ContractNumber,
                    providerNPI = doc.ProviderNPI,
                    providerName = doc.ProviderName,
                    providerType = doc.ProviderType,
                    lineOfBusiness = doc.LineOfBusiness,
                    planIds = doc.PlanIds ?? new List<string>(),
                    paymentMethodology = "FullCapitation",
                    networkStatus = "Participating",
                    effectiveDate = doc.EffectiveDate,
                    terminationDate = doc.TerminationDate,
                    status = MapStatus(doc.Status),
                    createdAt = doc.CreatedAt,
                    lastUpdatedAt = doc.LastUpdatedAt,
                    createdBy = doc.CreatedBy,
                    lastUpdatedBy = doc.LastUpdatedBy
                };

                var postResponse = await httpClient.PostAsJsonAsync("/api/v1/contracts", providerContract, jsonOptions);
                if (!postResponse.IsSuccessStatusCode)
                {
                    var errorBody = await postResponse.Content.ReadAsStringAsync();
                    throw new Exception($"POST /api/v1/contracts failed ({postResponse.StatusCode}): {errorBody}");
                }

                var createdContract = await postResponse.Content.ReadFromJsonAsync<CreatedContractResponse>(jsonOptions);
                var newContractId = createdContract?.Id ?? throw new Exception("No Id returned from POST");

                logEntry.NewProviderContractId = newContractId;

                // Create CapitationRateConfig directly in MongoDB
                var rateConfig = new NewCapitationRateConfig
                {
                    TenantId = doc.TenantId,
                    RateConfigNumber = doc.ContractNumber, // Preserve reference
                    ContractId = newContractId,
                    ContractNumber = doc.ContractNumber,
                    ProviderNPI = doc.ProviderNPI,
                    ProviderName = doc.ProviderName,
                    LineOfBusiness = doc.LineOfBusiness,
                    LastDenormSyncAt = DateTime.UtcNow,
                    ContractType = doc.ContractType,
                    RateTiers = doc.RateTiers ?? new List<object>(),
                    RiskAdjusted = doc.RiskAdjusted,
                    DefaultRiskScore = doc.DefaultRiskScore,
                    WithholdPercentage = doc.WithholdPercentage,
                    IncentivePoolPercentage = doc.IncentivePoolPercentage,
                    StopLossThreshold = doc.StopLossThreshold,
                    AggregateStopLoss = doc.AggregateStopLoss,
                    EffectiveDate = doc.EffectiveDate,
                    TerminationDate = doc.TerminationDate,
                    Status = doc.Status,
                    CreatedAt = doc.CreatedAt,
                    LastUpdatedAt = doc.LastUpdatedAt,
                    CreatedBy = doc.CreatedBy,
                    LastUpdatedBy = doc.LastUpdatedBy
                };

                await rateConfigCollection.InsertOneAsync(rateConfig);
                logEntry.NewRateConfigId = rateConfig.Id;
                logEntry.Status = "Succeeded";
                succeeded++;
                Console.WriteLine("OK");
            }
            catch (Exception ex)
            {
                logEntry.Status = "Failed";
                logEntry.Errors = ex.Message;
                failed++;
                Console.WriteLine($"FAILED: {ex.Message}");
            }

            migrationLog.Add(logEntry);
        }

        // ── Step 5: Write migration log ─────────────────────────────────
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var logFileName = $"migration-log-{timestamp}.json";
        var logJson = JsonSerializer.Serialize(migrationLog, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(logFileName, logJson);
        Console.WriteLine($"\n[LOG] Migration log written to {logFileName}");

        // ── Step 6: Rename old collection (do not delete) ───────────────
        var archiveName = $"capitation_contracts_migrated_{timestamp}";
        await db.RenameCollectionAsync("capitation_contracts", archiveName);
        Console.WriteLine($"[ARCHIVE] Renamed capitation_contracts → {archiveName}");

        // ── Summary ─────────────────────────────────────────────────────
        var postProviderContractCount = await httpClient.GetAsync("/api/v1/contracts");
        var postRateConfigCount = await rateConfigCollection.CountDocumentsAsync(FilterDefinition<NewCapitationRateConfig>.Empty);

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine($"  Total:                         {total}");
        Console.WriteLine($"  Succeeded:                     {succeeded}");
        Console.WriteLine($"  Failed:                        {failed}");
        Console.WriteLine($"  Pre-migration count:           {preMigrationCount}");
        Console.WriteLine($"  Post-migration RateConfigs:    {postRateConfigCount}");
        Console.WriteLine($"  COUNTS MATCH:                  {(preMigrationCount == postRateConfigCount ? "yes" : "WARNING — MISMATCH")}");
        Console.WriteLine("═══════════════════════════════════════════════════════════");

        return failed > 0 ? 2 : 0;
    }

    private static string MapStatus(string? status) => status switch
    {
        "Draft" => "Draft",
        "Active" => "Active",
        "Suspended" => "Suspended",
        "Terminated" => "Terminated",
        "Expired" => "Expired",
        _ => "Draft"
    };

    // ── DTOs for migration (loosely typed to handle legacy document shape) ──

    private class OldCapitationContract
    {
        [MongoDB.Bson.Serialization.Attributes.BsonId]
        [MongoDB.Bson.Serialization.Attributes.BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ContractNumber { get; set; } = string.Empty;
        public string ProviderNPI { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public string? ProviderType { get; set; }
        public string? ContractType { get; set; }
        public string? LineOfBusiness { get; set; }
        public List<string>? PlanIds { get; set; }
        public List<object>? RateTiers { get; set; }
        public bool RiskAdjusted { get; set; }
        public decimal DefaultRiskScore { get; set; } = 1.0m;
        public decimal WithholdPercentage { get; set; }
        public decimal? IncentivePoolPercentage { get; set; }
        public decimal? StopLossThreshold { get; set; }
        public decimal? AggregateStopLoss { get; set; }
        public DateTime EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public string? Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? LastUpdatedBy { get; set; }
    }

    private class NewCapitationRateConfig
    {
        [MongoDB.Bson.Serialization.Attributes.BsonId]
        [MongoDB.Bson.Serialization.Attributes.BsonRepresentation(MongoDB.Bson.BsonType.String)]
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string TenantId { get; set; } = string.Empty;
        public string RateConfigNumber { get; set; } = string.Empty;
        public string ContractId { get; set; } = string.Empty;
        public string ContractNumber { get; set; } = string.Empty;
        public string ProviderNPI { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public string? LineOfBusiness { get; set; }
        public DateTime? LastDenormSyncAt { get; set; }
        public string? ContractType { get; set; }
        public List<object>? RateTiers { get; set; }
        public bool RiskAdjusted { get; set; }
        public decimal DefaultRiskScore { get; set; } = 1.0m;
        public decimal WithholdPercentage { get; set; }
        public decimal? IncentivePoolPercentage { get; set; }
        public decimal? StopLossThreshold { get; set; }
        public decimal? AggregateStopLoss { get; set; }
        public DateTime EffectiveDate { get; set; }
        public DateTime? TerminationDate { get; set; }
        public string? Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? LastUpdatedBy { get; set; }
    }

    private class CreatedContractResponse
    {
        public string Id { get; set; } = string.Empty;
    }

    private class MigrationLogEntry
    {
        public string OldId { get; set; } = string.Empty;
        public string? NewProviderContractId { get; set; }
        public string? NewRateConfigId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Errors { get; set; }
    }
}
