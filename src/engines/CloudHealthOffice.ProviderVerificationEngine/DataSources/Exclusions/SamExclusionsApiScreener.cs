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

        var queries = BuildQueries(request);
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

    private static List<string> BuildQueries(ProviderScreeningRequest request)
    {
        var queries = new List<string>();
        var npi = ExclusionNameNormalizer.NormalizeNpi(request.Npi);
        if (npi is not null)
            queries.Add($"npi={npi}");

        if (!string.IsNullOrWhiteSpace(request.LastName))
        {
            var name = string.IsNullOrWhiteSpace(request.FirstName)
                ? request.LastName.Trim()
                : $"{request.FirstName.Trim()} {request.LastName.Trim()}";
            queries.Add($"exclusionName={Uri.EscapeDataString(name)}");
        }

        if (!string.IsNullOrWhiteSpace(request.OrganizationName))
            queries.Add($"exclusionName={Uri.EscapeDataString(request.OrganizationName.Trim())}");

        return queries;
    }

    private async Task<(List<ExclusionRecord> Records, string? Error)> FetchAllAsync(string query, CancellationToken ct)
    {
        var sam = _options.Sam;
        var records = new List<ExclusionRecord>();
        var pageSize = Math.Clamp(sam.PageSize, 1, 100);
        var maxPages = Math.Max(1, sam.MaxPages);

        for (var page = 0; page < maxPages; page++)
        {
            var (response, error) = await GetPageAsync(query, page, pageSize, ct).ConfigureAwait(false);
            if (error is not null)
                return (records, error);

            var entities = response!.ExcludedEntity ?? [];
            records.AddRange(entities.Select(MapEntity).OfType<ExclusionRecord>());

            var total = response.TotalRecords ?? entities.Count;
            if ((page + 1) * pageSize >= total || entities.Count == 0)
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
                  $"?api_key={Uri.EscapeDataString(sam.ApiKey!)}&{query}&page={page}&size={pageSize}";
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

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var sam = _options.Sam;
        var delay = retryAfter ?? TimeSpan.FromMilliseconds(sam.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
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
