using EnrollmentImportService.Models;
using MongoDB.Driver;

namespace EnrollmentImportService.Services;

public interface IEnrollmentRepository
{
    Task<Member?> GetMemberByIdAsync(string memberId, string tenantId);
    Task<Member?> GetMemberBySubscriberIdAsync(string subscriberId, string tenantId);
    Task<Member> CreateMemberAsync(Member member);
    Task<Member> UpdateMemberAsync(Member member);
    Task<Coverage> CreateCoverageAsync(Coverage coverage);
    Task<Coverage> UpdateCoverageAsync(Coverage coverage);
    Task<SponsorEntity?> GetSponsorByIdAsync(string sponsorId, string tenantId);
    Task<SponsorEntity> CreateSponsorAsync(SponsorEntity sponsor);
    Task<SponsorEntity> UpdateSponsorAsync(SponsorEntity sponsor);
}

/// <summary>
/// MongoDB repository for Members/Coverage/Sponsors. Index creation is
/// handled at startup by <c>EnrollmentIndexInitializer</c> so the
/// repository can be registered as a singleton and constructed without
/// I/O side effects (same pattern as member-service's MemberRepositoryMongo).
/// </summary>
public class EnrollmentRepository : IEnrollmentRepository
{
    private readonly IMongoCollection<Member> _members;
    private readonly IMongoCollection<Coverage> _coverage;
    private readonly IMongoCollection<SponsorEntity> _sponsors;
    private readonly ILogger<EnrollmentRepository> _logger;

    public EnrollmentRepository(IMongoDatabase database, ILogger<EnrollmentRepository> logger)
    {
        _members = database.GetCollection<Member>("Members");
        _coverage = database.GetCollection<Coverage>("Coverage");
        _sponsors = database.GetCollection<SponsorEntity>("Sponsors");
        _logger = logger;
    }

    public async Task<Member?> GetMemberByIdAsync(string memberId, string tenantId)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.MemberId, memberId) &
                     Builders<Member>.Filter.Eq(x => x.TenantId, tenantId);
        return await _members.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Member?> GetMemberBySubscriberIdAsync(string subscriberId, string tenantId)
    {
        var filter = Builders<Member>.Filter.Eq(x => x.SubscriberId, subscriberId) &
                     Builders<Member>.Filter.Eq(x => x.TenantId, tenantId);
        return await _members.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Member> CreateMemberAsync(Member member)
    {
        member.CreatedAt = DateTime.UtcNow;
        member.UpdatedAt = DateTime.UtcNow;

        await _members.InsertOneAsync(member);
        _logger.LogInformation("Created member {MemberId} for tenant {TenantId}", SanitizeForLog(member.MemberId), SanitizeForLog(member.TenantId));

        return member;
    }

    public async Task<Member> UpdateMemberAsync(Member member)
    {
        member.UpdatedAt = DateTime.UtcNow;

        await _members.ReplaceOneAsync(Builders<Member>.Filter.Eq(x => x.Id, member.Id), member);
        _logger.LogInformation("Updated member {MemberId} for tenant {TenantId}", SanitizeForLog(member.MemberId), SanitizeForLog(member.TenantId));

        return member;
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

    public async Task<SponsorEntity?> GetSponsorByIdAsync(string sponsorId, string tenantId)
    {
        var filter = Builders<SponsorEntity>.Filter.Eq(x => x.SponsorId, sponsorId) &
                     Builders<SponsorEntity>.Filter.Eq(x => x.TenantId, tenantId);
        return await _sponsors.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<SponsorEntity> CreateSponsorAsync(SponsorEntity sponsor)
    {
        sponsor.CreatedAt = DateTime.UtcNow;
        sponsor.UpdatedAt = DateTime.UtcNow;

        await _sponsors.InsertOneAsync(sponsor);
        _logger.LogInformation("Created sponsor {SponsorId} for tenant {TenantId}", SanitizeForLog(sponsor.SponsorId), SanitizeForLog(sponsor.TenantId));

        return sponsor;
    }

    public async Task<SponsorEntity> UpdateSponsorAsync(SponsorEntity sponsor)
    {
        sponsor.UpdatedAt = DateTime.UtcNow;

        await _sponsors.ReplaceOneAsync(Builders<SponsorEntity>.Filter.Eq(x => x.Id, sponsor.Id), sponsor);
        _logger.LogInformation("Updated sponsor {SponsorId} for tenant {TenantId}", SanitizeForLog(sponsor.SponsorId), SanitizeForLog(sponsor.TenantId));

        return sponsor;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
