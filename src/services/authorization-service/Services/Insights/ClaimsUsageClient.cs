using System.Net;
using System.Net.Http.Json;

namespace AuthorizationService.Services.Insights;

/// <summary>Which prior authorization numbers claims carry (claims-service POST /api/claims/authorization-usage).</summary>
public interface IClaimsUsageClient
{
    /// <summary>
    /// Claim counts by authorization number (numbers no claim carries are
    /// absent), or <see cref="ClaimsUsage.Unavailable"/> with the reason.
    /// </summary>
    Task<ClaimsUsage> GetUsageAsync(IReadOnlyCollection<string> authorizationNumbers, CancellationToken ct = default);
}

public sealed record ClaimsUsage(IReadOnlyDictionary<string, int>? Counts, string? UnavailableReason)
{
    public static ClaimsUsage Unavailable(string reason) => new(null, reason);
}

/// <summary>
/// The factory client carries the caller's CHO token (ChoOutboundTokenHandler),
/// so claims-service applies the caller's own claims:read: a role that can't
/// read claims gets "unavailable", never a silently empty answer.
/// </summary>
public sealed class HttpClaimsUsageClient : IClaimsUsageClient
{
    public const string HttpClientName = "ClaimsUsage";
    private const int BatchSize = 2000;

    private readonly HttpClient _http;
    private readonly ILogger<HttpClaimsUsageClient> _logger;

    public HttpClaimsUsageClient(IHttpClientFactory factory, ILogger<HttpClaimsUsageClient> logger)
    {
        _http = factory.CreateClient(HttpClientName);
        _logger = logger;
    }

    public async Task<ClaimsUsage> GetUsageAsync(IReadOnlyCollection<string> authorizationNumbers, CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var batch in authorizationNumbers.Distinct(StringComparer.Ordinal).Chunk(BatchSize))
        {
            try
            {
                using var response = await _http.PostAsJsonAsync("api/claims/authorization-usage", new { authorizationNumbers = batch }, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return ClaimsUsage.Unavailable("Your role can't read claims, so claim usage can't be checked.");
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("claims-service authorization usage returned {StatusCode}", (int)response.StatusCode);
                    return ClaimsUsage.Unavailable("Claims Service couldn't be reached.");
                }
                foreach (var row in await response.Content.ReadFromJsonAsync<List<UsageRow>>(cancellationToken: ct) ?? [])
                    counts[row.AuthorizationNumber] = row.ClaimCount;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "claims-service authorization usage failed");
                return ClaimsUsage.Unavailable("Claims Service couldn't be reached.");
            }
        }
        return new ClaimsUsage(counts, null);
    }

    private sealed record UsageRow(string AuthorizationNumber, int ClaimCount);
}
