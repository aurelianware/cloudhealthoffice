using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Programs.MediCal;
using CloudHealthOffice.Infrastructure.Security;

namespace AuthorizationService.Services.StatePrograms;

/// <summary>An active coverage on the requested date and the benefit plan it names.</summary>
public sealed record ActiveCoveragePlan(string? CoverageId, string PlanId);

/// <summary>What the APL 26-009 check needs from the member record.</summary>
public sealed record StateDentalMember(DateTime? DateOfBirth, IReadOnlyList<AidCodeSpan> AidCodes);

/// <summary>
/// The member reads <see cref="StateOnlyAdultDentalPaCheck"/> makes. Both throw
/// <see cref="HttpRequestException"/> when the service could not be asked, so
/// the check can tell "nothing on file" from "did not get to look".
/// </summary>
public interface IStateDentalMemberLookup
{
    /// <summary>The member's coverages active on <paramref name="asOf"/>; empty when there are none.</summary>
    Task<IReadOnlyList<ActiveCoveragePlan>> ActiveCoverageAsync(string tenantId, string memberId, DateTime asOf, CancellationToken ct);

    /// <summary>The member's date of birth and aid codes, or <c>null</c> when member-service has no such member.</summary>
    Task<StateDentalMember?> MemberAsync(string tenantId, string memberId, CancellationToken ct);
}

/// <summary>
/// Reads coverage-service and member-service with this service's own identity:
/// the provider submitting a prior authorization need not be allowed to read
/// coverage or member records.
/// </summary>
public sealed class HttpStateDentalMemberLookup(IHttpClientFactory clients) : IStateDentalMemberLookup
{
    public const string CoverageClientName = "StateDentalCoverage";
    public const string MemberClientName = "StateDentalMember";
    public const string DefaultCoverageServiceUrl = "http://coverage-service:8080";
    public const string DefaultMemberServiceUrl = "http://member-service:8080";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ActiveCoveragePlan>> ActiveCoverageAsync(
        string tenantId, string memberId, DateTime asOf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return [];
        var path = $"api/v1/coverage/member/{Uri.EscapeDataString(memberId)}/active?serviceDate={asOf:yyyy-MM-dd}";
        using var request = Request(path, tenantId);
        using var response = await clients.CreateClient(CoverageClientName).SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
        return json.RootElement.EnumerateArray()
            .Select(c => new ActiveCoveragePlan(Text(c, "id"), Text(c, "planId") ?? string.Empty))
            .Where(c => c.PlanId.Length > 0)
            .ToList();
    }

    public async Task<StateDentalMember?> MemberAsync(string tenantId, string memberId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return null;
        using var request = Request($"api/v1/members/{Uri.EscapeDataString(memberId)}", tenantId);
        using var response = await clients.CreateClient(MemberClientName).SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<MemberDto>(Json, ct).ConfigureAwait(false);
        if (dto is null) return null;
        var aidCodes = (dto.AidCodes ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Code) && a.EffectiveDate is not null)
            .Select(a => new AidCodeSpan(a.Code!.Trim(), a.EffectiveDate!.Value, a.EndDate))
            .ToList();
        var dob = dto.DateOfBirth is { } d && d != default ? d.Date : (DateTime?)null;
        return new StateDentalMember(dob, aidCodes);
    }

    private static HttpRequestMessage Request(string path, string tenantId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Tenant-ID", tenantId);
        request.UseChoServiceIdentity();
        return request;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private sealed class MemberDto
    {
        public DateTime? DateOfBirth { get; set; }
        public List<AidCodeDto>? AidCodes { get; set; }
    }

    private sealed class AidCodeDto
    {
        public string? Code { get; set; }
        public DateTime? EffectiveDate { get; set; }
        public DateTime? EndDate { get; set; }
    }
}
