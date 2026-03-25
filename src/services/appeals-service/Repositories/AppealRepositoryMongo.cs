using MongoDB.Driver;
using AppealsService.Models;

namespace AppealsService.Repositories;

public class AppealRepositoryMongo : IAppealRepository
{
    private readonly IMongoCollection<Appeal> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AppealRepositoryMongo> _logger;

    public AppealRepositoryMongo(
        IMongoDatabase database,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AppealRepositoryMongo> logger)
    {
        _collection = database.GetCollection<Appeal>("appeals");
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("TenantId not found in request context");
        return tenantId;
    }

    public async Task<Appeal?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Appeal>.Filter.And(
            Builders<Appeal>.Filter.Eq(x => x.Id, id),
            Builders<Appeal>.Filter.Eq(x => x.TenantId, tenantId));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Appeal?> GetByAppealNumberAsync(string appealNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Appeal>.Filter.And(
            Builders<Appeal>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Appeal>.Filter.Eq(x => x.AppealNumber, appealNumber));
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Appeal>> GetByClaimIdAsync(string claimId)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Appeal>.Filter.And(
            Builders<Appeal>.Filter.Eq(x => x.TenantId, tenantId),
            Builders<Appeal>.Filter.Eq(x => x.ClaimId, claimId));
        return await _collection.Find(filter)
            .SortByDescending(x => x.SubmittedDate)
            .ToListAsync();
    }

    public async Task<IEnumerable<Appeal>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? submittedFrom,
        DateTime? submittedTo,
        AppealStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page = 1,
        int pageSize = 50)
    {
        var tenantId = GetTenantId();
        var filters = new List<FilterDefinition<Appeal>>
        {
            Builders<Appeal>.Filter.Eq(x => x.TenantId, tenantId)
        };

        if (!string.IsNullOrEmpty(memberId))
            filters.Add(Builders<Appeal>.Filter.Eq(x => x.MemberId, memberId));
        if (!string.IsNullOrEmpty(providerNPI))
            filters.Add(Builders<Appeal>.Filter.Eq(x => x.ProviderNPI, providerNPI));
        if (submittedFrom.HasValue)
            filters.Add(Builders<Appeal>.Filter.Gte(x => x.SubmittedDate, submittedFrom.Value));
        if (submittedTo.HasValue)
            filters.Add(Builders<Appeal>.Filter.Lte(x => x.SubmittedDate, submittedTo.Value));
        if (status.HasValue)
            filters.Add(Builders<Appeal>.Filter.Eq(x => x.Status, status.Value));
        if (lineOfBusiness.HasValue)
            filters.Add(Builders<Appeal>.Filter.Eq(x => x.LineOfBusiness, lineOfBusiness.Value));

        return await _collection
            .Find(Builders<Appeal>.Filter.And(filters))
            .SortByDescending(x => x.SubmittedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<AppealsSummary> GetAppealsSummaryAsync(DateTime from, DateTime to)
    {
        var appeals = (await SearchAsync(null, null, from, to, null, null, 1, 10000)).ToList();

        var summary = new AppealsSummary
        {
            TotalAppeals = appeals.Count,
            InReview = appeals.Count(a => a.Status == AppealStatus.InReview),
            Approved = appeals.Count(a => a.Status == AppealStatus.Approved),
            Denied = appeals.Count(a => a.Status == AppealStatus.Denied),
            PartialApprovals = appeals.Count(a => a.Status == AppealStatus.PartialApproval),
            TotalAppealedAmount = appeals.Sum(a => a.AppealedAmount),
            TotalApprovedAmount = appeals
                .Where(a => a.Decision != null && a.Decision.ApprovedAmount.HasValue)
                .Sum(a => a.Decision!.ApprovedAmount!.Value)
        };

        var decidedAppeals = appeals.Where(a => a.DecisionDate.HasValue).ToList();
        if (decidedAppeals.Any())
        {
            summary.AverageDecisionTimeDays = decidedAppeals
                .Average(a => (a.DecisionDate!.Value - a.SubmittedDate).TotalDays);
        }

        var totalDecided = appeals.Count(a => a.Status == AppealStatus.Approved ||
                                              a.Status == AppealStatus.Denied ||
                                              a.Status == AppealStatus.PartialApproval);
        if (totalDecided > 0)
        {
            summary.ApprovalRate = ((double)(summary.Approved + summary.PartialApprovals) / totalDecided) * 100;
        }

        foreach (var appeal in appeals)
        {
            if (!summary.AppealsByStatus.ContainsKey(appeal.Status))
                summary.AppealsByStatus[appeal.Status] = 0;
            summary.AppealsByStatus[appeal.Status]++;

            if (!summary.AppealsByLevel.ContainsKey(appeal.AppealLevel))
                summary.AppealsByLevel[appeal.AppealLevel] = 0;
            summary.AppealsByLevel[appeal.AppealLevel]++;
        }

        return summary;
    }

    public async Task<Appeal> CreateAsync(Appeal appeal)
    {
        appeal.TenantId = GetTenantId();
        appeal.SubmittedDate = DateTime.UtcNow;
        appeal.TargetResponseDate = appeal.SubmittedDate.AddDays(appeal.IsUrgent ? 30 : 60);

        await _collection.InsertOneAsync(appeal);
        _logger.LogInformation("Created appeal {AppealId} for claim {ClaimId}", appeal.Id, appeal.ClaimId);
        return appeal;
    }

    public async Task<Appeal> UpdateAsync(Appeal appeal)
    {
        var filter = Builders<Appeal>.Filter.And(
            Builders<Appeal>.Filter.Eq(x => x.Id, appeal.Id),
            Builders<Appeal>.Filter.Eq(x => x.TenantId, appeal.TenantId));
        await _collection.ReplaceOneAsync(filter, appeal);
        _logger.LogInformation("Updated appeal {AppealId}", appeal.Id);
        return appeal;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Appeal>.Filter.And(
            Builders<Appeal>.Filter.Eq(x => x.Id, id),
            Builders<Appeal>.Filter.Eq(x => x.TenantId, tenantId));
        await _collection.DeleteOneAsync(filter);
        _logger.LogInformation("Deleted appeal {AppealId}", id);
    }
}
