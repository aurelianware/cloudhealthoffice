using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Services.Adjudication.CodeValidity;

/// <summary>One code to check: its system, the code, and the date of service.</summary>
public sealed record CodeValidityQuery(string System, string Code, DateOnly Date);

/// <summary>
/// terminology-service's answer (CodeSystem/$batch-validate-code). Status is
/// valid, invalid or unknown; unknown (nothing loaded for that date or system)
/// is never a finding.
/// </summary>
public sealed class CodeValidityAnswer
{
    public bool? Result { get; set; }
    public string Status { get; set; } = "unknown";
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Display { get; set; }
    public string? Reason { get; set; }
    public string? Message { get; set; }
    public string? ReleaseVersion { get; set; }

    public bool IsInvalid => string.Equals(Status, "invalid", StringComparison.OrdinalIgnoreCase);
}

public interface ICodeValidityClient
{
    /// <summary>
    /// Answers in query order, or null when terminology-service could not be
    /// reached, timed out or answered with an error (the check is then skipped).
    /// </summary>
    Task<IReadOnlyList<CodeValidityAnswer>?> ValidateAsync(
        string tenantId, IReadOnlyList<CodeValidityQuery> queries, TimeSpan timeout, CancellationToken ct);
}

public sealed class HttpCodeValidityClient : ICodeValidityClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpCodeValidityClient> _logger;

    public HttpCodeValidityClient(IHttpClientFactory httpClientFactory, ILogger<HttpCodeValidityClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CodeValidityAnswer>?> ValidateAsync(
        string tenantId, IReadOnlyList<CodeValidityQuery> queries, TimeSpan timeout, CancellationToken ct)
    {
        if (queries.Count == 0) return [];

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            var client = _httpClientFactory.CreateClient(UpstreamClientNames.TerminologyService);
            using var request = new HttpRequestMessage(HttpMethod.Post, "fhir/CodeSystem/$batch-validate-code")
            {
                Content = JsonContent.Create(
                    queries.Select(q => new { system = q.System, code = q.Code, date = q.Date.ToString("yyyy-MM-dd") }),
                    options: Json),
            };
            if (!string.IsNullOrWhiteSpace(tenantId)) request.Headers.Add("X-Tenant-ID", tenantId);

            using var response = await client.SendAsync(request, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Code validity check returned {StatusCode}; claim not checked", (int)response.StatusCode);
                return null;
            }

            var answers = await response.Content.ReadFromJsonAsync<List<CodeValidityAnswer>>(Json, linked.Token)
                .ConfigureAwait(false);
            return answers is not null && answers.Count == queries.Count ? answers : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Code validity check timed out for tenant {TenantId}; claim not checked",
                LogSanitizer.Sanitize(tenantId));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Code validity check failed for tenant {TenantId}; claim not checked",
                LogSanitizer.Sanitize(tenantId));
            return null;
        }
    }
}
