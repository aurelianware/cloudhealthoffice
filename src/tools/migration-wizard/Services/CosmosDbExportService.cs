using Microsoft.Azure.Cosmos;
using MigrationWizard.Models;

namespace MigrationWizard.Services;

/// <summary>
/// Service for exporting migrated data to Cloud Health Office Cosmos DB
/// </summary>
public class CosmosDbExportService : IAsyncDisposable
{
    private readonly CosmosDbConfig _config;
    private readonly ILogger<CosmosDbExportService> _logger;
    private CosmosClient? _client;
    private Database? _database;
    private Container? _membersContainer;
    private Container? _providersContainer;
    private Container? _benefitPlansContainer;
    private bool _initialized;

    public CosmosDbExportService(
        CosmosDbConfig config,
        ILogger<CosmosDbExportService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initialize Cosmos DB connection
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            _logger.LogInformation("Initializing Cosmos DB connection to {Endpoint}", _config.Endpoint);

            _client = new CosmosClient(_config.Endpoint, _config.Key, new CosmosClientOptions
            {
                AllowBulkExecution = true,
                MaxRetryAttemptsOnRateLimitedRequests = 9,
                MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(30)
            });

            _database = _client.GetDatabase(_config.DatabaseName);
            _membersContainer = _database.GetContainer(_config.MembersContainer);
            _providersContainer = _database.GetContainer(_config.ProvidersContainer);
            
            // Create BenefitPlans container if it doesn't exist
            // Use configured throughput (0 for serverless)
            var throughput = _config.DefaultThroughput > 0 ? _config.DefaultThroughput : (int?)null;
            _benefitPlansContainer = await _database.CreateContainerIfNotExistsAsync(
                _config.BenefitPlansContainer,
                "/planCode",
                throughput: throughput);

            _initialized = true;
            _logger.LogInformation("Cosmos DB connection initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Cosmos DB connection");
            throw;
        }
    }

    /// <summary>
    /// Test connection to Cosmos DB
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            await InitializeAsync();
            
            // Try to read database properties
            var response = await _database!.ReadAsync();
            _logger.LogInformation("Cosmos DB connection test successful. Database: {Id}", response.Resource.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cosmos DB connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Export member to Cosmos DB
    /// </summary>
    public async Task<bool> ExportMemberAsync(BackendMember member)
    {
        await InitializeAsync();

        try
        {
            var cosmosDoc = MapToCosmosDbMember(member);
            
            await _membersContainer!.UpsertItemAsync(
                cosmosDoc,
                new PartitionKey(cosmosDoc.MemberId));

            _logger.LogDebug("Exported member {MemberId} to Cosmos DB", member.MemberId);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            _logger.LogWarning("Rate limited while exporting member {MemberId}. Retrying...", member.MemberId);
            await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(1));
            return await ExportMemberAsync(member);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export member {MemberId}", member.MemberId);
            return false;
        }
    }

    /// <summary>
    /// Export multiple members in batch
    /// </summary>
    public async Task<(int succeeded, int failed)> ExportMembersBatchAsync(
        IEnumerable<BackendMember> members,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        var succeeded = 0;
        var failed = 0;
        var processed = 0;

        var tasks = new List<Task>();
        
        foreach (var member in members)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var cosmosDoc = MapToCosmosDbMember(member);
            
            tasks.Add(_membersContainer!.UpsertItemAsync(
                cosmosDoc,
                new PartitionKey(cosmosDoc.MemberId),
                cancellationToken: cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        Interlocked.Increment(ref succeeded);
                    else
                        Interlocked.Increment(ref failed);
                    
                    progress?.Report(Interlocked.Increment(ref processed));
                }, cancellationToken));

            // Batch requests to avoid overwhelming Cosmos DB
            if (tasks.Count >= 100)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        _logger.LogInformation("Batch export completed. Succeeded: {Succeeded}, Failed: {Failed}", succeeded, failed);
        return (succeeded, failed);
    }

    /// <summary>
    /// Export provider to Cosmos DB
    /// </summary>
    public async Task<bool> ExportProviderAsync(BackendProvider provider)
    {
        await InitializeAsync();

        try
        {
            var cosmosDoc = MapToCosmosDbProvider(provider);
            
            await _providersContainer!.UpsertItemAsync(
                cosmosDoc,
                new PartitionKey(cosmosDoc.Npi));

            _logger.LogDebug("Exported provider {Npi} to Cosmos DB", provider.Npi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export provider {Npi}", provider.Npi);
            return false;
        }
    }

    /// <summary>
    /// Export multiple providers in batch
    /// </summary>
    public async Task<(int succeeded, int failed)> ExportProvidersBatchAsync(
        IEnumerable<BackendProvider> providers,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        var succeeded = 0;
        var failed = 0;
        var processed = 0;

        var tasks = new List<Task>();
        
        foreach (var provider in providers)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var cosmosDoc = MapToCosmosDbProvider(provider);
            
            tasks.Add(_providersContainer!.UpsertItemAsync(
                cosmosDoc,
                new PartitionKey(cosmosDoc.Npi),
                cancellationToken: cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        Interlocked.Increment(ref succeeded);
                    else
                        Interlocked.Increment(ref failed);
                    
                    progress?.Report(Interlocked.Increment(ref processed));
                }, cancellationToken));

            if (tasks.Count >= 100)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        return (succeeded, failed);
    }

    /// <summary>
    /// Export benefit plan to Cosmos DB
    /// </summary>
    public async Task<bool> ExportBenefitPlanAsync(BackendBenefitPlan plan)
    {
        await InitializeAsync();

        try
        {
            var cosmosDoc = MapToCosmosDbBenefitPlan(plan);
            
            await _benefitPlansContainer!.UpsertItemAsync(
                cosmosDoc,
                new PartitionKey(cosmosDoc.PlanCode));

            _logger.LogDebug("Exported benefit plan {PlanCode} to Cosmos DB", plan.PlanCode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export benefit plan {PlanCode}", plan.PlanCode);
            return false;
        }
    }

    /// <summary>
    /// Export multiple benefit plans in batch
    /// </summary>
    public async Task<(int succeeded, int failed)> ExportBenefitPlansBatchAsync(
        IEnumerable<BackendBenefitPlan> plans,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();

        var succeeded = 0;
        var failed = 0;
        var processed = 0;

        var tasks = new List<Task>();
        
        foreach (var plan in plans)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var cosmosDoc = MapToCosmosDbBenefitPlan(plan);
            
            tasks.Add(_benefitPlansContainer!.UpsertItemAsync(
                cosmosDoc,
                new PartitionKey(cosmosDoc.PlanCode),
                cancellationToken: cancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        Interlocked.Increment(ref succeeded);
                    else
                        Interlocked.Increment(ref failed);
                    
                    progress?.Report(Interlocked.Increment(ref processed));
                }, cancellationToken));

            if (tasks.Count >= 50)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
        }

        return (succeeded, failed);
    }

    private CosmosDbMember MapToCosmosDbMember(BackendMember member)
    {
        return new CosmosDbMember
        {
            Id = member.MemberId,
            MemberId = member.MemberId,
            SubscriberId = member.SubscriberId,
            FirstName = member.FirstName,
            LastName = member.LastName,
            DateOfBirth = member.DateOfBirth.ToString("yyyy-MM-dd"),
            Gender = member.Gender,
            PlanCode = member.PlanCode,
            GroupNumber = member.GroupNumber,
            EffectiveDate = member.EffectiveDate.ToString("yyyy-MM-dd"),
            TerminationDate = member.TerminationDate?.ToString("yyyy-MM-dd"),
            RelationshipCode = member.RelationshipCode,
            Address = member.Address != null ? new CosmosDbAddress
            {
                Line1 = member.Address.Line1,
                Line2 = member.Address.Line2,
                City = member.Address.City,
                State = member.Address.State,
                ZipCode = member.Address.ZipCode
            } : null,
            PhoneNumber = member.PhoneNumber,
            Email = member.Email,
            Source = "Backend-Migration",
            MigratedAt = DateTime.UtcNow.ToString("O")
        };
    }

    private CosmosDbProvider MapToCosmosDbProvider(BackendProvider provider)
    {
        return new CosmosDbProvider
        {
            Id = provider.Npi,
            Npi = provider.Npi,
            ProviderId = provider.ProviderId,
            TaxId = provider.TaxId,
            FirstName = provider.FirstName,
            LastName = provider.LastName,
            OrganizationName = provider.OrganizationName,
            ProviderType = provider.ProviderType,
            Specialty = provider.Specialty,
            TaxonomyCode = provider.TaxonomyCode,
            IsParticipating = provider.IsParticipating,
            PracticeAddress = provider.PracticeAddress != null ? new CosmosDbAddress
            {
                Line1 = provider.PracticeAddress.Line1,
                Line2 = provider.PracticeAddress.Line2,
                City = provider.PracticeAddress.City,
                State = provider.PracticeAddress.State,
                ZipCode = provider.PracticeAddress.ZipCode
            } : null,
            Phone = provider.Phone,
            ContractEffectiveDate = provider.ContractEffectiveDate?.ToString("yyyy-MM-dd"),
            ContractTerminationDate = provider.ContractTerminationDate?.ToString("yyyy-MM-dd"),
            Source = "Backend-Migration",
            MigratedAt = DateTime.UtcNow.ToString("O")
        };
    }

    private CosmosDbBenefitPlan MapToCosmosDbBenefitPlan(BackendBenefitPlan plan)
    {
        return new CosmosDbBenefitPlan
        {
            Id = plan.PlanId,
            PlanId = plan.PlanId,
            PlanCode = plan.PlanCode,
            PlanName = plan.PlanName,
            PlanType = plan.PlanType,
            ProductType = plan.ProductType,
            LineOfBusiness = plan.LineOfBusiness,
            EffectiveDate = plan.EffectiveDate.ToString("yyyy-MM-dd"),
            TerminationDate = plan.TerminationDate?.ToString("yyyy-MM-dd"),
            Benefits = plan.Benefits.Select(b => new CosmosDbBenefit
            {
                ServiceTypeCode = b.ServiceTypeCode,
                ServiceTypeName = b.ServiceTypeName,
                IsCovered = b.IsCovered,
                RequiresPriorAuth = b.RequiresPriorAuth,
                Copay = b.Copay,
                Coinsurance = b.Coinsurance,
                DeductibleAmount = b.DeductibleAmount
            }).ToList(),
            CostShare = plan.CostShare != null ? new CosmosDbCostShare
            {
                IndividualDeductible = plan.CostShare.IndividualDeductible,
                FamilyDeductible = plan.CostShare.FamilyDeductible,
                IndividualOutOfPocketMax = plan.CostShare.IndividualOutOfPocketMax,
                FamilyOutOfPocketMax = plan.CostShare.FamilyOutOfPocketMax
            } : null,
            Source = "Backend-Migration",
            MigratedAt = DateTime.UtcNow.ToString("O")
        };
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
    }
}
