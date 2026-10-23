using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Logging;

namespace EnrollmentImportService.Clients;

public sealed class SponsorServiceException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public class HttpSponsorServiceClient : ISponsorServiceClient
{
    public const string HttpClientName = "SponsorServiceWrite";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpSponsorServiceClient> _logger;

    public HttpSponsorServiceClient(IHttpClientFactory httpClientFactory, ILogger<HttpSponsorServiceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> ExistsAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sponsors/{Uri.EscapeDataString(groupNumber)}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<string?> GetLineOfBusinessAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/sponsors/{Uri.EscapeDataString(groupNumber)}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "lineOfBusiness", StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number => property.Value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
                System.Text.Json.JsonValueKind.String => property.Value.GetString(),
                _ => null,
            };
        }
        return null;
    }

    public async Task<SponsorIdentifier> ResolveIdentifierAsync(
        string tenantId, string value, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return new SponsorIdentifier(SponsorIdentifierKind.Unknown);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/sponsors/resolve-identifier?value={Uri.EscapeDataString(value)}");
        request.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        // 404 is an answer, not a failure: the value is on file as neither
        // number. 409 is the ambiguous answer. Everything else — a 403, a 500,
        // sponsor-service being down — must throw, because "could not look"
        // read as "there is none" would send the import down the path that
        // invents a sponsor.
        if (response.StatusCode != HttpStatusCode.NotFound
            && response.StatusCode != HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);
        var root = doc.RootElement;

        var kind = Text(root, "kind") switch
        {
            nameof(SponsorIdentifierKind.GroupNumber) => SponsorIdentifierKind.GroupNumber,
            nameof(SponsorIdentifierKind.PolicyNumber) => SponsorIdentifierKind.PolicyNumber,
            nameof(SponsorIdentifierKind.Ambiguous) => SponsorIdentifierKind.Ambiguous,
            _ => SponsorIdentifierKind.Unknown,
        };

        if (kind == SponsorIdentifierKind.Ambiguous)
        {
            var candidates = new List<string>();
            if (root.TryGetProperty("candidateGroupNumbers", out var listed)
                && listed.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var candidate in listed.EnumerateArray())
                {
                    if (candidate.GetString() is { Length: > 0 } text) candidates.Add(text);
                }
            }
            return new SponsorIdentifier(kind, CandidateGroupNumbers: candidates);
        }

        if (kind == SponsorIdentifierKind.Unknown) return new SponsorIdentifier(kind);

        // A recognised kind with no group number is a wire mismatch, not an
        // unknown value: failing loudly beats filing coverage against nothing.
        var groupNumber = Text(root, "groupNumber");
        if (string.IsNullOrEmpty(groupNumber))
        {
            throw new SponsorServiceException(
                $"sponsor-service resolved '{value}' as {kind} but returned no group number",
                response.StatusCode);
        }

        return new SponsorIdentifier(kind, groupNumber, Text(root, "policyNumber"));
    }

    private static string? Text(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var found)
        && found.ValueKind == System.Text.Json.JsonValueKind.String
            ? found.GetString()
            : null;

    public async Task CreateAsync(string tenantId, CreateSponsorRequestDto request, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/sponsors")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("X-Tenant-ID", tenantId);

        using var response = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);

        // A 409 here means another concurrent import already created this
        // sponsor (group number) between our existence check and this call —
        // not a real failure, the sponsor exists either way.
        if (response.StatusCode == HttpStatusCode.Conflict) return;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("sponsor-service rejected sponsor creation for {GroupNumber}: {Status} {Body}",
                LogSanitizer.Sanitize(request.GroupNumber), response.StatusCode, LogSanitizer.Sanitize(body));
            throw new SponsorServiceException(
                $"sponsor-service create failed for {request.GroupNumber}: {response.StatusCode}", response.StatusCode);
        }
    }
}
