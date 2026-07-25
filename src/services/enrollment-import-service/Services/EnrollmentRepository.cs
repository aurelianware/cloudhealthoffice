using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Services;

/// <summary>
/// Coverage is the one entity enrollment-import-service still writes
/// directly rather than delegating to an owning service — coverage-service's
/// CreateCoverage requires a resolved PlanId that a raw 834 doesn't carry
/// (it only has InsuranceLineCode/PlanCoverageDescription/CoverageLevel), so
/// delegating this one needs a benefit-plan-service lookup this change
/// doesn't attempt. Member/Sponsor used to live here too — see
/// IMemberServiceClient/ISponsorServiceClient for why those moved.
/// </summary>
public interface IEnrollmentRepository
{
    Task<Coverage> CreateCoverageAsync(Coverage coverage);
    Task<Coverage> UpdateCoverageAsync(Coverage coverage);
}

/// <summary>
/// MongoDB repository for Coverage. Index creation is handled at startup by
/// <c>EnrollmentIndexInitializer</c> so the repository can be registered as
/// a singleton and constructed without I/O side effects (same pattern as
/// member-service's MemberRepositoryMongo).
/// </summary>
public class EnrollmentRepository : IEnrollmentRepository
{
    private readonly IMongoCollection<Coverage> _coverage;
    private readonly ILogger<EnrollmentRepository> _logger;

    public EnrollmentRepository(IMongoDatabase database, ILogger<EnrollmentRepository> logger)
    {
        _coverage = database.GetCollection<Coverage>("Coverage");
        _logger = logger;
    }

    public async Task<Coverage> CreateCoverageAsync(Coverage coverage)
    {
        coverage.CreatedAt = DateTime.UtcNow;
        coverage.UpdatedAt = DateTime.UtcNow;

        await _coverage.InsertOneAsync(coverage);
        _logger.LogInformation("Created coverage {CoverageId} for member {MemberId}", SanitizeForLog(coverage.Id), SanitizeForLog(coverage.MemberId));

        return coverage;
    }

    public async Task<Coverage> UpdateCoverageAsync(Coverage coverage)
    {
        coverage.UpdatedAt = DateTime.UtcNow;

        await _coverage.ReplaceOneAsync(Builders<Coverage>.Filter.Eq(x => x.Id, coverage.Id), coverage);
        _logger.LogInformation("Updated coverage {CoverageId} for member {MemberId}", SanitizeForLog(coverage.Id), SanitizeForLog(coverage.MemberId));

        return coverage;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
