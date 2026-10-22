using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Finance.Contracts;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using PaymentService.Models;
using CloudHealthOffice.Infrastructure.Logging;

namespace PaymentService.Services;

/// <summary>Configuration section <c>GlEvents</c>. Dispatch is off by default.</summary>
public sealed class GlEventOptions
{
    public const string SectionName = "GlEvents";

    /// <summary>Deliver outbox events to ar-service's GL posting. Default false: events accumulate, nothing is sent.</summary>
    public bool DispatchEnabled { get; set; }

    /// <summary>ar-service base URL (must be a configured CHO host).</summary>
    public string ArServiceBaseUrl { get; set; } = "http://ar-service";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
    public int BatchSize { get; set; } = 100;

    /// <summary>Longest wait between attempts of one failing event (backs off exponentially up to this).</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Where an outbox event lives.</summary>
public enum GlOutboxSource { PaymentRun, ReversalRun, PaymentFileTransmission, NachaSettlement }

/// <summary>An undelivered outbox event and the document holding it.</summary>
public sealed record PendingGlEvent(GlOutboxSource Source, string DocumentId, string TenantId, PaymentFileOutboxMessage Message);

/// <summary>
/// The outbox events of payment runs, reversal runs and transmission records, across
/// tenants (the dispatcher is a system job). Marking is a targeted update of one array
/// element (by event id), never a document rewrite. A later whole-document save holding an
/// older copy can clear a <c>PublishedAt</c>; the event is then delivered again, which the
/// consumer de-duplicates (at least once, never lost).
/// </summary>
public interface IGlOutboxStore
{
    /// <summary>Undelivered events due now (oldest first), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<PendingGlEvent>> ListDueAsync(DateTime now, int limit, CancellationToken cancellationToken = default);

    /// <summary>A tenant's undelivered events (for operators), whether due or backing off, oldest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<PendingGlEvent>> ListUnpublishedAsync(string tenantId, int limit = 500, CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(PendingGlEvent pending, DateTime at, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(PendingGlEvent pending, string error, DateTime nextAttemptAt, CancellationToken cancellationToken = default);
}

public sealed class MongoGlOutboxStore : IGlOutboxStore
{
    private readonly IMongoCollection<PaymentRun> _runs;
    private readonly IMongoCollection<ReversalRun> _reversals;
    private readonly IMongoCollection<PaymentFileTransmission> _transmissions;
    private readonly IMongoCollection<PaymentService.BankReconciliation.NachaFileSettlement> _settlements;

    public MongoGlOutboxStore(IMongoDatabase database)
    {
        _settlements = database.GetCollection<PaymentService.BankReconciliation.NachaFileSettlement>(PaymentService.BankReconciliation.NachaSettlementStoreMongo.CollectionName);
        _runs = database.GetCollection<PaymentRun>("PaymentRuns");
        _reversals = database.GetCollection<ReversalRun>("ReversalRuns");
        _transmissions = database.GetCollection<PaymentFileTransmission>(Repositories.PaymentFileTransmissionRepositoryMongo.CollectionName);
    }

    public Task<IReadOnlyList<PendingGlEvent>> ListDueAsync(DateTime now, int limit, CancellationToken cancellationToken = default)
        => ListAsync(null, now, limit, cancellationToken);

    public Task<IReadOnlyList<PendingGlEvent>> ListUnpublishedAsync(string tenantId, int limit = 500, CancellationToken cancellationToken = default)
        => ListAsync(tenantId, null, limit, cancellationToken);

    private async Task<IReadOnlyList<PendingGlEvent>> ListAsync(string? tenantId, DateTime? dueBy, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 5000);
        var result = new List<PendingGlEvent>();
        result.AddRange(await FindAsync(_runs, GlOutboxSource.PaymentRun, r => r.GlOutbox, nameof(PaymentRun.GlOutbox), r => r.Id, r => r.TenantId, tenantId, dueBy, limit, cancellationToken));
        result.AddRange(await FindAsync(_reversals, GlOutboxSource.ReversalRun, r => r.GlOutbox, nameof(ReversalRun.GlOutbox), r => r.Id, r => r.TenantId, tenantId, dueBy, limit, cancellationToken));
        result.AddRange(await FindAsync(_transmissions, GlOutboxSource.PaymentFileTransmission, t => t.Outbox, nameof(PaymentFileTransmission.Outbox), t => t.Id, t => t.TenantId, tenantId, dueBy, limit, cancellationToken));
        result.AddRange(await FindAsync(_settlements, GlOutboxSource.NachaSettlement, t => t.GlOutbox,
            nameof(PaymentService.BankReconciliation.NachaFileSettlement.GlOutbox), t => t.Id, t => t.TenantId,
            tenantId, dueBy, limit, cancellationToken));
        return result.OrderBy(e => e.Message.CreatedAt).ThenBy(e => e.Message.EventId, StringComparer.Ordinal).Take(limit).ToList();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Indexed = new(StringComparer.Ordinal);

    /// <summary>
    /// Only documents holding an undelivered (and, for the dispatcher, due) event, through an
    /// index on the outbox's PublishedAt, at most <paramref name="limit"/> documents per
    /// collection: a cycle never reads the whole history of runs.
    /// </summary>
    private static async Task<List<PendingGlEvent>> FindAsync<T>(
        IMongoCollection<T> collection, GlOutboxSource source,
        Expression<Func<T, IEnumerable<PaymentFileOutboxMessage>>> outbox, string field,
        Func<T, string> id, Func<T, string> tenant, string? tenantId, DateTime? dueBy, int limit, CancellationToken cancellationToken)
    {
        var key = $"{collection.Database.DatabaseNamespace.DatabaseName}.{collection.CollectionNamespace.CollectionName}";
        if (!Indexed.ContainsKey(key))
        {
            // Created on first use (the dispatcher only runs when enabled): undelivered elements
            // have PublishedAt null, which the multikey index serves.
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<T>(
                Builders<T>.IndexKeys.Ascending($"{field}.{nameof(PaymentFileOutboxMessage.PublishedAt)}").Ascending("TenantId"),
                new CreateIndexOptions { Name = $"gl_outbox_unpublished_{field}" }), cancellationToken: cancellationToken);
            Indexed[key] = true;
        }

        var f = Builders<T>.Filter;
        var filter = dueBy is { } now
            ? f.ElemMatch(outbox, m => m.PublishedAt == null && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            : f.ElemMatch(outbox, m => m.PublishedAt == null);
        if (tenantId != null)
            filter &= f.Eq("TenantId", tenantId);
        var compiled = outbox.Compile();
        var docs = await collection.Find(filter).Sort(Builders<T>.Sort.Ascending("_id")).Limit(limit).ToListAsync(cancellationToken);
        // One delivery per event id per document, even if an older save left a duplicate entry.
        return docs.SelectMany(d => compiled(d)
                .Where(m => m.PublishedAt == null && (dueBy == null || m.NextAttemptAt == null || m.NextAttemptAt <= dueBy))
                .GroupBy(m => m.EventId, StringComparer.Ordinal)
                .Select(g => new PendingGlEvent(source, id(d), tenant(d), g.First())))
            .ToList();
    }

    public Task MarkPublishedAsync(PendingGlEvent pending, DateTime at, CancellationToken cancellationToken = default)
        => UpdateElementAsync(pending, field => new BsonDocument("$set", new BsonDocument
        {
            { $"{field}.$[m].{nameof(PaymentFileOutboxMessage.PublishedAt)}", at },
            { $"{field}.$[m].{nameof(PaymentFileOutboxMessage.LastError)}", BsonNull.Value },
        }), cancellationToken);

    public Task MarkFailedAsync(PendingGlEvent pending, string error, DateTime nextAttemptAt, CancellationToken cancellationToken = default)
        => UpdateElementAsync(pending, field => new BsonDocument
        {
            { "$inc", new BsonDocument($"{field}.$[m].{nameof(PaymentFileOutboxMessage.PublishAttempts)}", 1) },
            { "$set", new BsonDocument
                {
                    { $"{field}.$[m].{nameof(PaymentFileOutboxMessage.LastError)}", error },
                    { $"{field}.$[m].{nameof(PaymentFileOutboxMessage.NextAttemptAt)}", nextAttemptAt },
                } },
        }, cancellationToken);

    /// <summary>
    /// An update of every outbox element with the event's id (array filter; field names as the
    /// driver maps them): a duplicate entry is marked with the first, never re-sent for ever.
    /// </summary>
    private Task UpdateElementAsync(PendingGlEvent pending, Func<string, BsonDocument> update, CancellationToken cancellationToken)
    {
        var (collection, field) = pending.Source switch
        {
            GlOutboxSource.PaymentRun => (_runs.Database.GetCollection<BsonDocument>(_runs.CollectionNamespace.CollectionName), nameof(PaymentRun.GlOutbox)),
            GlOutboxSource.ReversalRun => (_reversals.Database.GetCollection<BsonDocument>(_reversals.CollectionNamespace.CollectionName), nameof(ReversalRun.GlOutbox)),
            GlOutboxSource.PaymentFileTransmission => (_transmissions.Database.GetCollection<BsonDocument>(_transmissions.CollectionNamespace.CollectionName), nameof(PaymentFileTransmission.Outbox)),
            GlOutboxSource.NachaSettlement => (_settlements.Database.GetCollection<BsonDocument>(_settlements.CollectionNamespace.CollectionName), nameof(PaymentService.BankReconciliation.NachaFileSettlement.GlOutbox)),
            _ => throw new InvalidOperationException("Unknown GL outbox source."),
        };
        var filter = new BsonDocument
        {
            { "_id", pending.DocumentId },
            { "TenantId", pending.TenantId },
            { $"{field}.{nameof(PaymentFileOutboxMessage.EventId)}", pending.Message.EventId },
        };
        var options = new UpdateOptions
        {
            ArrayFilters = new[]
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument($"m.{nameof(PaymentFileOutboxMessage.EventId)}", pending.Message.EventId)),
            },
        };
        return collection.UpdateOneAsync(filter, update(field), options, cancellationToken);
    }
}

/// <summary>Where events are delivered (ar-service's GL posting).</summary>
public interface IGlEventSink
{
    /// <summary>Delivered (the consumer stored it, posted or parked, or already had it); otherwise throws <see cref="GlEventDeliveryException"/>.</summary>
    Task DeliverAsync(GlEventEnvelope envelope, CancellationToken cancellationToken = default);
}

public sealed class GlEventDeliveryException : Exception
{
    public GlEventDeliveryException(string message) : base(message) { }
}

/// <summary>
/// <c>POST {ArServiceBaseUrl}/api/gl/events</c> with payment-service's own service token
/// for the event's tenant, only to a configured CHO host.
/// </summary>
public sealed class HttpGlEventSink : IGlEventSink
{
    public const string HttpClientName = "GlEventsArService";

    private readonly IHttpClientFactory _http;
    private readonly IChoServiceTokenSource? _tokens;
    private readonly ChoOutboundHosts? _hosts;

    public HttpGlEventSink(IHttpClientFactory http, IServiceProvider services)
    {
        _http = http;
        _tokens = ChoServiceTokens.Resolve(services);
        _hosts = services.GetService<ChoOutboundHosts>();
    }

    public async Task DeliverAsync(GlEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (_tokens == null)
            throw new GlEventDeliveryException("No service token is configured (ChoAuth:ServiceToken); GL events cannot be delivered.");
        var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/gl/events") { Content = JsonContent.Create(envelope, options: GlEventTypes.Json) };
        if (_hosts?.IsChoService(new Uri(client.BaseAddress ?? new Uri("http://invalid"), request.RequestUri!)) != true)
            throw new GlEventDeliveryException("GlEvents:ArServiceBaseUrl is not a configured CHO host (ChoAuth:Outbound:Hosts).");
        request.Headers.Add(TenantMiddleware.TenantHeaderName, envelope.TenantId);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(envelope.TenantId, cancellationToken));
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return;
            throw new GlEventDeliveryException($"ar-service answered {(int)response.StatusCode} {response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ChoServiceTokenUnavailableException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new GlEventDeliveryException($"ar-service could not be reached ({ex.GetType().Name}).");
        }
    }
}

/// <summary>
/// Delivers outbox events to the GL, at least once, in creation order per cycle. A failing
/// event backs off (1 min, doubling, up to <see cref="GlEventOptions.MaxBackoff"/>) and is
/// never dropped. Off unless <c>GlEvents:DispatchEnabled</c>.
/// </summary>
public sealed class GlEventDispatcher : BackgroundService
{
    public static readonly EventId DeliveredEvent = new(4951, "GlEventDelivered");
    public static readonly EventId DeliveryFailedEvent = new(4952, "GlEventDeliveryFailed");

    private readonly IGlOutboxStore _store;
    private readonly IGlEventSink _sink;
    private readonly GlEventOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<GlEventDispatcher> _logger;

    public GlEventDispatcher(IGlOutboxStore store, IGlEventSink sink, IOptions<GlEventOptions> options, TimeProvider clock, ILogger<GlEventDispatcher> logger)
    {
        _store = store;
        _sink = sink;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>One cycle: the number of events delivered.</summary>
    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var due = await _store.ListDueAsync(now, Math.Max(1, _options.BatchSize), cancellationToken);
        var delivered = 0;
        foreach (var pending in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var envelope = new GlEventEnvelope
            {
                EventId = pending.Message.EventId,
                Type = pending.Message.Type,
                TenantId = pending.TenantId,
                Source = "payment-service",
                PayloadJson = pending.Message.PayloadJson,
                CreatedAt = pending.Message.CreatedAt,
            };
            try
            {
                await _sink.DeliverAsync(envelope, cancellationToken);
                await _store.MarkPublishedAsync(pending, _clock.GetUtcNow().UtcDateTime, cancellationToken);
                delivered++;
                _logger.LogInformation(DeliveredEvent, "GL event {EventId} ({Type}) of tenant {TenantId} delivered",
                    LogSanitizer.Sanitize(envelope.EventId), LogSanitizer.Sanitize(envelope.Type), LogSanitizer.Sanitize(envelope.TenantId));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var attempts = pending.Message.PublishAttempts + 1;
                var backoff = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(attempts - 1, 20)), _options.MaxBackoff.TotalMinutes));
                var reason = ex is GlEventDeliveryException ? ex.Message : $"Delivery failed ({ex.GetType().Name}).";
                _logger.LogWarning(DeliveryFailedEvent,
                    "GL event {EventId} ({Type}) of tenant {TenantId} not delivered (attempt {Attempt}): {Reason}; next attempt in {Backoff}",
                    LogSanitizer.Sanitize(envelope.EventId), LogSanitizer.Sanitize(envelope.Type), LogSanitizer.Sanitize(envelope.TenantId), attempts, LogSanitizer.Sanitize(reason), backoff);
                try
                {
                    await _store.MarkFailedAsync(pending, reason, now.Add(backoff), cancellationToken);
                }
                catch (Exception markError) when (markError is not OperationCanceledException)
                {
                    _logger.LogWarning("Could not record the failed delivery of GL event {EventId}: {Error}", LogSanitizer.Sanitize(envelope.EventId), markError.GetType().Name);
                }
            }
        }
        return delivered;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GL event dispatch cycle failed; retrying next cycle");
            }
            try { await Task.Delay(_options.Interval, _clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
