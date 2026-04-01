using AuthorizationService.Models;
using MongoDB.Driver;
using MongoDB.Bson;

namespace AuthorizationService.Repositories;

public class AuthorizationRepositoryMongo : IAuthorizationRepository
{
    private readonly IMongoCollection<Authorization> _collection;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuthorizationRepositoryMongo> _logger;

    public AuthorizationRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuthorizationRepositoryMongo> logger)
    {
        var collectionName = configuration["CosmosDb:ContainerName"] ?? "Authorizations";
        _collection = database.GetCollection<Authorization>(collectionName);
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    private string GetTenantId()
    {
        var tenantId = _httpContextAccessor.HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException("TenantId not found in request context");
        }
        return tenantId;
    }

    public async Task<Authorization?> GetByIdAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.Id, id),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.AuthorizationNumber, authorizationNumber),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<Authorization>> SearchAsync(
        string? memberId,
        string? providerNPI,
        DateTime? serviceDateFrom,
        DateTime? serviceDateTo,
        AuthorizationStatus? status,
        LineOfBusiness? lineOfBusiness,
        int page,
        int pageSize)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.Eq(x => x.TenantId, tenantId);

        if (!string.IsNullOrEmpty(memberId))
        {
            filter &= builder.Eq(x => x.MemberId, memberId);
        }

        if (!string.IsNullOrEmpty(providerNPI))
        {
            filter &= builder.Or(
                builder.Eq(x => x.RequestingProviderNPI, providerNPI),
                builder.Eq(x => x.ServicingProviderNPI, providerNPI)
            );
        }

        if (serviceDateFrom.HasValue)
        {
            filter &= builder.Gte(x => x.RequestedServiceDateFrom, serviceDateFrom.Value);
        }

        if (serviceDateTo.HasValue)
        {
            filter &= builder.Or(
                builder.Lte(x => x.RequestedServiceDateTo, serviceDateTo.Value),
                builder.Eq(x => x.RequestedServiceDateTo, null)
            );
        }

        if (status.HasValue)
        {
            filter &= builder.Eq(x => x.Status, status.Value);
        }

        if (lineOfBusiness.HasValue)
        {
            filter &= builder.Eq(x => x.LineOfBusiness, lineOfBusiness.Value);
        }

        return await _collection.Find(filter)
            .SortByDescending(x => x.SubmittedDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync();
    }

    public async Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness)
    {
        var tenantId = GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.And(
            builder.Eq(x => x.TenantId, tenantId),
            builder.Gte(x => x.SubmittedDate, from),
            builder.Lte(x => x.SubmittedDate, to)
        );

        if (lineOfBusiness.HasValue)
        {
            filter &= builder.Eq(x => x.LineOfBusiness, lineOfBusiness.Value);
        }

        var results = await _collection.Find(filter).ToListAsync();

        var summary = new AuthorizationsSummary
        {
            TotalAuthorizations = results.Count,
            ApprovedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Approved),
            DeniedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Denied),
            PendedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Pended),
            ModifiedAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Modified),
            ExpiredAuthorizations = results.Count(x => x.Status == AuthorizationStatus.Expired)
        };

        if (summary.TotalAuthorizations > 0)
        {
            summary.ApprovalRate = (decimal)(summary.ApprovedAuthorizations + summary.ModifiedAuthorizations) / summary.TotalAuthorizations * 100;
        }

        var reviewedAuths = results.Where(x => x.ReviewedDate.HasValue).ToList();
        if (reviewedAuths.Any())
        {
            summary.AverageReviewDays = (decimal)reviewedAuths.Average(x => (x.ReviewedDate!.Value - x.SubmittedDate).TotalDays);
        }

        return summary;
    }

    public async Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null)
    {
        var effectiveTenantId = tenantId ?? GetTenantId();
        var builder = Builders<Authorization>.Filter;
        var filter = builder.And(
            builder.Eq(x => x.TenantId, effectiveTenantId),
            builder.In(x => x.Status, new[]
            {
                AuthorizationStatus.Submitted,
                AuthorizationStatus.InReview,
                AuthorizationStatus.Pended
            })
        );

        return await _collection.Find(filter).ToListAsync();
    }

    public async Task<Authorization> CreateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;
        authorization.Id ??= Guid.NewGuid().ToString();

        await _collection.InsertOneAsync(authorization);
        return authorization;
    }

    public async Task<Authorization> UpdateAsync(Authorization authorization)
    {
        var tenantId = GetTenantId();
        authorization.TenantId = tenantId;

        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.Id, authorization.Id),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );

        await _collection.ReplaceOneAsync(filter, authorization);
        return authorization;
    }

    public async Task DeleteAsync(string id)
    {
        var tenantId = GetTenantId();
        var filter = Builders<Authorization>.Filter.And(
            Builders<Authorization>.Filter.Eq(x => x.Id, id),
            Builders<Authorization>.Filter.Eq(x => x.TenantId, tenantId)
        );
        await _collection.DeleteOneAsync(filter);
    }
}
