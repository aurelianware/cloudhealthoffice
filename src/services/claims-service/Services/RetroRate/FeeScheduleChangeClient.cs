using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.RetroRate;

/// <summary>
/// Reads fee schedule changes from benefit-plan-service
/// (<c>GET /api/v1/fee-schedules/changes</c>), which records one for every
/// create and replace of a schedule.
/// </summary>
public interface IFeeScheduleChangeClient
{
    /// <summary>
    /// The schedule's change that stored <paramref name="revision"/>, or its
    /// latest change when null. Null when there is none or benefit-plan-service
    /// could not be reached.
    /// </summary>
    Task<FeeScheduleChange?> GetChangeAsync(string tenantId, string feeScheduleId, long? revision, CancellationToken ct = default);
}

public sealed class HttpFeeScheduleChangeClient : IFeeScheduleChangeClient
{
    internal const string ChangesPath = "/api/v1/fee-schedules/changes";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpFeeScheduleChangeClient> _logger;

    public HttpFeeScheduleChangeClient(IHttpClientFactory httpClientFactory, ILogger<HttpFeeScheduleChangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<FeeScheduleChange?> GetChangeAsync(
        string tenantId, string feeScheduleId, long? revision, CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);
            var path = $"{ChangesPath}?scheduleId={Uri.EscapeDataString(feeScheduleId)}&limit=500";
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Benefit-plan-service fee schedule changes returned {StatusCode} for tenant {Tenant}",
                    response.StatusCode, LogSanitizer.Sanitize(tenantId));
                return null;
            }

            var changes = await response.Content
                .ReadFromJsonAsync<List<FeeScheduleChange>>(JsonOptions, ct)
                .ConfigureAwait(false) ?? [];
            return Pick(changes, revision);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Reading fee schedule changes failed for tenant {Tenant}", LogSanitizer.Sanitize(tenantId));
            return null;
        }
    }

    internal static FeeScheduleChange? Pick(IReadOnlyList<FeeScheduleChange> changes, long? revision) =>
        revision is { } r
            ? changes.FirstOrDefault(c => c.NewRevision == r)
            : changes.OrderByDescending(c => c.NewRevision).FirstOrDefault();
}
