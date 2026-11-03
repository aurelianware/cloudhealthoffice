using System.Text.Json;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using PremiumBillingService.StateCapitation;

namespace PremiumBillingService.Clients;

/// <summary>
/// The rate cell inputs each member's 834 transactions carried, from
/// enrollment-import-service (<c>GET /api/v1/enrollment/rate-cell-inputs</c>).
/// </summary>
public interface IRateCellInputSource
{
    /// <summary>Every member's snapshots, oldest first. Throws on any failure.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<RateCellInputSnapshot>>> GetAllAsync(string tenantId, CancellationToken ct = default);
}

public sealed class HttpRateCellInputSource : IRateCellInputSource
{
    public const string HttpClientName = "EnrollmentImportService";
    internal const int PageSize = 2000;
    internal const int MaxPages = 5000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _factory;

    public HttpRateCellInputSource(IHttpClientFactory factory) => _factory = factory;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<RateCellInputSnapshot>>> GetAllAsync(string tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tenantId))
            throw new InvalidOperationException("An enrollment-import-service call must name its tenant");

        var client = _factory.CreateClient(HttpClientName);
        var result = new Dictionary<string, IReadOnlyList<RateCellInputSnapshot>>(StringComparer.OrdinalIgnoreCase);
        string? after = null;
        for (var page = 0; ; page++)
        {
            if (page >= MaxPages)
                throw new InvalidOperationException($"enrollment-import-service returned more than {MaxPages} pages of rate cell inputs");
            var url = $"/api/v1/enrollment/rate-cell-inputs?limit={PageSize}";
            if (after != null) url += $"&after={Uri.EscapeDataString(after)}";

            // The member-month build is premium-billing's own pipeline: the
            // finance caller need not hold enrollment:read.
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
            request.Headers.Add(TenantMiddleware.TenantHeaderName, tenantId);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"enrollment-import-service answered {(int)response.StatusCode} {response.StatusCode} for the rate cell inputs",
                    null, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<Page>(Json, ct)
                       ?? throw new InvalidOperationException("enrollment-import-service returned an empty rate cell inputs page");
            foreach (var m in body.Members)
                result[m.MemberId] = m.Snapshots.Select(ToSnapshot).ToList();

            if (string.IsNullOrEmpty(body.Next)) break;
            if (after != null && string.CompareOrdinal(body.Next, after) <= 0)
                throw new InvalidOperationException("enrollment-import-service did not advance the rate cell inputs page");
            after = body.Next;
        }
        return result;
    }

    internal static RateCellInputSnapshot ToSnapshot(SnapshotWire s) => new(
        DateOnly.FromDateTime((s.EventDate ?? s.OccurredAt).Date),
        s.Version,
        s.Inputs.ReportingCategories.Select(c => (c.Name, c.Value)).ToList(),
        s.Inputs.CoverageLines.Select(l => l.PlanCoverageDescription).OfType<string>().ToList(),
        ExpectedMemberMonthBuilder.X12Date(s.Inputs.DateOfBirth),
        s.Inputs.CountyCode);

    internal sealed class Page
    {
        public List<MemberWire> Members { get; set; } = new();
        public string? Next { get; set; }
    }

    internal sealed class MemberWire
    {
        public string MemberId { get; set; } = string.Empty;
        public List<SnapshotWire> Snapshots { get; set; } = new();
    }

    internal sealed class SnapshotWire
    {
        public int Version { get; set; }
        public DateTime OccurredAt { get; set; }
        public DateTime? EventDate { get; set; }
        public InputsWire Inputs { get; set; } = new();
    }

    internal sealed class InputsWire
    {
        public List<CategoryWire> ReportingCategories { get; set; } = new();
        public List<LineWire> CoverageLines { get; set; } = new();
        public string? DateOfBirth { get; set; }
        public string? CountyCode { get; set; }
    }

    internal sealed class CategoryWire
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
    }

    internal sealed class LineWire
    {
        public string? PlanCoverageDescription { get; set; }
    }
}
