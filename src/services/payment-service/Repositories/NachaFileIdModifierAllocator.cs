using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace PaymentService.Repositories;

/// <summary>
/// NACHA file ID modifiers (file header position 34, A-Z then 0-9). Banks reject
/// as a duplicate a second file with the same immediate destination, immediate
/// origin, file creation date and modifier, so two runs whose files are created
/// on the same day must not share one. A modifier is claimed once per
/// (tenant, destination, origin, creation date) by an insert-if-absent of
/// <c>{tenant}:{destination}:{origin}:{yyMMdd}:{modifier}</c>, and the run keeps
/// the one it got (<c>PaymentRunEftFile.FileIdModifier</c>) so its file rebuilds
/// byte for byte.
/// </summary>
public interface INachaFileIdModifierAllocator
{
    /// <summary>
    /// The run's modifier for that day: the one it already holds, else the first
    /// free one. Throws when all 36 are taken.
    /// </summary>
    Task<string> AllocateAsync(string tenantId, string immediateDestination, string immediateOrigin, DateTime fileCreationDate, string runId);
}

/// <summary>One claimed modifier.</summary>
[BsonIgnoreExtraElements]
public sealed class NachaFileIdModifierClaim
{
    [BsonId][JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("runId")] public string RunId { get; set; } = string.Empty;
    [JsonPropertyName("modifier")] public string Modifier { get; set; } = string.Empty;
    [JsonPropertyName("claimedAt")] public DateTime ClaimedAt { get; set; }
}

public static class NachaFileIdModifiers
{
    /// <summary>In claim order.</summary>
    public static readonly IReadOnlyList<string> All =
        Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range('0', 10).Select(c => ((char)c).ToString()))
            .ToList();

    public static string KeyFor(string tenantId, string destination, string origin, DateTime date, string modifier)
        => $"{Uri.EscapeDataString(tenantId)}:{Uri.EscapeDataString(destination.Trim())}:{Uri.EscapeDataString(origin.Trim())}:{date:yyMMdd}:{modifier}";

    /// <summary>Tries each modifier in order; <paramref name="tryClaim"/> answers the holder's run id after an insert-if-absent.</summary>
    public static async Task<string> AllocateAsync(
        string tenantId, string destination, string origin, DateTime date, string runId,
        Func<NachaFileIdModifierClaim, Task<string>> tryClaim)
    {
        foreach (var modifier in All)
        {
            var holder = await tryClaim(new NachaFileIdModifierClaim
            {
                Id = KeyFor(tenantId, destination, origin, date, modifier),
                TenantId = tenantId,
                RunId = runId,
                Modifier = modifier,
                ClaimedAt = DateTime.UtcNow,
            });
            if (holder == runId)
                return modifier;
        }
        throw new InvalidOperationException(
            $"All 36 NACHA file ID modifiers for {date:yyyy-MM-dd} are taken; no further file can be created that day without the bank rejecting it as a duplicate.");
    }
}

public sealed class NachaFileIdModifierAllocatorMongo : INachaFileIdModifierAllocator
{
    public const string CollectionName = "NachaFileIdModifiers";

    private readonly IMongoCollection<NachaFileIdModifierClaim> _collection;

    public NachaFileIdModifierAllocatorMongo(IMongoDatabase database)
        => _collection = database.GetCollection<NachaFileIdModifierClaim>(CollectionName);

    public Task<string> AllocateAsync(string tenantId, string immediateDestination, string immediateOrigin, DateTime fileCreationDate, string runId)
        => NachaFileIdModifiers.AllocateAsync(tenantId, immediateDestination, immediateOrigin, fileCreationDate, runId, async claim =>
        {
            try
            {
                await _collection.InsertOneAsync(claim);
                return claim.RunId;
            }
            catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
            {
                var holder = await _collection.Find(Builders<NachaFileIdModifierClaim>.Filter.And(
                    Builders<NachaFileIdModifierClaim>.Filter.Eq(x => x.Id, claim.Id),
                    Builders<NachaFileIdModifierClaim>.Filter.Eq(x => x.TenantId, claim.TenantId))).FirstOrDefaultAsync();
                return holder?.RunId ?? string.Empty;
            }
        });
}

/// <summary>Cosmos: container <c>NachaFileIdModifiers</c>, partition key <c>/tenantId</c>, created if absent.</summary>
public sealed class NachaFileIdModifierAllocatorCosmos : INachaFileIdModifierAllocator
{
    public const string ContainerName = "NachaFileIdModifiers";

    private readonly Lazy<Task<Container>> _container;

    public NachaFileIdModifierAllocatorCosmos(CosmosClient client, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        _container = new Lazy<Task<Container>>(async () =>
            (await client.GetDatabase(databaseName).CreateContainerIfNotExistsAsync(new ContainerProperties(ContainerName, "/tenantId"))).Container);
    }

    public async Task<string> AllocateAsync(string tenantId, string immediateDestination, string immediateOrigin, DateTime fileCreationDate, string runId)
    {
        var container = await _container.Value;
        return await NachaFileIdModifiers.AllocateAsync(tenantId, immediateDestination, immediateOrigin, fileCreationDate, runId, async claim =>
        {
            try
            {
                await container.CreateItemAsync(claim, new PartitionKey(claim.TenantId));
                return claim.RunId;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                var holder = await container.ReadItemAsync<NachaFileIdModifierClaim>(claim.Id, new PartitionKey(claim.TenantId));
                return holder.Resource.RunId;
            }
        });
    }
}
