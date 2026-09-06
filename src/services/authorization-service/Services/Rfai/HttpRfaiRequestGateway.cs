using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuthorizationService.Services.Rfai;

/// <summary>
/// Raises additional-information requests against rfai-service over its internal
/// API.
///
/// authorization-service does not own the request record and does not keep a
/// copy of it: it sends the decision's request and keeps the handle that comes
/// back. Every call carries the authorization's OWN tenant explicitly, so the
/// request is recorded in the same partition as the authorization that caused it
/// even when this runs on a background thread with no ambient HTTP context.
/// </summary>
public sealed class HttpRfaiRequestGateway : IRfaiRequestGateway
{
    public const string HttpClientName = "RfaiService";

    /// <summary>The header rfai-service's TenantMiddleware reads.</summary>
    private const string TenantHeader = "X-Tenant-ID";

    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<HttpRfaiRequestGateway> _logger;

    public HttpRfaiRequestGateway(
        IHttpClientFactory factory, ILogger<HttpRfaiRequestGateway> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<RfaiRequestHandle?> EnsureRequestAsync(
        RfaiRequestCommand command, CancellationToken ct = default)
    {
        var payload = new
        {
            authNumber = command.AuthNumber,
            authorizationId = command.AuthorizationId,
            correlationKey = command.CorrelationKey,
            memberId = command.MemberId,
            requestingProviderNpi = command.RequestingProviderNpi,
            reviewDecision = command.ReviewDecision,
            reasonCode = command.ReasonCode,
            reasonDescription = command.ReasonDescription,
            requestedBy = command.RequestedBy,
            requestSource = command.RequestSource,
            dueDate = command.DueDate,
            notes = command.Notes,
            requestedItems = command.RequestedItems.Select(i => new
            {
                code = i.Code,
                loincCode = i.LoincCode,
                description = i.Description,
                required = i.Required,
                serviceLineProcedureCode = i.ServiceLineProcedureCode,
                diagnosisCode = i.DiagnosisCode,
            }).ToArray(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/rfai")
        {
            Content = JsonContent.Create(payload, options: WireFormat),
        };
        request.Headers.TryAddWithoutValidation(TenantHeader, command.TenantId);

        using var response = await _factory.CreateClient(HttpClientName).SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Status category only — never the body, which echoes the request.
            _logger.LogWarning(
                "rfai-service returned {Status} when raising an additional-information request.",
                (int)response.StatusCode);
            return null;
        }

        var created = await response.Content.ReadFromJsonAsync<RfaiCaseHandleDto>(WireFormat, ct);

        if (created is null || string.IsNullOrWhiteSpace(created.Id)
            || string.IsNullOrWhiteSpace(created.TrackingId))
        {
            _logger.LogWarning(
                "rfai-service accepted the request but returned no usable handle.");
            return null;
        }

        return new RfaiRequestHandle
        {
            Id = created.Id,
            TrackingId = created.TrackingId,
            // 201 means this call created it; 200 means it replayed onto an
            // existing request. rfai-service distinguishes the two deliberately.
            Created = response.StatusCode == System.Net.HttpStatusCode.Created,
        };
    }

    /// <summary>
    /// The two fields of rfai-service's response this service needs. Everything
    /// else on the case — requested items, received artifacts, notes — stays in
    /// rfai-service, so there is nothing here to fall out of date with it.
    /// </summary>
    private sealed record RfaiCaseHandleDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("trackingId")]
        public string? TrackingId { get; init; }
    }
}
