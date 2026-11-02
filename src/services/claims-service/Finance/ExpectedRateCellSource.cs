using System.Globalization;
using System.Net;
using CloudHealthOffice.Infrastructure.Security;

namespace ClaimsService.Finance;

/// <summary>The state's expected member-months (member, month, rate cell), as loaded in premium-billing-service.</summary>
public interface IExpectedRateCellSource
{
    /// <summary>Rows for months <paramref name="from"/> to <paramref name="to"/>, inclusive.</summary>
    Task<IReadOnlyList<ExpectedRateCell>> GetAsync(string tenantId, string state, DateOnly from, DateOnly to, CancellationToken ct = default);
}

public sealed class ExpectedRateCellsUnavailableException(string message) : Exception(message);

/// <summary>
/// Reads <c>GET api/v1/state-capitation/{state}/expected.csv</c> from
/// premium-billing-service with claims-service's own token.
/// </summary>
public sealed class HttpExpectedRateCellSource : IExpectedRateCellSource
{
    public const string HttpClientName = "PremiumBillingService";

    /// <summary>premium-billing-service caps a window at 36 months.</summary>
    public const int MaxMonthsPerCall = 36;

    private readonly IHttpClientFactory _factory;

    public HttpExpectedRateCellSource(IHttpClientFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<ExpectedRateCell>> GetAsync(string tenantId, string state, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = new List<ExpectedRateCell>();
        var client = _factory.CreateClient(HttpClientName);
        for (var start = from; start <= to; start = start.AddMonths(MaxMonthsPerCall))
        {
            var end = start.AddMonths(MaxMonthsPerCall - 1);
            if (end > to) end = to;
            var url = $"/api/v1/state-capitation/{Uri.EscapeDataString(state)}/expected.csv?from={start:yyyy-MM}&to={end:yyyy-MM}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url).UseChoServiceIdentity();
            request.Headers.Add("X-Tenant-ID", tenantId);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new ExpectedRateCellsUnavailableException("premium-billing-service could not be reached for the expected member-months");
            }
            using (response)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new ExpectedRateCellsUnavailableException($"premium-billing-service answered {(int)response.StatusCode} for the expected member-months");
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                rows.AddRange(Parse(reader));
            }
        }
        return rows;
    }

    /// <summary>member_id,month,rate_cell (as premium-billing writes it: quoted when needed, formula-guarded with a leading ').</summary>
    public static IEnumerable<ExpectedRateCell> Parse(TextReader reader)
    {
        if (reader.ReadLine() is null) yield break; // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var fields = SplitCsv(line);
            if (fields.Count < 3) continue;
            if (!DateOnly.TryParseExact(fields[1] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)) continue;
            yield return new ExpectedRateCell(Unguard(fields[0]), month, Unguard(fields[2]));
        }
    }

    private static string Unguard(string value) =>
        value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? value[1..] : value;

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
