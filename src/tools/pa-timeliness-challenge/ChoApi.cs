using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.Tools.PaTimelinessChallenge;

/// <summary>
/// The CHO endpoints a run uses, on authorization-service and fhir-service.
/// Every call carries the tenant header and the bearer token.
/// </summary>
public sealed class ChoApi
{
    private readonly HttpClient _http;
    private readonly Uri _authorizationUrl;
    private readonly Uri _fhirUrl;

    public ChoApi(HttpClient http, Uri authorizationUrl, Uri fhirUrl, string tenantId, string bearerToken)
    {
        _http = http;
        _authorizationUrl = authorizationUrl;
        _fhirUrl = fhirUrl;
        _http.DefaultRequestHeaders.Remove("X-Tenant-ID");
        _http.DefaultRequestHeaders.Add("X-Tenant-ID", tenantId);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
    }

    public sealed record Response(HttpStatusCode Status, JsonNode? Body, string Text)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    public Task<Response> ImportCsvAsync(string csv, string fileName, CancellationToken ct)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", fileName },
            { new StringContent(PaCorpusGenerator.SourceSystem), "sourceSystem" },
        };
        return SendAsync(HttpMethod.Post, Auth("api/v1/authorizations/import/csv"), form, ct);
    }

    public Task<Response> RecordFactAsync(string authorizationId, PaCorpusWriter.ReportingFact fact, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Post, Auth($"api/authorizations/cms-0057/{Uri.EscapeDataString(authorizationId)}/facts"), new JsonObject
        {
            ["eventId"] = fact.EventId,
            // authorization-service reads enums by name only (integers are refused).
            ["kind"] = fact.Kind,
            ["effectiveAt"] = fact.EffectiveAt.ToString("O", CultureInfo.InvariantCulture),
            ["outcome"] = fact.Outcome,
            ["originalDueAt"] = fact.OriginalDueAt?.ToString("O", CultureInfo.InvariantCulture),
            ["extendedDueAt"] = fact.ExtendedDueAt?.ToString("O", CultureInfo.InvariantCulture),
            ["sourceSystem"] = fact.SourceSystem,
            ["evidenceReference"] = fact.EvidenceReference,
        }, ct);

    public Task<Response> MetricsAsync(DateTimeOffset from, DateTimeOffset to, PaLineOfBusiness? lob, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Auth("api/authorizations/cms-0057/metrics"
            + $"?from={Q(from)}&to={Q(to)}" + (lob is { } l ? $"&lineOfBusiness={l}" : "")), null, ct);

    public Task<Response> DecisionTimelinessAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Auth($"api/authorizations/reports/um-decision-timeliness?from={Q(from)}&to={Q(to)}"), null, ct);

    public Task<Response> AnnualDraftAsync(int year, PaLineOfBusiness lob, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Auth($"api/authorizations/cms-0057/annual-draft?year={year}&timeZone=UTC&lineOfBusiness={lob}"), null, ct);

    public Task<Response> PasSubmitAsync(JsonObject bundle, CancellationToken ct)
    {
        var content = new StringContent(bundle.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/fhir+json");
        return SendAsync(HttpMethod.Post, new Uri(_fhirUrl, "fhir/r4/Claim/$submit"), content, ct);
    }

    public Task<Response> AuthorizationByNumberAsync(string number, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Auth($"api/authorizations/number/{Uri.EscapeDataString(number)}"), null, ct);

    public Task<Response> AuthorizationAsync(string id, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, Auth($"api/authorizations/{Uri.EscapeDataString(id)}"), null, ct);

    public Task<Response> ExtendAsync(string id, string reason, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Post, Auth($"api/authorizations/{Uri.EscapeDataString(id)}/decision-extension"), new JsonObject
        {
            ["basis"] = "PlanNeedsAdditionalInformation",
            ["reason"] = reason,
        }, ct);

    public Task<Response> DecideAsync(string id, PaCase c, CancellationToken ct)
    {
        var decision = c.Decision!;
        var lines = new JsonArray();
        foreach (var line in c.Lines)
        {
            lines.Add(new JsonObject
            {
                ["lineNumber"] = line.LineNumber,
                ["reviewDecision"] = line.Decision == PaOutcome.Denied ? "A3" : "A1",
                ["denialReasonCode"] = line.DenialReasonCode,
                ["denialReason"] = line.DenialReason,
            });
        }
        return SendJsonAsync(HttpMethod.Post, Auth($"api/authorizations/{Uri.EscapeDataString(id)}/response"), new JsonObject
        {
            ["controlNumber"] = c.CaseId,
            ["reviewDecision"] = decision.Outcome switch { PaOutcome.Denied => "A3", PaOutcome.Modified => "A2", _ => "A1" },
            ["denialReasonCode"] = decision.DenialReasonCode,
            ["denialReason"] = decision.DenialReason,
            ["reviewerName"] = "PA Timeliness Challenge reviewer (synthetic)",
            ["lineDecisions"] = lines,
        }, ct);
    }

    private Uri Auth(string path) => new(_authorizationUrl, path);

    private static string Q(DateTimeOffset value) =>
        Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private Task<Response> SendJsonAsync(HttpMethod method, Uri uri, JsonObject body, CancellationToken ct) =>
        SendAsync(method, uri, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

    private async Task<Response> SendAsync(HttpMethod method, Uri uri, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? body = null;
        if (text.Length > 0 && text.TrimStart() is ['{' or '[', ..])
        {
            try { body = JsonNode.Parse(text); }
            catch (JsonException) { }
        }
        return new Response(response.StatusCode, body, text);
    }
}
