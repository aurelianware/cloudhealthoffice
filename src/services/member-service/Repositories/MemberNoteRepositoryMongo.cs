using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MemberService.Models;
using MongoDB.Driver;

namespace MemberService.Repositories;

/// <summary>
/// MongoDB implementation of <see cref="IMemberNoteRepository"/>.
/// </summary>
public class MemberNoteRepositoryMongo : IMemberNoteRepository
{
    private readonly IMongoCollection<MemberNote> _collection;

    public MemberNoteRepositoryMongo(IMongoDatabase database)
    {
        _collection = database.GetCollection<MemberNote>("MemberNotes");
    }

    public async Task<MemberNote> CreateAsync(MemberNote note)
    {
        if (string.IsNullOrEmpty(note.Id)) note.Id = Guid.NewGuid().ToString();
        if (note.CreatedDate == default) note.CreatedDate = DateTime.UtcNow;
        await _collection.InsertOneAsync(note);
        return note;
    }

    public async Task<MemberNote?> GetByIdAsync(string tenantId, string memberId, string noteId)
    {
        var filter = Builders<MemberNote>.Filter.Eq(n => n.TenantId, tenantId)
                   & Builders<MemberNote>.Filter.Eq(n => n.MemberId, memberId)
                   & Builders<MemberNote>.Filter.Eq(n => n.Id, noteId);
        return await _collection.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<(IReadOnlyList<MemberNote> Items, string? ContinuationToken)> ListByMemberAsync(
        string tenantId,
        string memberId,
        MemberNoteCategory? category,
        int pageSize,
        string? continuationToken)
    {
        var fb = Builders<MemberNote>.Filter;
        var filter = fb.Eq(n => n.TenantId, tenantId) & fb.Eq(n => n.MemberId, memberId);
        if (category.HasValue) filter &= fb.Eq(n => n.Category, category.Value);

        int skip = 0;
        if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var parsed))
        {
            skip = parsed;
        }

        var results = await _collection.Find(filter)
            .SortByDescending(n => n.CreatedDate)
            .Skip(skip)
            .Limit(pageSize)
            .ToListAsync();

        string? nextToken = results.Count == pageSize ? (skip + pageSize).ToString() : null;
        return (results, nextToken);
    }
}
