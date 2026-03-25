using ClaimsScrubbingService.Models;
using MongoDB.Driver;

namespace ClaimsScrubbingService.Repositories;

public interface IScrubRuleRepository
{
    Task<IReadOnlyList<CustomRule>> LoadCustomRulesAsync();
}

public class ScrubRuleRepository : IScrubRuleRepository
{
    private readonly IMongoCollection<CustomRule> _collection;
    private readonly ILogger<ScrubRuleRepository> _logger;

    // Collection name matches Node.js version
    private const string CollectionName = "ScrubRules";

    public ScrubRuleRepository(IMongoDatabase database, ILogger<ScrubRuleRepository> logger)
    {
        _logger     = logger;
        _collection = database.GetCollection<CustomRule>(CollectionName);
    }

    /// <summary>
    /// Loads all enabled custom rules from MongoDB.
    /// Equivalent to the Node.js <c>rulesCollection.find({ type: 'custom', enabled: true })</c>.
    /// </summary>
    public async Task<IReadOnlyList<CustomRule>> LoadCustomRulesAsync()
    {
        try
        {
            var filter = Builders<CustomRule>.Filter.And(
                Builders<CustomRule>.Filter.Eq(r => r.Type, "custom"),
                Builders<CustomRule>.Filter.Eq(r => r.Enabled, true));

            var rules = await _collection.Find(filter).ToListAsync();
            _logger.LogInformation("Loaded {Count} custom rules from MongoDB", rules.Count);
            return rules;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load custom rules from MongoDB");
            return Array.Empty<CustomRule>();
        }
    }
}
