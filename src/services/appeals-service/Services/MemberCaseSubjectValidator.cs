using System.Net;
using System.Text.Json;

namespace AppealsService.Services;

public interface IMemberCaseSubjectValidator
{
    Task<bool> IsPreServiceMedicalDenialAsync(string? authorizationId, string memberId, DateTime receivedAt, CancellationToken ct);
}

public sealed class HttpMemberCaseSubjectValidator(IHttpClientFactory clients) : IMemberCaseSubjectValidator
{
    public async Task<bool> IsPreServiceMedicalDenialAsync(string? authorizationId, string memberId, DateTime receivedAt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizationId)) return false;
        using var response = await clients.CreateClient("ContactAppealAuthorization")
            .GetAsync("api/authorizations/" + Uri.EscapeDataString(authorizationId), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return false;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var a = json.RootElement;
        if (!a.TryGetProperty("memberId", out var member) || member.GetString() != memberId
            || !a.TryGetProperty("status", out var status) || status.GetString() is not ("Denied" or "Modified")
            || !a.TryGetProperty("requestedServiceDateFrom", out var date) || !date.TryGetDateTime(out var serviceDate)
            || serviceDate.Date < receivedAt.Date) return false;
        // Restrict the initial track to plain medical procedures. Drug appeals have different clocks.
        if (a.TryGetProperty("serviceTypeCode", out var type) && type.GetString() == "88") return false;
        if (!a.TryGetProperty("requestedServices", out var lines) || lines.GetArrayLength() == 0) return false;
        foreach (var line in lines.EnumerateArray())
        {
            if (line.TryGetProperty("productOrServiceSystem", out var system) && !string.IsNullOrWhiteSpace(system.GetString()))
                return false;
            if (!line.TryGetProperty("procedureCode", out var code) || code.GetString() is not { Length: 5 } procedure
                || procedure.StartsWith('J')) return false;
        }
        return true;
    }
}
