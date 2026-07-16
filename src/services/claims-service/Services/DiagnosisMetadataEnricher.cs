using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.ReferenceData;
using Microsoft.Extensions.Caching.Memory;
using ClaimsService.Models;

namespace ClaimsService.Services;

public interface IClaimDiagnosisMetadataEnricher
{
    Task EnrichAsync(Claim claim, CancellationToken ct = default);
    Task EnrichAsync(IEnumerable<Claim> claims, CancellationToken ct = default);
}

public sealed class ClaimDiagnosisMetadataEnricher : IClaimDiagnosisMetadataEnricher
{
    private readonly IDiagnosisDescriptionLookup _descriptionLookup;

    public ClaimDiagnosisMetadataEnricher(IDiagnosisDescriptionLookup descriptionLookup)
    {
        _descriptionLookup = descriptionLookup;
    }

    public async Task EnrichAsync(IEnumerable<Claim> claims, CancellationToken ct = default)
    {
        var pendingDescriptions = new List<PendingDiagnosisDescription>();
        foreach (var claim in claims)
        {
            PrepareDiagnosisMetadata(claim, pendingDescriptions);
        }

        await PopulateDescriptionsAsync(pendingDescriptions, ct);
    }

    public async Task EnrichAsync(Claim claim, CancellationToken ct = default)
    {
        var pendingDescriptions = new List<PendingDiagnosisDescription>();
        PrepareDiagnosisMetadata(claim, pendingDescriptions);
        await PopulateDescriptionsAsync(pendingDescriptions, ct);
    }

    private static void PrepareDiagnosisMetadata(
        Claim claim,
        List<PendingDiagnosisDescription> pendingDescriptions)
    {
        for (var index = 0; index < claim.DiagnosisCodes.Count; index++)
        {
            var diagnosis = claim.DiagnosisCodes[index];
            if (diagnosis.PointerNumber <= 0)
            {
                diagnosis.PointerNumber = index + 1;
            }

            if (string.IsNullOrWhiteSpace(diagnosis.CodeQualifier))
            {
                diagnosis.CodeQualifier = diagnosis.PointerNumber <= 1 ? "ABK" : "ABF";
            }

            if (string.IsNullOrWhiteSpace(diagnosis.Description))
            {
                var normalizedCode = NormalizeCode(diagnosis.Code);
                if (normalizedCode is not null)
                {
                    pendingDescriptions.Add(new PendingDiagnosisDescription(diagnosis, normalizedCode));
                }
            }
        }
    }

    private async Task PopulateDescriptionsAsync(
        IReadOnlyCollection<PendingDiagnosisDescription> pendingDescriptions,
        CancellationToken ct)
    {
        if (pendingDescriptions.Count == 0)
        {
            return;
        }

        var lookups = pendingDescriptions
            .Select(x => x.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                code => code,
                code => _descriptionLookup.FindDescriptionAsync(code, ct),
                StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(lookups.Values);

        foreach (var pending in pendingDescriptions)
        {
            var description = await lookups[pending.Code];
            if (!string.IsNullOrWhiteSpace(description))
            {
                pending.Diagnosis.Description = description;
            }
        }
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToUpperInvariant();
    }

    private sealed record PendingDiagnosisDescription(DiagnosisCode Diagnosis, string Code);
}

public interface IDiagnosisDescriptionLookup
{
    Task<string?> FindDescriptionAsync(string? code, CancellationToken ct = default);
}

public sealed class DiagnosisDescriptionLookup : IDiagnosisDescriptionLookup
{
    private const string CacheKeyPrefix = "claims-service:diagnosis-description:";
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromMinutes(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiagnosisDescriptionLookup> _logger;

    public DiagnosisDescriptionLookup(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<DiagnosisDescriptionLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> FindDescriptionAsync(string? code, CancellationToken ct = default)
    {
        var normalizedCode = NormalizeCode(code);
        if (normalizedCode is null)
        {
            return null;
        }

        var cacheKey = CacheKeyPrefix + normalizedCode;
        if (_cache.TryGetValue<CachedDiagnosisDescription>(cacheKey, out var cached))
        {
            return cached?.Description;
        }

        var terminologyDescription = await TryFindTerminologyDescriptionAsync(normalizedCode, ct);
        if (!string.IsNullOrWhiteSpace(terminologyDescription))
        {
            Cache(cacheKey, terminologyDescription);
            return terminologyDescription;
        }

        if (SyntheticDiagnosisDescriptions.TryGetValue(normalizedCode, out var syntheticDescription))
        {
            Cache(cacheKey, syntheticDescription);
            return syntheticDescription;
        }

        var referenceDataDescription = await TryFindReferenceDataDescriptionAsync(normalizedCode, ct);
        Cache(cacheKey, referenceDataDescription);
        return referenceDataDescription;
    }

    private async Task<string?> TryFindTerminologyDescriptionAsync(string code, CancellationToken ct)
    {
        var timeoutMilliseconds = Math.Clamp(
            _configuration.GetValue<int?>("Services:TerminologyDisplayLookupTimeoutMilliseconds") ?? 750,
            100,
            3_000);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

            var client = _httpClientFactory.CreateClient(UpstreamClientNames.TerminologyService);
            var response = await client.GetFromJsonAsync<TerminologyCodeLookupResponse>(
                "fhir/CodeSystem/$lookup" +
                $"?system={Uri.EscapeDataString(Icd10CmSystem)}" +
                $"&code={Uri.EscapeDataString(code)}",
                timeout.Token);

            return response is { Result: true } && !string.IsNullOrWhiteSpace(response.Display)
                ? response.Display
                : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Terminology ICD-10 display lookup timed out for {Code}",
                SanitizeForLog(code));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Terminology ICD-10 display lookup failed for {Code}",
                SanitizeForLog(code));
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Terminology ICD-10 display lookup returned malformed JSON for {Code}",
                SanitizeForLog(code));
            return null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogDebug(
                ex,
                "Terminology ICD-10 display lookup returned an unsupported response for {Code}",
                SanitizeForLog(code));
            return null;
        }
    }

    private async Task<string?> TryFindReferenceDataDescriptionAsync(string code, CancellationToken ct)
    {
        var timeoutMilliseconds = Math.Clamp(
            _configuration.GetValue<int?>("Services:ReferenceDataDisplayLookupTimeoutMilliseconds") ?? 750,
            100,
            3_000);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

            var client = _httpClientFactory.CreateClient(UpstreamClientNames.ReferenceDataService);
            var response = await client.GetFromJsonAsync<ReferenceDataValidationResponse>(
                $"api/ReferenceData/icd10/{Uri.EscapeDataString(code)}/validate",
                timeout.Token);

            return string.IsNullOrWhiteSpace(response?.Description)
                ? null
                : response.Description;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(
                "Reference data ICD-10 display lookup timed out for {Code}",
                SanitizeForLog(code));
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Reference data ICD-10 display lookup failed for {Code}",
                SanitizeForLog(code));
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Reference data ICD-10 display lookup returned malformed JSON for {Code}",
                SanitizeForLog(code));
            return null;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogDebug(
                ex,
                "Reference data ICD-10 display lookup returned an unsupported response for {Code}",
                SanitizeForLog(code));
            return null;
        }
    }

    private void Cache(string cacheKey, string? description)
    {
        _cache.Set(
            cacheKey,
            new CachedDiagnosisDescription(description),
            description is null ? NegativeCacheDuration : CacheDuration);
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToUpperInvariant();
    }

    private static string SanitizeForLog(string value) =>
        value.Replace("\r", "").Replace("\n", "");

    private sealed record CachedDiagnosisDescription(string? Description);

    private sealed class TerminologyCodeLookupResponse
    {
        public bool Result { get; set; }
        public string? Display { get; set; }
    }

    private sealed class ReferenceDataValidationResponse
    {
        public string? Description { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, string> SyntheticDiagnosisDescriptions =
        SyntheticIcd10CmCatalog.Diagnoses.ToDictionary(
            diagnosis => diagnosis.Code,
            diagnosis => diagnosis.Display,
            StringComparer.OrdinalIgnoreCase);
}
