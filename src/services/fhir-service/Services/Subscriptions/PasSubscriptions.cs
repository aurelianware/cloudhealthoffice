using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FhirService.Services.Subscriptions;

/// <summary>
/// Topic-based Subscriptions (FHIR Subscriptions R5 Backport for R4) for the
/// one topic CHO publishes: Da Vinci PAS's pended-authorization topic. A
/// provider system subscribes, CHO confirms the endpoint with a handshake, and
/// when a pended prior authorization is decided CHO sends the updated
/// ClaimResponse (PAS 2.2.1 §Subscription).
///
/// <para>
/// A Subscription names an endpoint CHO will POST to, which makes it an SSRF
/// vector: only hosts listed in <see cref="PasSubscriptionOptions.AllowedEndpointHosts"/>
/// are accepted, and only over https outside a development host.
/// </para>
/// </summary>
public static class PasSubscriptionTopic
{
    public const string Url = "http://hl7.org/fhir/us/davinci-pas/SubscriptionTopic/PASSubscriptionTopic";

    public const string BackportSubscriptionProfile =
        "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-subscription";

    public const string BackportStatusProfile =
        "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-subscription-status-r4";

    public const string BackportNotificationProfile =
        "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-subscription-notification-r4";

    internal const string FilterCriteriaUrl =
        "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-filter-criteria";

    internal const string PayloadContentUrl =
        "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-payload-content";

    /// <summary>The PAS topic's one filter: the submitting organization's identifier.</summary>
    internal const string OrgIdentifierFilter = "org-identifier";
}

/// <summary>Configuration section <c>Subscriptions</c>.</summary>
public sealed class PasSubscriptionOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>Off by default: no Subscription is accepted and none is advertised.</summary>
    public bool Enabled { get; set; }

    /// <summary>The hosts a rest-hook endpoint may name. Empty means none.</summary>
    public List<string> AllowedEndpointHosts { get; set; } = [];

    /// <summary>How often pended authorizations are re-read for a decision.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after creation the handshake is sent, so the subscriber is ready for it.</summary>
    public TimeSpan HandshakeDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Pended authorizations are watched this long, then dropped.</summary>
    public TimeSpan WatchFor { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>A stored Subscription.</summary>
public sealed record PasSubscriptionRecord
{
    public required string Id { get; init; }
    public required string TenantId { get; init; }
    public required string Status { get; set; }
    public required string Endpoint { get; init; }
    public IReadOnlyList<string> Headers { get; init; } = [];
    public required string PayloadContent { get; init; }
    public string? OrgIdentifier { get; init; }
    public DateTimeOffset? End { get; init; }
    public long EventCount { get; set; }

    /// <summary>The Subscription as the client sent it, with CHO's id and status.</summary>
    public required string Resource { get; set; }
}

/// <summary>A pended authorization CHO is watching for a decision.</summary>
public sealed record PasPendedWatch(
    string TenantId, string AuthorizationNumber, string? ProviderNpi, DateTimeOffset Since);

public interface IPasSubscriptionStore
{
    Task SaveAsync(PasSubscriptionRecord subscription, CancellationToken ct);
    Task<PasSubscriptionRecord?> GetAsync(string tenantId, string id, CancellationToken ct);
    Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct);
    Task<IReadOnlyList<PasSubscriptionRecord>> ActiveAsync(string tenantId, CancellationToken ct);

    Task WatchAsync(PasPendedWatch watch, CancellationToken ct);
    Task<IReadOnlyList<PasPendedWatch>> WatchesAsync(CancellationToken ct);
    Task UnwatchAsync(PasPendedWatch watch, CancellationToken ct);
}

/// <summary>For tests and single-instance development hosts.</summary>
public sealed class InMemoryPasSubscriptionStore : IPasSubscriptionStore
{
    private readonly ConcurrentDictionary<(string, string), PasSubscriptionRecord> _subscriptions = new();
    private readonly ConcurrentDictionary<(string, string), PasPendedWatch> _watches = new();

    public Task SaveAsync(PasSubscriptionRecord subscription, CancellationToken ct)
    {
        _subscriptions[(subscription.TenantId, subscription.Id)] = subscription;
        return Task.CompletedTask;
    }

    public Task<PasSubscriptionRecord?> GetAsync(string tenantId, string id, CancellationToken ct)
        => Task.FromResult(_subscriptions.TryGetValue((tenantId, id), out var s) ? s : null);

    public Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct)
        => Task.FromResult(_subscriptions.TryRemove((tenantId, id), out _));

    public Task<IReadOnlyList<PasSubscriptionRecord>> ActiveAsync(string tenantId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PasSubscriptionRecord>>(
            _subscriptions.Values.Where(s => s.TenantId == tenantId && s.Status == "active").ToList());

    public Task WatchAsync(PasPendedWatch watch, CancellationToken ct)
    {
        _watches[(watch.TenantId, watch.AuthorizationNumber)] = watch;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PasPendedWatch>> WatchesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PasPendedWatch>>(_watches.Values.ToList());

    public Task UnwatchAsync(PasPendedWatch watch, CancellationToken ct)
    {
        _watches.TryRemove((watch.TenantId, watch.AuthorizationNumber), out _);
        return Task.CompletedTask;
    }
}

/// <summary>Durable store: one collection of Subscriptions, one of watched pended authorizations.</summary>
public sealed class MongoPasSubscriptionStore : IPasSubscriptionStore
{
    private readonly IMongoCollection<BsonDocument> _subscriptions;
    private readonly IMongoCollection<BsonDocument> _watches;

    public MongoPasSubscriptionStore(IMongoClient client, IConfiguration configuration)
    {
        var database = client.GetDatabase(configuration["MongoDb:DatabaseName"] ?? "CloudHealthOffice");
        _subscriptions = database.GetCollection<BsonDocument>("pas_subscriptions");
        _watches = database.GetCollection<BsonDocument>("pas_pended_watches");
    }

    public Task SaveAsync(PasSubscriptionRecord s, CancellationToken ct)
        => _subscriptions.ReplaceOneAsync(Key(s.TenantId, s.Id), new BsonDocument
        {
            ["tenantId"] = s.TenantId,
            ["id"] = s.Id,
            ["status"] = s.Status,
            ["endpoint"] = s.Endpoint,
            ["headers"] = new BsonArray(s.Headers),
            ["payloadContent"] = s.PayloadContent,
            ["orgIdentifier"] = s.OrgIdentifier is null ? BsonNull.Value : s.OrgIdentifier,
            ["end"] = s.End is null ? BsonNull.Value : s.End.Value.UtcDateTime,
            ["eventCount"] = s.EventCount,
            ["resource"] = s.Resource,
        }, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<PasSubscriptionRecord?> GetAsync(string tenantId, string id, CancellationToken ct)
    {
        var document = await _subscriptions.Find(Key(tenantId, id)).FirstOrDefaultAsync(ct);
        return document is null ? null : FromDocument(document);
    }

    public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken ct)
        => (await _subscriptions.DeleteOneAsync(Key(tenantId, id), ct)).DeletedCount > 0;

    public async Task<IReadOnlyList<PasSubscriptionRecord>> ActiveAsync(string tenantId, CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("tenantId", tenantId)
                     & Builders<BsonDocument>.Filter.Eq("status", "active");
        return (await _subscriptions.Find(filter).ToListAsync(ct)).Select(FromDocument).ToList();
    }

    public Task WatchAsync(PasPendedWatch w, CancellationToken ct)
        => _watches.ReplaceOneAsync(WatchKey(w), new BsonDocument
        {
            ["tenantId"] = w.TenantId,
            ["authorizationNumber"] = w.AuthorizationNumber,
            ["providerNpi"] = w.ProviderNpi is null ? BsonNull.Value : w.ProviderNpi,
            ["since"] = w.Since.UtcDateTime,
        }, new ReplaceOptions { IsUpsert = true }, ct);

    public async Task<IReadOnlyList<PasPendedWatch>> WatchesAsync(CancellationToken ct)
        => (await _watches.Find(FilterDefinition<BsonDocument>.Empty).Limit(1000).ToListAsync(ct))
            .Select(d => new PasPendedWatch(
                d["tenantId"].AsString,
                d["authorizationNumber"].AsString,
                d["providerNpi"].IsBsonNull ? null : d["providerNpi"].AsString,
                new DateTimeOffset(d["since"].ToUniversalTime(), TimeSpan.Zero)))
            .ToList();

    public Task UnwatchAsync(PasPendedWatch w, CancellationToken ct) => _watches.DeleteOneAsync(WatchKey(w), ct);

    private static FilterDefinition<BsonDocument> Key(string tenantId, string id)
        => Builders<BsonDocument>.Filter.Eq("tenantId", tenantId) & Builders<BsonDocument>.Filter.Eq("id", id);

    private static FilterDefinition<BsonDocument> WatchKey(PasPendedWatch w)
        => Builders<BsonDocument>.Filter.Eq("tenantId", w.TenantId)
           & Builders<BsonDocument>.Filter.Eq("authorizationNumber", w.AuthorizationNumber);

    private static PasSubscriptionRecord FromDocument(BsonDocument d) => new()
    {
        Id = d["id"].AsString,
        TenantId = d["tenantId"].AsString,
        Status = d["status"].AsString,
        Endpoint = d["endpoint"].AsString,
        Headers = d["headers"].AsBsonArray.Select(h => h.AsString).ToList(),
        PayloadContent = d["payloadContent"].AsString,
        OrgIdentifier = d["orgIdentifier"].IsBsonNull ? null : d["orgIdentifier"].AsString,
        End = d["end"].IsBsonNull ? null : new DateTimeOffset(d["end"].ToUniversalTime(), TimeSpan.Zero),
        EventCount = d["eventCount"].ToInt64(),
        Resource = d["resource"].AsString,
    };
}

/// <summary>Why a Subscription was refused, for the 400 the client gets.</summary>
public sealed record PasSubscriptionRefusal(string Message);

/// <summary>Accepts, confirms and notifies PAS Subscriptions.</summary>
public sealed class PasSubscriptionService
{
    public const string HttpClientName = "PasSubscriptionNotifications";

    private readonly IPasSubscriptionStore _store;
    private readonly IOptionsMonitor<PasSubscriptionOptions> _options;
    private readonly IHttpClientFactory _http;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PasSubscriptionService> _logger;

    public PasSubscriptionService(
        IPasSubscriptionStore store, IOptionsMonitor<PasSubscriptionOptions> options, IHttpClientFactory http,
        IHostEnvironment environment, ILogger<PasSubscriptionService> logger)
    {
        _store = store;
        _options = options;
        _http = http;
        _environment = environment;
        _logger = logger;
    }

    public bool Enabled => _options.CurrentValue.Enabled;

    public IPasSubscriptionStore Store => _store;

    /// <summary>
    /// Validates and stores a new Subscription with status <c>requested</c>,
    /// and schedules its handshake. Returns the stored resource, or why not.
    /// </summary>
    public async Task<(PasSubscriptionRecord? Created, PasSubscriptionRefusal? Refusal)> CreateAsync(
        JsonObject subscription, string tenantId, string fhirBaseUrl, CancellationToken ct)
    {
        var (record, refusal) = Parse(subscription, tenantId, Guid.NewGuid().ToString("n"));
        if (refusal is not null)
            return (null, refusal);

        await _store.SaveAsync(record!, ct);
        _logger.LogInformation("PAS Subscription {Id} created for tenant {Tenant}",
            record!.Id, LogSanitizer.Sanitize(tenantId));

        var delay = _options.CurrentValue.HandshakeDelay;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                await HandshakeAsync(record, fhirBaseUrl, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PAS Subscription {Id} handshake failed", record.Id);
            }
        }, CancellationToken.None);

        return (record, null);
    }

    /// <summary>Replaces a Subscription's settings; it is confirmed again with a new handshake.</summary>
    public async Task<(PasSubscriptionRecord? Updated, PasSubscriptionRefusal? Refusal)> UpdateAsync(
        JsonObject subscription, string tenantId, string id, string fhirBaseUrl, CancellationToken ct)
    {
        if (await _store.GetAsync(tenantId, id, ct) is null)
            return (null, null);

        var (record, refusal) = Parse(subscription, tenantId, id);
        if (refusal is not null)
            return (null, refusal);

        await _store.SaveAsync(record!, ct);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.CurrentValue.HandshakeDelay);
                await HandshakeAsync(record!, fhirBaseUrl, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PAS Subscription {Id} handshake failed", record!.Id);
            }
        }, CancellationToken.None);
        return (record, null);
    }

    internal (PasSubscriptionRecord? Record, PasSubscriptionRefusal? Refusal) Parse(
        JsonObject subscription, string tenantId, string id)
    {
        if (subscription["resourceType"]?.GetValue<string>() != "Subscription")
            return (null, new("The body is not a Subscription."));

        if (subscription["criteria"]?.GetValue<string>() != PasSubscriptionTopic.Url)
            return (null, new($"CHO publishes one topic, {PasSubscriptionTopic.Url}; criteria must name it."));

        var channel = subscription["channel"] as JsonObject;
        if (channel?["type"]?.GetValue<string>() != "rest-hook")
            return (null, new("Only rest-hook channels are supported."));

        if (!Uri.TryCreate(channel["endpoint"]?.GetValue<string>(), UriKind.Absolute, out var endpoint)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            return (null, new("channel.endpoint must be an absolute URL without user info or fragment."));

        var options = _options.CurrentValue;
        var httpsOrDevelopment = endpoint.Scheme == Uri.UriSchemeHttps
                                 || (endpoint.Scheme == Uri.UriSchemeHttp && _environment.IsDevelopment());
        if (!httpsOrDevelopment
            || !options.AllowedEndpointHosts.Any(h => string.Equals(h.Trim(), endpoint.Host, StringComparison.OrdinalIgnoreCase)))
            return (null, new("channel.endpoint is not a notification endpoint this server is configured to send to."));

        var payload = channel["payload"]?.GetValue<string>();
        if (payload is not ("application/fhir+json" or "application/json"))
            return (null, new("channel.payload must be application/fhir+json."));

        var content = (channel["_payload"]?["extension"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(e => e["url"]?.GetValue<string>() == PasSubscriptionTopic.PayloadContentUrl)
            ?["valueCode"]?.GetValue<string>() ?? "id-only";
        if (content is not ("empty" or "id-only" or "full-resource"))
            return (null, new("The backport-payload-content must be empty, id-only or full-resource."));

        string? org = null;
        foreach (var filter in ((subscription["_criteria"]?["extension"] as JsonArray)?.OfType<JsonObject>() ?? [])
                 .Where(e => e["url"]?.GetValue<string>() == PasSubscriptionTopic.FilterCriteriaUrl)
                 .Select(e => e["valueString"]?.GetValue<string>()))
        {
            var parts = filter?.Split('=', 2);
            if (parts is not { Length: 2 } || parts[0].Trim() != PasSubscriptionTopic.OrgIdentifierFilter)
                return (null, new($"The PAS topic's only filter is {PasSubscriptionTopic.OrgIdentifierFilter}."));
            // system|value, or a bare value.
            org = parts[1].Contains('|') ? parts[1][(parts[1].IndexOf('|') + 1)..].Trim() : parts[1].Trim();
        }

        DateTimeOffset? end = DateTimeOffset.TryParse(subscription["end"]?.GetValue<string>(), out var e2) ? e2 : null;

        var headers = (channel["header"] as JsonArray)?.Select(h => h?.GetValue<string>())
            .Where(h => !string.IsNullOrWhiteSpace(h) && h!.Contains(':') && !h.Contains('\n') && !h.Contains('\r'))
            .Select(h => h!)
            .ToList() ?? [];

        var stored = (JsonObject)subscription.DeepClone();
        stored["id"] = id;
        stored["status"] = "requested";

        return (new PasSubscriptionRecord
        {
            Id = id,
            TenantId = tenantId,
            Status = "requested",
            Endpoint = endpoint.AbsoluteUri,
            Headers = headers,
            PayloadContent = content,
            OrgIdentifier = org,
            End = end,
            Resource = stored.ToJsonString(),
        }, null);
    }

    /// <summary>The handshake: on a 2xx the Subscription becomes active, otherwise error.</summary>
    internal async Task HandshakeAsync(PasSubscriptionRecord subscription, string fhirBaseUrl, CancellationToken ct)
    {
        var bundle = Notification(subscription, fhirBaseUrl, "handshake", "requested", events: 0, eventNumber: null,
            focus: null);
        var delivered = await SendAsync(subscription, bundle, ct);
        subscription.Status = delivered ? "active" : "error";
        var resource = JsonNode.Parse(subscription.Resource)!.AsObject();
        resource["status"] = subscription.Status;
        subscription.Resource = resource.ToJsonString();
        await _store.SaveAsync(subscription, ct);
        _logger.LogInformation("PAS Subscription {Id} handshake {Outcome}", subscription.Id,
            delivered ? "accepted; active" : "refused; error");
    }

    /// <summary>
    /// Sends an event notification for a decided authorization to every active
    /// Subscription in its tenant whose org-identifier filter (if any) is the
    /// requesting provider's.
    /// </summary>
    public async Task NotifyAsync(
        PasPendedWatch watch, Hl7.Fhir.Model.Bundle decision, string fhirBaseUrl, CancellationToken ct)
    {
        foreach (var subscription in await _store.ActiveAsync(watch.TenantId, ct))
        {
            if (subscription.End is { } end && end < DateTimeOffset.UtcNow)
                continue;
            if (subscription.OrgIdentifier is { } org
                && !string.Equals(org, watch.ProviderNpi?.Trim(), StringComparison.Ordinal))
                continue;

            subscription.EventCount++;
            var bundle = Notification(subscription, fhirBaseUrl, "event-notification", "active",
                subscription.EventCount, subscription.EventCount, decision);
            var delivered = await SendAsync(subscription, bundle, ct);
            await _store.SaveAsync(subscription, ct);
            _logger.LogInformation(
                "PAS Subscription {Id}: notification for authorization {Auth} {Outcome}",
                subscription.Id, LogSanitizer.Sanitize(watch.AuthorizationNumber),
                delivered ? "delivered" : "not delivered");
        }
    }

    private async Task<bool> SendAsync(PasSubscriptionRecord subscription, JsonObject bundle, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint)
            {
                Content = new StringContent(bundle.ToJsonString(), Encoding.UTF8, "application/fhir+json"),
            };
            // A header named twice is sent once, with its last value: two
            // Authorization values would reach the subscriber as one
            // comma-joined credential it cannot read.
            foreach (var header in subscription.Headers)
            {
                var colon = header.IndexOf(':');
                var name = header[..colon].Trim();
                message.Headers.Remove(name);
                message.Headers.TryAddWithoutValidation(name, header[(colon + 1)..].Trim());
            }

            using var response = await _http.CreateClient(HttpClientName).SendAsync(message, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "PAS Subscription {Id}: the endpoint could not be reached", subscription.Id);
            return false;
        }
    }

    /// <summary>
    /// An R4 backport notification: a history Bundle whose first entry is the
    /// SubscriptionStatus (Parameters), followed, for full-resource, by the
    /// ClaimResponse and what it references.
    /// </summary>
    internal static JsonObject Notification(
        PasSubscriptionRecord subscription, string fhirBaseUrl, string type, string status,
        long events, long? eventNumber, Hl7.Fhir.Model.Bundle? focus)
    {
        var baseUrl = fhirBaseUrl.TrimEnd('/');
        var parameters = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "subscription",
                ["valueReference"] = new JsonObject { ["reference"] = $"{baseUrl}/Subscription/{subscription.Id}" },
            },
            new JsonObject { ["name"] = "topic", ["valueCanonical"] = PasSubscriptionTopic.Url },
            new JsonObject { ["name"] = "status", ["valueCode"] = status },
            new JsonObject { ["name"] = "type", ["valueCode"] = type },
            new JsonObject { ["name"] = "events-since-subscription-start", ["valueString"] = events.ToString() },
        };

        var entries = new JsonArray();
        var resources = new List<JsonObject>();
        if (eventNumber is { } number && focus is not null)
        {
            var serialized = JsonNode.Parse(
                new Hl7.Fhir.Serialization.FhirJsonSerializer().SerializeToString(focus))!.AsObject();
            foreach (var entry in (serialized["entry"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                if (entry["resource"] is JsonObject resource && resource["id"] is not null)
                    resources.Add((JsonObject)resource.DeepClone());
            }

            var claimResponse = resources.FirstOrDefault(r => r["resourceType"]?.GetValue<string>() == "ClaimResponse");
            var parts = new JsonArray
            {
                new JsonObject { ["name"] = "event-number", ["valueString"] = number.ToString() },
                new JsonObject { ["name"] = "timestamp", ["valueInstant"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
            };
            if (claimResponse is not null && subscription.PayloadContent != "empty")
            {
                parts.Add(new JsonObject
                {
                    ["name"] = "focus",
                    ["valueReference"] = new JsonObject
                    {
                        ["reference"] = $"{baseUrl}/ClaimResponse/{claimResponse["id"]!.GetValue<string>()}",
                    },
                });
            }

            parameters.Add(new JsonObject { ["name"] = "notification-event", ["part"] = parts });
        }

        entries.Add(new JsonObject
        {
            ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
            ["resource"] = new JsonObject
            {
                ["resourceType"] = "Parameters",
                ["id"] = Guid.NewGuid().ToString(),
                ["meta"] = new JsonObject { ["profile"] = new JsonArray(PasSubscriptionTopic.BackportStatusProfile) },
                ["parameter"] = parameters,
            },
            ["request"] = new JsonObject
            {
                ["method"] = "GET",
                ["url"] = $"{baseUrl}/Subscription/{subscription.Id}/$status",
            },
            ["response"] = new JsonObject { ["status"] = "200" },
        });

        if (subscription.PayloadContent != "empty")
        {
            foreach (var resource in resources)
            {
                var url = $"{baseUrl}/{resource["resourceType"]!.GetValue<string>()}/{resource["id"]!.GetValue<string>()}";
                var entry = new JsonObject
                {
                    ["fullUrl"] = url,
                    ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = url },
                    ["response"] = new JsonObject { ["status"] = "200" },
                };
                if (subscription.PayloadContent == "full-resource")
                    entry["resource"] = resource;
                entries.Add(entry);
            }
        }

        return new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["id"] = Guid.NewGuid().ToString(),
            ["meta"] = new JsonObject { ["profile"] = new JsonArray(PasSubscriptionTopic.BackportNotificationProfile) },
            ["type"] = "history",
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["entry"] = entries,
        };
    }
}

/// <summary>
/// Re-reads each watched pended authorization and, once it is decided,
/// notifies the tenant's Subscriptions with the updated ClaimResponse. Reads
/// run in a background tenant context, so they carry fhir-service's own
/// service token for that tenant.
/// </summary>
public sealed class PasSubscriptionNotificationWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<PasSubscriptionOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PasSubscriptionNotificationWorker> _logger;

    public PasSubscriptionNotificationWorker(
        IServiceProvider services, IOptionsMonitor<PasSubscriptionOptions> options, IConfiguration configuration,
        ILogger<PasSubscriptionNotificationWorker> logger)
    {
        _services = services;
        _options = options;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            if (options.Enabled)
            {
                try
                {
                    await PollOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "PAS Subscription notification poll failed");
                }
            }

            await Task.Delay(options.PollInterval, stoppingToken);
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        using var root = _services.CreateScope();
        var store = root.ServiceProvider.GetRequiredService<IPasSubscriptionStore>();
        var watches = await store.WatchesAsync(ct);
        // The base the notification names its Subscription and ClaimResponse by.
        var fhirBase = _configuration["Subscriptions:FhirBaseUrl"]
                       ?? _configuration["Fhir:ServerBaseUrl"] ?? "http://localhost/fhir/r4";

        foreach (var watch in watches)
        {
            if (DateTimeOffset.UtcNow - watch.Since > _options.CurrentValue.WatchFor)
            {
                await store.UnwatchAsync(watch, ct);
                continue;
            }

            using var scope = _services.CreateScope();
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = BackgroundTenantContext.Create(scope.ServiceProvider, watch.TenantId);
            try
            {
                var record = await scope.ServiceProvider.GetRequiredService<IPriorAuthorizationStore>()
                    .GetByAuthorizationNumberAsync(watch.AuthorizationNumber, ct);
                if (record is null
                    || record.Status == PriorAuthorizationStatus.Pended)
                    continue;

                var decision = scope.ServiceProvider.GetRequiredService<PasResponseBuilder>().BuildInquiryResponse(record);
                await scope.ServiceProvider.GetRequiredService<PasSubscriptionService>()
                    .NotifyAsync(watch, decision, fhirBase, ct);
                await store.UnwatchAsync(watch, ct);
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }
    }
}
