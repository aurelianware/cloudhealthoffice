using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Security;

namespace CloudHealthOffice.GoldenPath.Tests.Harness;

/// <summary>Answers every request with a delegate: an in-process stand-in for another service.</summary>
internal sealed class DelegatingServiceHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public DelegatingServiceHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => _respond(request, ct);

    public static HttpResponseMessage Json(object value, JsonSerializerOptions options) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, value.GetType(), options), Encoding.UTF8, "application/json"),
    };
}

/// <summary>Named clients routed to in-process handlers; unknown names get a 404 service.</summary>
internal sealed class RoutingHttpClientFactory : IHttpClientFactory
{
    private readonly Dictionary<string, HttpMessageHandler> _handlers = new(StringComparer.Ordinal);

    public RoutingHttpClientFactory Route(string name, HttpMessageHandler handler)
    {
        _handlers[name] = handler;
        return this;
    }

    public HttpClient CreateClient(string name)
    {
        var handler = _handlers.TryGetValue(name, out var h)
            ? h
            : new DelegatingServiceHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        return new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri($"http://{name.ToLowerInvariant()}") };
    }
}

/// <summary>The wire formats of the services on the path, as their Program.cs configure MVC.</summary>
internal static class Wire
{
    /// <summary>claims-service: plain <c>AddControllers()</c> (camelCase, numeric enums).</summary>
    public static readonly JsonSerializerOptions ClaimsService = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// benefit-plan-service: <c>AddCloudHealthOfficeJsonOptions()</c> (string enums,
    /// integers rejected) plus <c>BenefitJsonConverter</c>.
    /// </summary>
    public static readonly JsonSerializerOptions BenefitPlanService = CreateBenefitPlanService();

    private static JsonSerializerOptions CreateBenefitPlanService()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        o.Converters.Add(new BenefitPlanService.Models.Benefits.BenefitJsonConverter());
        return o;
    }
}

/// <summary>The user releasing the payment run (not its creator: maker-checker).</summary>
internal sealed class ApprovingUser : ICurrentActor
{
    public ApprovingUser(string tenantId) => TenantId = tenantId;
    public bool IsAuthenticated => true;
    public string UserId => "golden-checker";
    public string? DisplayName => null;
    public string? Email => null;
    public string TenantId { get; }
    public bool IsService => false;
    public IReadOnlyCollection<string> Roles => Array.Empty<string>();
    public bool HasPermission(string permission) => true;
}
