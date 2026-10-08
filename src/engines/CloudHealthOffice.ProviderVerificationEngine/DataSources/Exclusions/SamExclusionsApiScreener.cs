namespace CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Live per-provider screening against the SAM.gov Exclusions API
/// (<c>{ApiBaseUrl}{ApiVersion}/exclusions</c>, default v4). Optional mode
/// (<c>Sam:Mode = Api</c>); the default is the daily extract because SAM keys
/// are rate-limited per account.
/// <para>
/// Any failure — transport error, timeout, 5xx after retries, rejected key,
/// unexpected payload, more results than <see cref="SamOptions.MaxPages"/>
/// allows — reports NOT screened, never clear. 429 and 5xx are retried with
/// exponential backoff (Retry-After wins). The API key travels only in the
/// request URL and is never logged; the HttpClient used here must have its
/// request loggers removed (see the service registration).
/// </para>
/// </summary>
public sealed class SamExclusionsApiScreener : IExclusionSource
{
    public const string HttpClientName = "SamGovExclusionsApi";
    public const string ModeName = "LiveApi";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ExclusionScreeningOptions _options;
    private readonly ILogger<SamExclusionsApiScreener> _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SamExclusionsApiScreener(
        IHttpClientFactory httpFactory,
        IOptions<ExclusionScreeningOptions> options,
        ILogger<SamExclusionsApiScreener> logger,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public ExclusionScreeningSource Source => ExclusionScreeningSource.SamGov;

    public async Task<ExclusionSourceOutcome> ScreenAsync(ProviderScreeningRequest request, CancellationToken ct)
    {
        var status = new ExclusionSourceScreening { Source = Source, Mode = ModeName };
        var sam = _options.Sam;
        if (string.IsNullOrWhiteSpace(sam.ApiKey))
        {
            status.Note = "SAM.gov API key not configured";
            return new ExclusionSourceOutcome { Status = status };
        }

        var queries = BuildQueries(request, sam);
        if (queries.Count == 0)
        {
            status.Note = "No NPI or name to search SAM.gov with";
            return new ExclusionSourceOutcome { Status = status };
        }

        var candidates = new List<ExclusionRecord>();
        foreach (var query in queries)
        {
            var fetched = await FetchAllAsync(query, ct).ConfigureAwait(false);
            if (fetched.Error is not null)
            {
                status.Note = fetched.Error;
                return new ExclusionSourceOutcome { Status = status };
            }
            candidates.AddRange(fetched.Records);
        }

        var outcome = ExclusionMatcher.Match(request, candidates, _time.GetUtcNow().UtcDateTime);
        status.WasScreened = true;
        status.DataAsOf = _time.GetUtcNow();
        return new ExclusionSourceOutcome { Status = status, IsExcluded = outcome.IsExcluded, Matches = outcome.Matches };
    }

    /// <summary>
    /// One query string per identifier: NPI, then the individual's name, then
    /// the organization's name. Parameter names come from
    /// <see cref="SamOptions"/> so the contract can be adjusted to the
    /// published API without a code change.
    /// </summary>
    internal static List<string> BuildQueries(ProviderScreeningRequest request, SamOptions sam)
    {
        static string P(string name, string value) => $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";

        var queries = new List<string>();
        var npi = ExclusionNameNormalizer.NormalizeNpi(request.Npi);
        if (npi is not null)
            queries.Add(P(sam.NpiParameter, npi));

        if (!string.IsNullOrWhiteSpace(request.LastName))
        {
            var first = request.FirstName?.Trim();
            var last = request.LastName.Trim();
            if (sam.NameSearchStyle == SamNameSearchStyle.NameParts)
            {
                queries.Add(string.IsNullOrEmpty(first)
                    ? P(sam.LastNameParameter, last)
                    : $"{P(sam.FirstNameParameter, first)}&{P(sam.LastNameParameter, last)}");
            }
            else
            {
                queries.Add(P(sam.ExclusionNameParameter, string.IsNullOrEmpty(first) ? last : $"{first} {last}"));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.OrganizationName))
        {
            var org = request.OrganizationName.Trim();
            queries.Add(sam.NameSearchStyle == SamNameSearchStyle.NameParts
                ? P(sam.EntityNameParameter, org)
                : P(sam.ExclusionNameParameter, org));
        }

        return queries;
    }

    /// <summary>Pagination parameters for page <paramref name="pageIndex"/> (0-based).</summary>
    internal static string PageQuery(SamOptions sam, int pageIndex, int pageSize)
    {
        var position = sam.PaginationStyle == SamPaginationStyle.StartLength ? pageIndex * pageSize : pageIndex;
        return $"{Uri.EscapeDataString(sam.PageParameter)}={position}&{Uri.EscapeDataString(sam.SizeParameter)}={pageSize}";
    }

    private async Task<(List<ExclusionRecord> Records, string? Error)> FetchAllAsync(string query, CancellationToken ct)
    {
        var sam = _options.Sam;
        var records = new List<ExclusionRecord>();
        var pageSize = Math.Clamp(sam.PageSize, 1, 100);
        var maxPages = Math.Max(1, sam.MaxPages);
        string? previousPageSignature = null;
        var fetched = 0;

        for (var page = 0; page < maxPages; page++)
        {
            var (response, error) = await GetPageAsync(query, page, pageSize, ct).ConfigureAwait(false);
            if (error is not null)
                return (records, error);

            var entities = response!.ExcludedEntity ?? [];
            var total = response.TotalRecords ?? entities.Count;

            // Every entity must map: silently dropping one we cannot read
            // (schema drift) could hide the very exclusion we were asked about.
            var mapped = entities.Select(MapEntity).ToList();
            if (mapped.Any(r => r is null))
                return (records, "SAM.gov returned an exclusion record in an unrecognized shape; not screened");

            if (entities.Count == 0)
            {
                // An empty page before the reported total was read is an
                // inconsistent (partial) response, not "no more results".
                return fetched >= total
                    ? (records, null)
                    : (records, $"SAM.gov returned an empty page after {fetched} of {total} results; not screened");
            }

            // A server that ignores the pagination parameters returns page 0
            // again; reading it twice would never reach the remaining results.
            var signature = string.Join("|", mapped.Select(r => $"{r!.Npi}/{r.LastName}/{r.FirstName}/{r.BusinessName}/{r.ExclusionDate:yyyyMMdd}"));
            if (signature == previousPageSignature)
                return (records, "SAM.gov repeated the same page (pagination not honoured); not screened");
            previousPageSignature = signature;

            records.AddRange(mapped.OfType<ExclusionRecord>());
            fetched += entities.Count;
            if (fetched >= total)
                return (records, null);
        }

        // More hits than we are allowed to page through: a partial read could
        // miss the provider, so it is not a screen.
        return (records, $"SAM.gov returned more results than {maxPages} page(s); not screened");
    }

    private async Task<(SamExclusionsResponse? Response, string? Error)> GetPageAsync(
        string query, int page, int pageSize, CancellationToken ct)
    {
        var sam = _options.Sam;
        var url = $"{sam.ApiBaseUrl.TrimEnd('/')}/{sam.ApiVersion.Trim('/')}/exclusions" +
                  $"?api_key={Uri.EscapeDataString(sam.ApiKey!)}&{query}&{PageQuery(sam, page, pageSize)}";
        var http = _httpFactory.CreateClient(HttpClientName);
        var attempts = Math.Max(0, sam.MaxRetries) + 1;
        string lastError = "SAM.gov request failed";

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            TimeSpan? retryAfter = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(sam.RequestTimeout);
            try
            {
                using var response = await http.GetAsync(url, timeout.Token).ConfigureAwait(false);
                var code = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        var body = await response.Content.ReadFromJsonSafeAsync(timeout.Token).ConfigureAwait(false);
                        if (body is null || (body.TotalRecords is null && body.ExcludedEntity is null))
                            return (null, "SAM.gov returned an unexpected response body; not screened");
                        return (body, null);
                    }
                    catch (JsonException)
                    {
                        return (null, "SAM.gov returned non-JSON content; not screened");
                    }
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning(
                        "SAM.gov rejected the configured API key (HTTP {StatusCode}). SAM keys expire every 90 days — " +
                        "rotate ProviderVerification:ExclusionScreening:Sam:ApiKey. Providers are reported NOT screened against SAM until fixed.",
                        code);
                    return (null, $"SAM.gov API key rejected (HTTP {code})");
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || code >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    lastError = $"SAM.gov unavailable (HTTP {code})";
                    retryAfter = ReadRetryAfter(response);
                    _logger.LogWarning("SAM.gov exclusions API returned HTTP {StatusCode} (attempt {Attempt}/{Attempts})", code, attempt, attempts);

                    // Honour Retry-After exactly; never retry earlier than the
                    // server asked. A wait beyond what a screening call can
                    // afford fails the source (not screened) instead.
                    if (retryAfter is { } wait && wait > sam.MaxServerRetryDelay)
                    {
                        _logger.LogWarning(
                            "SAM.gov asked to retry after {RetryAfter}, beyond MaxServerRetryDelay {Max}; reporting not screened",
                            wait, sam.MaxServerRetryDelay);
                        return (null, $"SAM.gov rate limited (retry after {wait.TotalSeconds:0}s); not screened");
                    }
                }
                else
                {
                    return (null, $"SAM.gov request failed (HTTP {code}); not screened");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = "SAM.gov request timed out";
                _logger.LogWarning("SAM.gov exclusions API timed out (attempt {Attempt}/{Attempts})", attempt, attempts);
            }
            catch (HttpRequestException ex)
            {
                lastError = "SAM.gov unreachable";
                // The exception message does not contain the request URL (or key).
                _logger.LogWarning("SAM.gov exclusions API unreachable (attempt {Attempt}/{Attempts}): {Error}", attempt, attempts, ex.Message);
            }

            if (attempt < attempts)
                await _delay(Backoff(attempt, retryAfter), ct).ConfigureAwait(false);
        }

        return (null, $"{lastError}; not screened");
    }

    /// <summary>
    /// A server Retry-After is used as-is (already checked against
    /// <see cref="SamOptions.MaxServerRetryDelay"/>); only the locally
    /// computed exponential backoff is capped by <see cref="SamOptions.MaxRetryDelay"/>.
    /// </summary>
    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } serverDelay)
            return serverDelay;

        var sam = _options.Sam;
        var delay = TimeSpan.FromMilliseconds(sam.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return delay > sam.MaxRetryDelay ? sam.MaxRetryDelay : delay;
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    internal static ExclusionRecord? MapEntity(SamExcludedEntity entity)
    {
        var id = entity.ExclusionIdentification;
        if (id is null)
            return null;

        var actions = entity.ExclusionActions?.ListOfActions ?? [];
        var statuses = actions.Select(a => a.RecordStatus).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        // Active unless every reported action is explicitly inactive.
        var isActive = statuses.Count == 0 || statuses.Any(s => !SamExtractCsvParser.IsInactiveStatus(s));
        var latest = actions.LastOrDefault();

        var classification = entity.ExclusionDetails?.ClassificationType;
        var isIndividual = string.Equals(classification, "Individual", StringComparison.OrdinalIgnoreCase);

        return new ExclusionRecord
        {
            Source = ExclusionScreeningSource.SamGov,
            Classification = classification,
            FirstName = id.FirstName,
            MiddleName = id.MiddleName,
            LastName = id.LastName,
            BusinessName = isIndividual && id.LastName is not null ? null : id.EntityName,
            Npi = id.Npi,
            UeiSam = id.UeiSam,
            CageCode = id.CageCode,
            ExclusionType = entity.ExclusionDetails?.ExclusionType,
            ExclusionProgram = entity.ExclusionDetails?.ExclusionProgram,
            ExcludingAgency = entity.ExclusionDetails?.ExcludingAgencyName ?? entity.ExclusionDetails?.ExcludingAgencyCode,
            ExclusionDate = ExclusionDates.Parse(latest?.ActivateDate),
            EndDate = ExclusionDates.Parse(latest?.TerminationDate),
            IsActive = isActive
        }.Normalize();
    }
}

internal static class SamHttpContentExtensions
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<SamExclusionsResponse?> ReadFromJsonSafeAsync(this HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<SamExclusionsResponse>(stream, Options, ct).ConfigureAwait(false);
    }
}

// ── SAM.gov Exclusions API v4 response DTOs ─────────────────────

internal sealed class SamExclusionsResponse
{
    [JsonPropertyName("totalRecords")] public int? TotalRecords { get; set; }
    [JsonPropertyName("excludedEntity")] public List<SamExcludedEntity>? ExcludedEntity { get; set; }
}

internal sealed class SamExcludedEntity
{
    [JsonPropertyName("exclusionDetails")] public SamExclusionDetails? ExclusionDetails { get; set; }
    [JsonPropertyName("exclusionIdentification")] public SamExclusionIdentification? ExclusionIdentification { get; set; }
    [JsonPropertyName("exclusionActions")] public SamExclusionActions? ExclusionActions { get; set; }
}

internal sealed class SamExclusionDetails
{
    [JsonPropertyName("classificationType")] public string? ClassificationType { get; set; }
    [JsonPropertyName("exclusionType")] public string? ExclusionType { get; set; }
    [JsonPropertyName("exclusionProgram")] public string? ExclusionProgram { get; set; }
    [JsonPropertyName("excludingAgencyCode")] public string? ExcludingAgencyCode { get; set; }
    [JsonPropertyName("excludingAgencyName")] public string? ExcludingAgencyName { get; set; }
}

internal sealed class SamExclusionIdentification
{
    [JsonPropertyName("ueiSAM")] public string? UeiSam { get; set; }
    [JsonPropertyName("cageCode")] public string? CageCode { get; set; }
    [JsonPropertyName("npi"), JsonConverter(typeof(StringOrNumberConverter))] public string? Npi { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("middleName")] public string? MiddleName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("entityName")] public string? EntityName { get; set; }
}

internal sealed class SamExclusionActions
{
    [JsonPropertyName("listOfActions")] public List<SamExclusionAction>? ListOfActions { get; set; }
}

internal sealed class SamExclusionAction
{
    [JsonPropertyName("activateDate")] public string? ActivateDate { get; set; }
    [JsonPropertyName("terminationDate")] public string? TerminationDate { get; set; }
    [JsonPropertyName("terminationType")] public string? TerminationType { get; set; }
    [JsonPropertyName("recordStatus")] public string? RecordStatus { get; set; }
}

/// <summary>Reads a JSON string or number as a string (identifiers are not always quoted).</summary>
internal sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for identifier")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
