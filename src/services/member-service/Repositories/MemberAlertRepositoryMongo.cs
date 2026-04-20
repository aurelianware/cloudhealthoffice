using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MemberService.Models;
using MongoDB.Driver;

namespace MemberService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IMemberAlertRepository"/>.
/// </summary>
public class MemberAlertRepositoryMongo : IMemberAlertRepository
{
    private readonly IMongoCollection<MemberAlert> _collection;

    public MemberAlertRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<MemberAlert>("MemberAlerts");
    }

    public async Task<MemberAlert> CreateAsync(MemberAlert alert)
    {
        if (string.IsNullOrEmpty(alert.Id)) alert.Id = Guid.NewGuid().ToString();
        if (alert.CreatedDate == default) alert.CreatedDate = DateTime.UtcNow;
        await _collection.InsertOneAsync(alert);
        return alert;
    }

    public async Task<MemberAlert?> GetByIdAsync(string tenantId, string memberId, string alertId)
    {
        var filter = Builders<MemberAlert>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<MemberAlert>.Filter.Eq(a => a.MemberId, memberId)
                   & Builders<MemberAlert>.Filter.Eq(a => a.Id, alertId);
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<IReadOnlyList<MemberAlert>> ListByMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly,
        DateTime? asOf = null)
    {
        var filter = Builders<MemberAlert>.Filter.Eq(a => a.TenantId, tenantId)
                   & Builders<MemberAlert>.Filter.Eq(a => a.MemberId, memberId);
        var results = await _collection.Find(filter).ToListAsync();

        var t = asOf ?? DateTime.UtcNow;
        if (activeOnly) results = results.Where(a => a.IsActive(t)).ToList();

        return results
            .OrderByDescending(a => a.StartDate)
            .ThenBy(a => a.AlertType)
            .ToList();
    }

    public async Task<MemberAlert> EndAsync(MemberAlert alert)
    {
        var filter = Builders<MemberAlert>.Filter.Eq(a => a.TenantId, alert.TenantId)
                   & Builders<MemberAlert>.Filter.Eq(a => a.Id, alert.Id);
        await _collection.ReplaceOneAsync(filter, alert);
        return alert;
    }
}
