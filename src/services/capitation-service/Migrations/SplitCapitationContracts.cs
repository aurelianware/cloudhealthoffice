using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using MongoDB.Driver;

namespace CapitationService.Migrations;

/// <summary>
/// One-time migration: splits capitation_contracts documents into
/// ProviderContract (master) + CapitationRateConfig (child) records.
///
/// Usage: dotnet run --project src/services/capitation-service -- --migrate-split
/// (run by an operator with capitation-service's own configuration:
/// <c>MongoDb</c>, <c>ProviderContractsService:BaseUrl</c> and <c>ChoAuth</c>,
/// including <c>ChoAuth:ServiceToken</c>; outside Development/Testing that is
/// <c>Source=TokenService</c>, run from a pod with capitation-service's workload identity).
///
/// Credentials: the CLI has no inbound caller, so there is no user token to
/// forward. It sends a short-lived CHO service token as capitation-service
/// (<c>ChoAuth:ServiceToken</c>, the source the running service uses for
/// its background calls) through the shared <see cref="ChoOutboundTokenHandler"/>,
/// one per call, for the tenant the call names in <c>X-Tenant-ID</c>: each
/// legacy document's own tenant. A document with no tenant is not migrated.
/// Without a service-token key the CLI aborts before touching anything.
///
/// Prerequisites (see Pre-Flight Checklist):
///   1. provider-contracts-service running at configured URL
///   2. MongoDB backup completed
///   3. Document count captured
/// </summary>
public static class SplitCapitationContracts
{
    /// <summary>The factory client the migration calls provider-contracts-service with.</summary>
    public const string HttpClientName = "ProviderContractsService";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The migration's services: the shared CHO outbound token handler on an
    /// <see cref="IHttpClientFactory"/> client, with capitation-service's
    /// service-token signing key from <c>ChoAuth</c>. Fails when no service
    /// token is configured (every call would be rejected).
    /// </summary>
    public static ServiceProvider BuildServices(
        IConfiguration configuration,
        string environmentName,
        string providerContractsServiceUrl,
        Action<IHttpClientBuilder>? configureClient = null)
    {
        var options = new ChoAuthOptions();
        configuration.GetSection(ChoAuthOptions.SectionName).Bind(options);
        var allowSymmetricKeys = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
        options.Validate(allowSymmetricKeys);
        if (options.ServiceToken == null)
            throw new InvalidOperationException(
                $"{ChoAuthOptions.SectionName}:ServiceToken is not configured. The migration calls provider-contracts-service " +
                "as capitation-service and needs its service-token signing key.");

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddConsole());
        services.AddSingleton(options);
        // Deployed: token-service issues the token for this workload identity;
        // Development/Testing: the local development key.
        services.AddChoServiceTokenSource(options);
        services.AddHttpContextAccessor();
        // The outbound handler only attaches tokens to allowlisted CHO hosts.
        // This CLI calls exactly one: the provider-contracts URL it was given.
        var outboundConfiguration = new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ProviderContractsService"] = providerContractsServiceUrl
            })
            .Build();
        services.AddSingleton(new ChoOutboundHosts(outboundConfiguration));
        services.AddTransient<ChoOutboundTokenHandler>();

        var client = services.AddHttpClient(HttpClientName, c =>
        {
            c.BaseAddress = new Uri(providerContractsServiceUrl);
            c.Timeout = TimeSpan.FromSeconds(30);
        }).AddChoServiceAuthentication();
        configureClient?.Invoke(client);

        return services.BuildServiceProvider();
    }

    /// <summary>Checks that provider-contracts-service answers for <paramref name="tenantId"/>.</summary>
    public static async Task<HttpResponseMessage> PreflightAsync(IHttpClientFactory factory, string tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/contracts");
        request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
        return await factory.CreateClient(HttpClientName).SendAsync(request);
    }

    /// <summary>
    /// Creates the ProviderContract for one legacy document in that document's
    /// tenant and returns its id.
    /// </summary>
    public static async Task<string> CreateProviderContractAsync(IHttpClientFactory factory, OldCapitationContract doc)
    {
        if (string.IsNullOrWhiteSpace(doc.TenantId))
            throw new InvalidOperationException("Document has no tenant; it cannot be migrated.");

        // provider-contracts-service takes the tenant and the acting identity
        // from the token; the body's tenantId/createdBy are informational.
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

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/contracts")
        {
            Content = JsonContent.Create(providerContract, options: JsonOptions)
        };
        request.Headers.Add(TenantMiddleware.TenantHeaderName, doc.TenantId);
        using var postResponse = await factory.CreateClient(HttpClientName).SendAsync(request);
        if (!postResponse.IsSuccessStatusCode)
        {
            var errorBody = await postResponse.Content.ReadAsStringAsync();
            throw new Exception($"POST /api/v1/contracts failed ({postResponse.StatusCode}): {errorBody}");
        }

        var createdContract = await postResponse.Content.ReadFromJsonAsync<CreatedContractResponse>(JsonOptions);
        return createdContract?.Id is { Length: > 0 } id ? id : throw new Exception("No Id returned from POST");
    }

    public static async Task<int> RunAsync(
        string mongoConnectionString,
        string databaseName,
        string providerContractsServiceUrl,
        IConfiguration configuration,
        string environmentName)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine("  CapitationContract → ProviderContract + CapitationRateConfig");
        Console.WriteLine("  Migration Script — Cloud Health Office");
        Console.WriteLine("═══════════════════════════════════════════════════════════");
        Console.WriteLine();

        // ── Step 1: Credentials ─────────────────────────────────────────
        ServiceProvider services;
        try
        {
            services = BuildServices(configuration, environmentName, providerContractsServiceUrl);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"ABORT: {ex.Message}");
            return 1;
        }
        await using var _ = services;
        var factory = services.GetRequiredService<IHttpClientFactory>();

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

        // ── Pre-flight: provider-contracts-service, once per tenant ─────
        foreach (var tenantId in documents.Select(d => d.TenantId).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct())
        {
            Console.WriteLine($"[PRE-FLIGHT] Checking provider-contracts-service for tenant {tenantId}...");
            try
            {
                using var response = await PreflightAsync(factory, tenantId);
                if (!response.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"ABORT: provider-contracts-service returned {response.StatusCode} at {providerContractsServiceUrl} for tenant {tenantId}.");
                    Console.Error.WriteLine("See pre-flight checklist in CHO-Finance-Spec-v2_1.md.");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ABORT: provider-contracts-service not reachable at {providerContractsServiceUrl}.");
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.Error.WriteLine("See pre-flight checklist in CHO-Finance-Spec-v2_1.md.");
                return 1;
            }
        }
        Console.WriteLine($"[PRE-FLIGHT] OK — provider-contracts-service reachable at {providerContractsServiceUrl}");

        // ── Step 3 & 4: Migrate ─────────────────────────────────────────
        var migrationLog = new List<MigrationLogEntry>();
        int succeeded = 0;
        int failed = 0;
        int total = documents.Count;

        foreach (var (doc, index) in documents.Select((d, i) => (d, i)))
        {
            var logEntry = new MigrationLogEntry { OldId = doc.Id };
            Console.Write($"Migrating {index + 1}/{total}: {doc.ContractNumber}... ");

            try
            {
                // Create ProviderContract via API, in the document's tenant
                var newContractId = await CreateProviderContractAsync(factory, doc);

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

    /// <summary>A legacy capitation_contracts document.</summary>
    public class OldCapitationContract
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
