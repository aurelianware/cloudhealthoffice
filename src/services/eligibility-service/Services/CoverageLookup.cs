using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace EligibilityService.Services;

/// <summary>A coverage lookup failed; this is not evidence of ineligibility.</summary>
public sealed class CoverageLookupUnavailableException : Exception
{
    public CoverageLookupUnavailableException(Exception inner)
        : base("Coverage could not be verified. Retry the lookup.", inner) { }
}

/// <summary>
/// The coverage-service /active contract: 404 or a valid empty list means no
/// coverage. Authentication, upstream, transport and unreadable-response failures
/// must never be turned into a negative eligibility determination.
/// </summary>
internal static class CoverageLookup
{
    public static async Task<List<T>> ReadAsync<T>(
        HttpClient client, HttpRequestMessage request, CancellationToken ct = default)
    {
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return new List<T>();
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct)
                ?? throw new JsonException("Coverage response must be a list, not null.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            throw new CoverageLookupUnavailableException(ex);
        }
    }
}
