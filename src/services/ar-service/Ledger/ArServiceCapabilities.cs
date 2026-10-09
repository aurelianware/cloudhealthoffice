using System.Reflection;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace ArService.Ledger;

/// <summary>
/// The marker document this build of ar-service writes at startup, so that the legacy
/// reconciliation tool (tools/ArLegacyPostingReconciliation) can tell that the environment
/// it is about to change runs a build that credits balances on apply (PR #1271) <em>and</em>
/// refuses legacy postings (409). Before that build, writing posted ids or balance versions
/// would break the running service: its models did not ignore unknown elements, and its
/// balance saves were unconditional.
/// <para>
/// Collection <see cref="Collection"/> in the base database (<c>MongoDb:DatabaseName</c>),
/// document <see cref="DocumentId"/>. An older build never writes it; it cannot remove it
/// either, so after a rollback the marker stays — the runbook covers that.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ArServiceCapabilities
{
    public const string Collection = "ar_service_capabilities";
    public const string DocumentId = "cash-posting-ledger";

    /// <summary>Apply credits balances with fixed entry ids and versioned saves (PR #1271).</summary>
    public const string LedgerCredit = "ledger-credit-v1";

    /// <summary>Apply and void refuse an unreconciled legacy posting with 409.</summary>
    public const string LegacyReconciliationGuard = "legacy-reconciliation-guard-v1";

    /// <summary>Models ignore elements they do not know, so a later rollback still reads documents.</summary>
    public const string IgnoresExtraElements = "ignore-extra-elements-v1";

    public static readonly IReadOnlyList<string> Current = [LedgerCredit, LegacyReconciliationGuard, IgnoresExtraElements];

    [BsonId] public string Id { get; set; } = DocumentId;
    public List<string> Capabilities { get; set; } = new();
    public string? Build { get; set; }
    public string? Host { get; set; }
    public DateTime FirstStartedAt { get; set; }
    public DateTime LastStartedAt { get; set; }

    public static string BuildVersion =>
        typeof(ArServiceCapabilities).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ArServiceCapabilities).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Upserts the marker: the capabilities of this build, and when it last started.</summary>
    public static Task WriteAsync(IMongoDatabase baseDatabase, DateTime now, CancellationToken cancellationToken = default)
    {
        var u = Builders<ArServiceCapabilities>.Update;
        return baseDatabase.GetCollection<ArServiceCapabilities>(Collection).UpdateOneAsync(
            c => c.Id == DocumentId,
            u.Set(c => c.Capabilities, Current.ToList())
             .Set(c => c.Build, BuildVersion)
             .Set(c => c.Host, Environment.MachineName)
             .Set(c => c.LastStartedAt, now)
             .SetOnInsert(c => c.FirstStartedAt, now),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    public static Task<ArServiceCapabilities?> ReadAsync(IMongoDatabase baseDatabase, CancellationToken cancellationToken = default) =>
        baseDatabase.GetCollection<ArServiceCapabilities>(Collection)
            .Find(c => c.Id == DocumentId).FirstOrDefaultAsync(cancellationToken)!;
}

/// <summary>
/// Writes <see cref="ArServiceCapabilities"/> once at startup, in the background: the service
/// starts and serves whether or not the database is reachable yet, and the write is retried
/// until it succeeds or the service stops.
/// </summary>
public sealed class ArServiceCapabilitiesWriter : BackgroundService
{
    private readonly IMongoClient _client;
    private readonly string _baseDatabaseName;
    private readonly ILogger<ArServiceCapabilitiesWriter> _logger;

    public ArServiceCapabilitiesWriter(IMongoClient client, IConfiguration configuration, ILogger<ArServiceCapabilitiesWriter> logger)
    {
        _client = client;
        _baseDatabaseName = configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice";
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ArServiceCapabilities.WriteAsync(_client.GetDatabase(_baseDatabaseName), DateTime.UtcNow, stoppingToken);
                _logger.LogInformation("ar-service capability marker written ({Capabilities})", string.Join(", ", ArServiceCapabilities.Current));
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ar-service capability marker not written yet; retrying in {Delay}", delay);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }
}
