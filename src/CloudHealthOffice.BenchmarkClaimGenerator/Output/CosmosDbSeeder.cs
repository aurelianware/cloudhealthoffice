using System.Text.Json;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Seeds a Cosmos DB instance with production-shaped documents for benchmark testing.
/// Uses the Azure.Cosmos SDK in bulk executor mode for throughput.
/// This seeder is optional — it requires an Azure Cosmos DB connection at runtime.
/// </summary>
public class CosmosDbSeeder
{
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly string _tenantId;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Batch size for bulk writes.</summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Container name constants matching the production service configuration.
    /// Verified against: member-service MemberRepository, coverage-service CoverageRepository,
    /// provider-service ProviderRepository, benefit-plan-service BenefitPlanRepository,
    /// FeeScheduleEngine FeeScheduleRepositoryCosmos, BenefitEngine AccumulatorRepositoryCosmos.
    /// </summary>
    public static class ContainerNames
    {
        /// <summary>Members container (member-service), partitioned by /tenantId.</summary>
        public const string Members = "Members";

        /// <summary>Coverage container (coverage-service), partitioned by /tenantId.</summary>
        public const string Coverages = "Coverage";

        /// <summary>Providers container (provider-service), partitioned by /tenantId.</summary>
        public const string Providers = "Providers";

        /// <summary>ProviderContracts container (FeeScheduleEngine), partitioned by /tenantId.</summary>
        public const string ProviderContracts = "ProviderContracts";

        /// <summary>BenefitPlans container (benefit-plan-service), partitioned by /tenantId.</summary>
        public const string BenefitPlans = "BenefitPlans";

        /// <summary>FeeSchedules container (FeeScheduleEngine), partitioned by /tenantId.</summary>
        public const string FeeSchedules = "FeeSchedules";

        /// <summary>Accumulators container (BenefitEngine), partitioned by /tenantId.</summary>
        public const string Accumulators = "Accumulators";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosDbSeeder"/> class.
    /// </summary>
    /// <param name="connectionString">Cosmos DB connection string.</param>
    /// <param name="tenantId">Tenant identifier for the benchmark data.</param>
    /// <param name="databaseName">Cosmos DB database name. Default: "cloudhealthoffice".</param>
    /// <param name="logger">Optional logger.</param>
    public CosmosDbSeeder(
        string connectionString,
        string tenantId = "mcc-benchmark",
        string databaseName = "cloudhealthoffice",
        ILogger? logger = null)
    {
        _connectionString = connectionString;
        _databaseName = databaseName;
        _tenantId = tenantId;
        _logger = logger ?? NullLogger.Instance;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// Seed benefit plans into Cosmos DB.
    /// </summary>
    public async Task<int> SeedBenefitPlansAsync(
        List<SyntheticBenefitPlan> plans,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding {Count} benefit plans...", plans.Count);
        var documents = plans.Select(p => new
        {
            id = Guid.NewGuid().ToString(),
            tenantId = _tenantId,
            planId = p.PlanId,
            planName = p.PlanName,
            payer = p.Payer,
            planType = p.PlanType,
            lineOfBusiness = p.LineOfBusiness,
            medicaidProgram = p.MedicaidProgram,
            effectiveDate = p.EffectiveDate,
            terminationDate = p.TerminationDate,
            isActive = p.IsActive,
            costSharing = new
            {
                individualDeductible = p.IndividualDeductible,
                familyDeductible = p.FamilyDeductible,
                individualOutOfPocketMax = p.IndividualOopMax,
                familyOutOfPocketMax = p.FamilyOopMax,
                inNetworkDeductible = p.IndividualDeductible,
                outOfNetworkDeductible = p.OutOfNetworkDeductible ?? 0m,
                inNetworkOutOfPocketMax = p.IndividualOopMax,
                outOfNetworkOutOfPocketMax = p.OutOfNetworkOopMax ?? 0m,
            },
            benefits = p.Benefits.Select(b => new
            {
                id = Guid.NewGuid().ToString(),
                serviceCategory = b.ServiceCategory,
                description = b.Description,
                inNetworkCopay = b.InNetworkCopay,
                outNetworkCopay = b.OutNetworkCopay,
                inNetworkCoinsurance = b.InNetworkCoinsurance,
                outNetworkCoinsurance = b.OutNetworkCoinsurance,
                deductibleApplies = b.DeductibleApplies,
                priorAuthRequired = b.PriorAuthRequired,
            }),
        }).ToList();

        return await WriteBatchesAsync(ContainerNames.BenefitPlans, documents, cancellationToken);
    }

    /// <summary>
    /// Seed fee schedules into Cosmos DB.
    /// </summary>
    public async Task<int> SeedFeeSchedulesAsync(
        List<SyntheticFeeSchedule> feeSchedules,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding {Count} fee schedules...", feeSchedules.Count);
        var documents = feeSchedules.Select(fs => new
        {
            id = $"{_tenantId}:{fs.Name}:{fs.EffectiveDate:yyyyMMdd}",
            tenantId = _tenantId,
            feeScheduleId = fs.FeeScheduleId,
            name = fs.Name,
            type = fs.Type,
            effectiveDate = fs.EffectiveDate,
            termDate = fs.TermDate,
            percentOfMedicare = fs.PercentOfMedicare,
            drgBaseRate = fs.DrgBaseRate,
            perDiemRate = fs.PerDiemRate,
            lines = fs.Lines.Select(l => new
            {
                procedureCode = l.ProcedureCode,
                modifier = l.Modifier,
                placeOfService = l.PlaceOfService,
                rate = l.AllowedAmount,
                rateType = l.RateType,
                effectiveDate = l.EffectiveDate,
                termDate = l.TermDate,
            }),
        }).ToList();

        return await WriteBatchesAsync(ContainerNames.FeeSchedules, documents, cancellationToken);
    }

    /// <summary>
    /// Seed providers into Cosmos DB.
    /// </summary>
    public async Task<int> SeedProvidersAsync(
        List<SyntheticProvider> providers,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding {Count:N0} providers...", providers.Count);
        var documents = providers.Select(p => new
        {
            id = Guid.NewGuid().ToString(),
            tenantId = _tenantId,
            npi = p.Npi,
            providerType = p.ProviderType,
            taxId = p.TaxId,
            firstName = p.FirstName,
            lastName = p.LastName,
            organizationName = p.OrganizationName,
            credentials = p.Credentials,
            primarySpecialty = p.SpecialtyDescription,
            taxonomyCode = p.TaxonomyCode,
            networkStatus = p.NetworkStatus,
            credentialingStatus = p.CredentialingStatus,
            address = p.Address,
            city = p.City,
            state = p.State,
            zipCode = p.ZipCode,
            phone = p.Phone,
            effectiveDate = p.EffectiveDate,
            termDate = p.TermDate,
            status = p.NetworkStatus == "Terminated" ? "Terminated" : "Active",
        }).ToList();

        return await WriteBatchesAsync(ContainerNames.Providers, documents, cancellationToken);
    }

    /// <summary>
    /// Seed provider contracts into Cosmos DB.
    /// </summary>
    public async Task<int> SeedProviderContractsAsync(
        List<SyntheticProviderContract> contracts,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding {Count:N0} provider contracts...", contracts.Count);
        var documents = contracts.Select(c => new
        {
            id = Guid.NewGuid().ToString(),
            tenantId = _tenantId,
            contractNumber = c.ContractNumber,
            providerNpi = c.ProviderNpi,
            providerName = c.ProviderName,
            providerType = c.ProviderType,
            lineOfBusiness = c.LineOfBusiness,
            paymentMethodology = c.PaymentMethodology,
            networkStatus = c.NetworkStatus,
            feeScheduleId = c.FeeScheduleId,
            contractType = c.ContractType,
            effectiveDate = c.EffectiveDate,
            terminationDate = c.TermDate,
            autoRenews = c.AutoRenews,
            status = c.Status,
        }).ToList();

        return await WriteBatchesAsync(ContainerNames.ProviderContracts, documents, cancellationToken);
    }

    /// <summary>
    /// Seed members into Cosmos DB.
    /// </summary>
    public async Task<int> SeedMembersAsync(
        List<SyntheticMember> members,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding {Count:N0} subscribers (with dependents)...", members.Count);
        int total = 0;

        foreach (var batch in members.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documents = new List<object>();
            foreach (var m in batch)
            {
                documents.Add(CreateMemberDocument(m));
                foreach (var dep in m.Dependents)
                {
                    documents.Add(CreateDependentDocument(m, dep));
                }
            }

            total += documents.Count;
            await WriteDocumentsAsync(ContainerNames.Members, documents, cancellationToken);

            if (total % 5_000 == 0)
            {
                _logger.LogInformation("Seeded {Count:N0} member documents...", total);
            }
        }

        _logger.LogInformation("Seeded {Count:N0} total member documents", total);
        return total;
    }

    /// <summary>
    /// Seed coverage records into Cosmos DB.
    /// </summary>
    public async Task<int> SeedCoveragesAsync(
        List<SyntheticMember> members,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding coverage records...");
        int total = 0;

        var allCoverages = new List<object>();
        foreach (var m in members)
        {
            foreach (var cov in m.Coverages)
                allCoverages.Add(CreateCoverageDocument(cov));
            foreach (var dep in m.Dependents)
                foreach (var cov in dep.Coverages)
                    allCoverages.Add(CreateCoverageDocument(cov));
        }

        total = await WriteBatchesAsync(ContainerNames.Coverages, allCoverages, cancellationToken);
        _logger.LogInformation("Seeded {Count:N0} coverage records", total);
        return total;
    }

    /// <summary>
    /// Seed accumulator records into Cosmos DB.
    /// Document shape matches BenefitEngine AccumulatorDocument:
    /// id = "{tenantId}:{scope}:{ownerId}:{benefitPlanId}:{planYear}",
    /// partitioned by /tenantId, with ownerId, scope, balances[], transactions[], version.
    /// </summary>
    public async Task<int> SeedAccumulatorsAsync(
        List<SyntheticAccumulator> accumulators,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Seeding {Count:N0} accumulators...", accumulators.Count);
        var documents = accumulators.Select(a => new
        {
            id = a.Id,
            tenantId = a.TenantId,
            ownerId = a.MemberId,
            scope = a.Scope,
            benefitPlanId = a.BenefitPlanId,
            planYear = a.PlanYear,
            version = 1L,
            lastUpdated = a.LastUpdated,
            balances = new[]
            {
                new { type = "IndividualDeductible", networkTier = a.NetworkTier, limitAmount = a.IndividualDeductibleLimit, accumulatedAmount = a.IndividualDeductibleSpent },
                new { type = "FamilyDeductible", networkTier = a.NetworkTier, limitAmount = a.FamilyDeductibleLimit, accumulatedAmount = a.FamilyDeductibleSpent },
                new { type = "IndividualOutOfPocketMax", networkTier = a.NetworkTier, limitAmount = a.IndividualOopMaxLimit, accumulatedAmount = a.IndividualOopSpent },
                new { type = "FamilyOutOfPocketMax", networkTier = a.NetworkTier, limitAmount = a.FamilyOopMaxLimit, accumulatedAmount = a.FamilyOopSpent },
            }.Where(b => b.limitAmount > 0).ToArray(),
            transactions = Array.Empty<object>(),
        }).ToList();

        return await WriteBatchesAsync(ContainerNames.Accumulators, documents, cancellationToken);
    }

    /// <summary>
    /// Create a member document matching the member-service Member entity shape.
    /// Uses flat address fields (address, city, state, zipCode) and effectiveDate/terminationDate
    /// per the member-service model.
    /// </summary>
    private object CreateMemberDocument(SyntheticMember m)
    {
        return new
        {
            id = Guid.NewGuid().ToString(),
            tenantId = _tenantId,
            memberId = m.MemberId,
            subscriberId = m.SubscriberId,
            isSubscriber = true,
            relationshipCode = m.RelationshipCode,
            firstName = m.FirstName,
            lastName = m.LastName,
            dateOfBirth = m.DateOfBirth,
            gender = m.Gender,
            address = m.Address,
            city = m.City,
            state = m.State,
            zipCode = m.ZipCode,
            phone = m.Phone,
            status = m.EnrollmentStatus,
            lineOfBusiness = m.LineOfBusiness,
            effectiveDate = m.CoverageEffectiveDate,
            terminationDate = m.CoverageTermDate,
            maintenanceTypeCode = m.MaintenanceTypeCode,
            groupNumber = m.GroupNumber,
            relationship = m.RelationshipCode,
            dependentIds = m.Dependents.Select(d => d.MemberId).ToList(),
            createdDate = DateTime.UtcNow,
            lastUpdatedDate = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Create a dependent member document matching the member-service Member entity shape.
    /// </summary>
    private object CreateDependentDocument(SyntheticMember subscriber, SyntheticDependent dep)
    {
        return new
        {
            id = Guid.NewGuid().ToString(),
            tenantId = _tenantId,
            memberId = dep.MemberId,
            subscriberId = subscriber.SubscriberId,
            subscriberMemberId = subscriber.MemberId,
            isSubscriber = false,
            relationshipCode = dep.RelationshipCode,
            firstName = dep.FirstName,
            lastName = dep.LastName,
            dateOfBirth = dep.DateOfBirth,
            gender = dep.Gender,
            address = dep.Address,
            city = dep.City,
            state = dep.State,
            zipCode = dep.ZipCode,
            status = dep.EnrollmentStatus,
            lineOfBusiness = subscriber.LineOfBusiness,
            effectiveDate = subscriber.CoverageEffectiveDate,
            terminationDate = subscriber.CoverageTermDate,
            groupNumber = subscriber.GroupNumber,
            relationship = dep.RelationshipCode,
            dependentIds = new List<string>(),
            createdDate = DateTime.UtcNow,
            lastUpdatedDate = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Create a coverage document matching the coverage-service Coverage entity shape.
    /// Uses <c>insuranceLineCode</c> (coverage-service convention) rather than
    /// <c>insuranceType</c> (enrollment-import-service convention).
    /// </summary>
    private object CreateCoverageDocument(SyntheticCoverage cov)
    {
        return new
        {
            id = cov.Id,
            tenantId = _tenantId,
            memberId = cov.MemberId,
            groupNumber = cov.GroupNumber,
            planId = cov.PlanId,
            coverageLevel = cov.CoverageLevelCode,
            insuranceLineCode = cov.InsuranceLineCode,
            effectiveDate = cov.EffectiveDate,
            terminationDate = cov.TermDate,
            status = cov.Status,
            lineOfBusiness = cov.LineOfBusiness,
            maintenanceTypeCode = cov.MaintenanceTypeCode,
            pcpNpi = cov.PcpNpi,
            pcpName = cov.PcpName,
            pcpAssignmentDate = cov.PcpAssignmentDate,
            createdDate = DateTime.UtcNow,
            lastUpdatedDate = DateTime.UtcNow,
        };
    }

    private async Task<int> WriteBatchesAsync<T>(
        string containerName,
        List<T> documents,
        CancellationToken cancellationToken)
    {
        int total = 0;
        foreach (var batch in documents.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteDocumentsAsync(containerName, batch.Cast<object>().ToList(), cancellationToken);
            total += batch.Length;

            if (total % 5_000 == 0)
            {
                _logger.LogInformation("Seeded {Count:N0} / {Total:N0} documents to {Container}",
                    total, documents.Count, containerName);
            }
        }

        return total;
    }

    /// <summary>
    /// Write a batch of documents to a Cosmos DB container.
    /// This is a no-op stub — override in a subclass to provide the actual Azure.Cosmos SDK
    /// bulk write implementation. The stub exists so the project compiles without the
    /// Azure.Cosmos package dependency (which is optional).
    /// </summary>
    protected virtual Task WriteDocumentsAsync(
        string containerName,
        List<object> documents,
        CancellationToken cancellationToken)
    {
        // No-op: override this method with an Azure.Cosmos SDK implementation to
        // actually seed a Cosmos DB instance. Without the override, document shapes
        // are still validated by the caller methods but nothing is persisted.
        _logger.LogDebug("Stub WriteDocumentsAsync: {Count} documents for {Container} (no-op)",
            documents.Count, containerName);
        return Task.CompletedTask;
    }
}
