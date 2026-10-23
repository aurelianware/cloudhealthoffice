using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PersonalRepresentativeService.Services;
using CloudHealthOffice.PlanConfiguration;
using System.Text.Json.Serialization;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>A request that reached a fake downstream service.</summary>
public sealed record DownstreamRequest(string Host, string Path, string? TenantHeader, string? Authorization);

/// <summary>
/// Primary handler for one named HttpClient. A fresh instance per pipeline
/// (the factory disposes expired handlers); the behaviour lives in the shared
/// fake service.
/// </summary>
public sealed class ResponderHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public ResponderHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_respond(request));

    internal static string? Tenant(HttpRequestMessage request)
        => request.Headers.TryGetValues("X-Tenant-ID", out var values) ? values.FirstOrDefault() : null;

    internal static DownstreamRequest Record(HttpRequestMessage request)
        => new(request.RequestUri!.Host, request.RequestUri.AbsolutePath, Tenant(request),
            request.Headers.Authorization?.ToString());

    internal static HttpResponseMessage Json(JsonNode body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
    };
}

/// <summary>
/// member-document-service: <c>GET api/v1/member-documents/{id}</c> scoped to
/// the request's tenant, as the real service scopes to its token's tenant.
/// </summary>
public sealed class FakeMemberDocumentService
{
    private readonly ConcurrentDictionary<(string Tenant, string Id), JsonObject> _documents = new();

    public ConcurrentBag<DownstreamRequest> Requests { get; } = new();

    /// <summary>Tenants for which the service answers 500.</summary>
    public ConcurrentDictionary<string, bool> FailingTenants { get; } = new();

    /// <summary>Tenants for which the call throws (connection refused / timeout).</summary>
    public ConcurrentDictionary<string, bool> UnreachableTenants { get; } = new();

    /// <summary>A finalized document stored in <paramref name="tenant"/> for <paramref name="memberId"/>.</summary>
    public string Add(string tenant, string memberId, params string[] relatedMemberIds)
    {
        var id = Guid.NewGuid().ToString();
        _documents[(tenant, id)] = new JsonObject
        {
            ["id"] = id,
            ["tenantId"] = tenant,
            ["memberId"] = memberId,
            ["category"] = "proof-of-authority",
            ["relatedMemberIds"] = new JsonArray(relatedMemberIds.Select(m => (JsonNode?)m).ToArray()),
            ["documentId"] = $"cat-{id}",
            ["uploadPending"] = false
        };
        return id;
    }

    /// <summary>Stores a raw body under (<paramref name="servedInTenant"/>, id); for misbehaving-service tests.</summary>
    public void AddRaw(string servedInTenant, string id, JsonObject body) => _documents[(servedInTenant, id)] = body;

    public JsonObject Get(string tenant, string id) => _documents[(tenant, id)];

    public HttpMessageHandler Handler() => new ResponderHandler(Respond);

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        Requests.Add(ResponderHandler.Record(request));
        var tenant = ResponderHandler.Tenant(request) ?? string.Empty;
        if (UnreachableTenants.ContainsKey(tenant))
            throw new HttpRequestException("Connection refused (member-document-service)");
        if (FailingTenants.ContainsKey(tenant))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        if (request.Headers.Authorization is null)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        const string prefix = "/api/v1/member-documents/";
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method != HttpMethod.Get || !path.StartsWith(prefix, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var id = Uri.UnescapeDataString(path[prefix.Length..]);
        return _documents.TryGetValue((tenant, id), out var doc)
            ? ResponderHandler.Json(doc.DeepClone())
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// tenant-service: <c>GET api/v1/tenants/{id}</c> and
/// <c>GET api/v1/configuration/resolve</c>, the two places an activation
/// control can live while the tenant controls are moving into plan
/// configuration. A resolve is answered from an <see cref="InMemoryPlanSettings"/>
/// through the real wire shape, so the service under test uses its real client.
/// </summary>
public sealed class FakeTenantService
{
    private readonly ConcurrentDictionary<string, bool?> _requireSecondPerson = new();

    private static readonly JsonSerializerOptions ResolveJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settings this tenant-service holds. Empty, so resolves answer the definition's default.</summary>
    public InMemoryPlanSettings Settings { get; } = new();

    public ConcurrentBag<DownstreamRequest> Requests { get; } = new();

    /// <summary>Tenants for which tenant-service answers 503.</summary>
    public ConcurrentDictionary<string, bool> FailingTenants { get; } = new();

    /// <summary>
    /// Sets <c>configuration.personalRepresentativeControls.requireSecondPerson</c>
    /// for the tenant; null leaves the block out. Tenants never set have no
    /// such block either.
    /// </summary>
    public void SetRequireSecondPerson(string tenant, bool? value) => _requireSecondPerson[tenant] = value;

    /// <summary>
    /// Sets the <c>personalRepresentative.requireSecondUserActivation</c>
    /// setting at tenant level, which is where the control is moving to.
    /// </summary>
    public void SetRequireSecondUserActivation(string tenant, bool value) => Settings.Set(
        tenant, TenantPersonalRepControls.SettingKey, SettingLevel.Tenant, string.Empty,
        value ? "true" : "false");

    public HttpMessageHandler Handler() => new ResponderHandler(Respond);

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        Requests.Add(ResponderHandler.Record(request));
        var tenant = ResponderHandler.Tenant(request) ?? string.Empty;
        if (FailingTenants.ContainsKey(tenant))
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (request.Headers.Authorization is null)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);

        if (request.RequestUri!.AbsolutePath.Contains("/configuration/resolve", StringComparison.Ordinal))
            return Resolve(tenant, request.RequestUri);

        var configuration = new JsonObject
        {
            ["paymentControls"] = new JsonObject { ["enforceSeparationOfDuties"] = true }
        };
        if (_requireSecondPerson.TryGetValue(tenant, out var value) && value.HasValue)
            configuration["personalRepresentativeControls"] = new JsonObject { ["requireSecondPerson"] = value.Value };

        return ResponderHandler.Json(new JsonObject
        {
            ["tenantId"] = tenant,
            ["configuration"] = configuration
        });
    }

    private HttpResponseMessage Resolve(string tenant, Uri uri)
    {
        var key = System.Web.HttpUtility.ParseQueryString(uri.Query)["key"] ?? string.Empty;
        var resolved = Settings.ResolveAsync(tenant, key, SettingScope.Empty).GetAwaiter().GetResult();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(resolved, ResolveJson), Encoding.UTF8, "application/json"),
        };
    }
}
