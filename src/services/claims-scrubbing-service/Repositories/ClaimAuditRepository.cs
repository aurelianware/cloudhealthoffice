using ClaimsScrubbingService.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ClaimsScrubbingService.Repositories;

public interface IClaimAuditRepository
{
    Task InsertAuditAsync(X12837Claim claim, ClaimValidationResult result, string correlationId);
}

public class ClaimAuditRepository : IClaimAuditRepository
{
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly ILogger<ClaimAuditRepository> _logger;

    // Collection name matches Node.js version
    private const string CollectionName = "ScrubAudit";

    public ClaimAuditRepository(IMongoDatabase database, ILogger<ClaimAuditRepository> logger)
    {
        _logger     = logger;
        _collection = database.GetCollection<BsonDocument>(CollectionName);

        // Ensure TTL index on expireAt (90-day TTL matching Node.js version)
        EnsureTtlIndex();
    }

    private void EnsureTtlIndex()
    {
        try
        {
            var indexKeys  = Builders<BsonDocument>.IndexKeys.Ascending("expireAt");
            var indexModel = new CreateIndexModel<BsonDocument>(
                indexKeys,
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero, Background = true });

            _collection.Indexes.CreateOne(indexModel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure TTL index on {Collection}", CollectionName);
        }
    }

    public async Task InsertAuditAsync(X12837Claim claim, ClaimValidationResult result, string correlationId)
    {
        try
        {
            var editCodes = result.Results
                .Where(r => !r.Passed && r.EditCode != null)
                .Select(r => r.EditCode)
                .ToList();

            var doc = new BsonDocument
            {
                ["claimId"]              = claim.ClaimId,
                ["claimType"]            = claim.ClaimType,
                ["patientControlNumber"] = claim.ClaimHeader.PatientControlNumber,
                ["billingProviderNpi"]   = claim.BillingProvider.Npi,
                ["memberId"]             = claim.Subscriber.MemberId,
                ["validationStatus"]     = result.Status,
                ["errorCount"]           = result.ErrorCount,
                ["warningCount"]         = result.WarningCount,
                ["rulesExecuted"]        = result.RulesExecuted,
                ["rulesPassed"]          = result.RulesPassed,
                ["rulesFailed"]          = result.RulesFailed,
                ["routingDestination"]   = result.Routing.Destination,
                ["editCodes"]            = new BsonArray(editCodes),
                ["validationTimeMs"]     = result.TotalValidationTimeMs,
                ["correlationId"]        = correlationId,
                ["timestamp"]            = DateTime.UtcNow,
                // 90-day TTL — MongoDB's TTL index will delete documents when expireAt is reached
                ["expireAt"]             = DateTime.UtcNow.AddDays(90)
            };

            await _collection.InsertOneAsync(doc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert audit record for claim {ClaimId}", claim.ClaimId);
            // Non-fatal: do not rethrow
        }
    }
}
