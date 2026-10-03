using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using SmartAuthService.Models;

namespace SmartAuthService.Services;

/// <summary>
/// Launch contexts in MongoDB, next to the identity bindings, so a launch
/// registered through one pod is usable from any other and survives a restart.
///
/// <list type="bullet">
///   <item>Single use: consumption is one <c>findOneAndDelete</c>, so of any
///   number of concurrent uses exactly one gets the context.</item>
///   <item>Short-lived: <c>expiresAt</c> is checked on every consume, and a TTL
///   index removes expired documents.</item>
///   <item>Tenant-scoped: the consume filter includes the tenant and client the
///   launch was registered for.</item>
///   <item>The launch token is never stored, only its SHA-256, so a read of the
///   collection yields nothing that can be presented.</item>
/// </list>
/// </summary>
public sealed class MongoLaunchContextStore : ILaunchContextStore
{
    public const string CollectionName = "smart_launch_contexts";

    private readonly IMongoCollection<StoredLaunch> _launches;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly Lazy<Task> _indexes;

    public MongoLaunchContextStore(IMongoDatabase database, IConfiguration config, TimeProvider? time = null)
    {
        _launches = database.GetCollection<StoredLaunch>(CollectionName);
        _ttl = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("SmartAuth:LaunchContextTtlMinutes", 5), 1, 60));
        _time = time ?? TimeProvider.System;
        _indexes = new Lazy<Task>(() => _launches.Indexes.CreateOneAsync(new CreateIndexModel<StoredLaunch>(
            Builders<StoredLaunch>.IndexKeys.Ascending(l => l.ExpiresAt),
            new CreateIndexOptions { Name = "ttl_expires_at", ExpireAfter = TimeSpan.Zero })),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<string> RegisterAsync(
        string tenantId, string registeredBy, RegisterLaunchRequest request, CancellationToken ct = default)
    {
        await _indexes.Value;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var now = _time.GetUtcNow().UtcDateTime;

        await _launches.InsertOneAsync(new StoredLaunch
        {
            Id = Hash(token),
            TenantId = tenantId,
            ClientId = request.ClientId,
            PatientId = request.PatientId,
            EncounterId = request.EncounterId,
            RegisteredBy = registeredBy,
            CreatedAt = now,
            ExpiresAt = now.Add(_ttl),
        }, cancellationToken: ct);

        return token;
    }

    public async Task<LaunchContext?> ConsumeAsync(
        string launchToken, string tenantId, string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(launchToken)) return null;
        await _indexes.Value;

        var now = _time.GetUtcNow().UtcDateTime;
        var id = Hash(launchToken);
        var stored = await _launches.FindOneAndDeleteAsync(
            l => l.Id == id && l.TenantId == tenantId && l.ClientId == clientId && l.ExpiresAt > now,
            cancellationToken: ct);

        return stored == null ? null : new LaunchContext
        {
            LaunchToken = launchToken,
            TenantId = stored.TenantId,
            ClientId = stored.ClientId,
            PatientId = stored.PatientId,
            EncounterId = stored.EncounterId,
            RegisteredBy = stored.RegisteredBy,
            CreatedAt = new DateTimeOffset(stored.CreatedAt, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(stored.ExpiresAt, TimeSpan.Zero),
        };
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    [BsonIgnoreExtraElements]
    internal sealed class StoredLaunch
    {
        [BsonId] public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string? PatientId { get; set; }
        public string? EncounterId { get; set; }
        public string RegisteredBy { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreatedAt { get; set; }

        /// <summary>A BSON date, so the TTL index applies to it.</summary>
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime ExpiresAt { get; set; }
    }
}
