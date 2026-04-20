using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MemberService.Models;
using Microsoft.Azure.Cosmos;

namespace MemberService.Repositories;

/// <summary>
/// Cosmos DB repository for <see cref="MemberNote"/>. Notes are immutable —
/// only Create + Read are exposed. Corrections are new notes that link back
/// to the prior note via <see cref="MemberNote.LinkedResourceId"/>.
/// </summary>
public class MemberNoteRepository : IMemberNoteRepository
{
    private readonly Container _container;
    private const string ContainerName = "MemberNotes";

    public MemberNoteRepository(CosmosClient cosmosClient, string databaseName)
    {
        _container = cosmosClient.GetDatabase(databaseName).GetContainer(ContainerName);
    }

    public async Task<MemberNote> CreateAsync(MemberNote note)
    {
        if (string.IsNullOrEmpty(note.Id)) note.Id = Guid.NewGuid().ToString();
        if (note.CreatedDate == default) note.CreatedDate = DateTime.UtcNow;

        var response = await _container.CreateItemAsync(note, new PartitionKey(note.TenantId));
        return response.Resource;
    }

    public async Task<MemberNote?> GetByIdAsync(string tenantId, string memberId, string noteId)
    {
        try
        {
            var response = await _container.ReadItemAsync<MemberNote>(noteId, new PartitionKey(tenantId));
            var note = response.Resource;
            return note.MemberId == memberId ? note : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<(IReadOnlyList<MemberNote> Items, string? ContinuationToken)> ListByMemberAsync(
        string tenantId,
        string memberId,
        MemberNoteCategory? category,
        int pageSize,
        string? continuationToken)
    {
        var queryText = "SELECT * FROM c WHERE c.tenantId = @tenantId AND c.memberId = @memberId";
        if (category.HasValue) queryText += " AND c.category = @category";
        queryText += " ORDER BY c.createdDate DESC";

        var query = new QueryDefinition(queryText)
            .WithParameter("@tenantId", tenantId)
            .WithParameter("@memberId", memberId);
        if (category.HasValue) query = query.WithParameter("@category", (int)category.Value);

        var iterator = _container.GetItemQueryIterator<MemberNote>(
            query,
            continuationToken,
            new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(tenantId),
                MaxItemCount = pageSize
            });

        var results = new List<MemberNote>();
        string? nextToken = null;
        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
            nextToken = response.ContinuationToken;
        }
        return (results, nextToken);
    }
}

public interface IMemberNoteRepository
{
    Task<MemberNote> CreateAsync(MemberNote note);
    Task<MemberNote?> GetByIdAsync(string tenantId, string memberId, string noteId);

    /// <summary>
    /// Page notes for a member, newest first. <paramref name="category"/> optional;
    /// when null, returns all categories.
    /// </summary>
    Task<(IReadOnlyList<MemberNote> Items, string? ContinuationToken)> ListByMemberAsync(
        string tenantId,
        string memberId,
        MemberNoteCategory? category,
        int pageSize,
        string? continuationToken);
}
