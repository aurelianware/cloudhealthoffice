using System.Diagnostics;
using System.Net.Http.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using Microsoft.Extensions.Options;

namespace FhirService.Services;

public class CrdService : ICrdService
{
    private readonly HttpClient _terminologyClient;
    private readonly IPriorAuthRuleEngine _priorAuthRuleEngine;
    private readonly CrdCodeClassification _defaultClassification;
    private readonly ICrdClassificationStore _classificationStore;
    private readonly ILogger<CrdService> _logger;

    public CrdService(
        IHttpClientFactory httpClientFactory,
        IPriorAuthRuleEngine priorAuthRuleEngine,
        IOptions<CrdConfig> config,
        ICrdClassificationStore classificationStore,
        ILogger<CrdService> logger)
    {
        _terminologyClient = httpClientFactory.CreateClient("TerminologyService");
        _priorAuthRuleEngine = priorAuthRuleEngine;
        _defaultClassification = LoadFromConfig(config.Value);
        _classificationStore = classificationStore;
        _logger = logger;
    }

    public async Task<CrdEvaluationResult> EvaluateCoverageRequirementsAsync(
        CrdHookRequest request, string tenantId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Extract codes from draftOrders
        var codes = ExtractCodes(request);

        // 2. Translate SNOMED codes if needed
        var translated = await TranslateCodesAsync(codes, tenantId, ct);

        // 3. Evaluate each code against benefit configuration (O(1) HashSet lookup)
        var classification = GetClassification(tenantId);
        var cards = new List<CrdCard>();
        foreach (var tc in translated)
        {
            var effectiveCode = tc.EffectiveCode;
            var display = tc.TranslatedCoding?.Display ?? tc.OriginalCode.Display ?? effectiveCode;
            var ruleDecision = await EvaluatePriorAuthRuleAsync(
                request, tenantId, effectiveCode, ct);

            if (ruleDecision.IsPriorAuthRequired())
            {
                cards.Add(BuildAuthRequiredCard(display, ruleDecision));
            }
            else if (ruleDecision?.Outcome is PaDecisionOutcome.Approve)
            {
                cards.Add(BuildAutoApprovedCard(display, ruleDecision));
            }
            else if (classification.AuthRequiredCodes.Contains(effectiveCode))
            {
                cards.Add(BuildAuthRequiredCard(display));
            }
            else if (classification.DocumentationRequiredCodes.Contains(effectiveCode))
            {
                cards.Add(BuildDocumentationRequiredCard(display));
            }
            else if (classification.AutoApprovedCodes.Contains(effectiveCode))
            {
                cards.Add(BuildAutoApprovedCard(display));
            }
            else
            {
                cards.Add(BuildNoAuthRequiredCard(display));
            }
        }

        sw.Stop();
        return new CrdEvaluationResult
        {
            Cards = cards,
            CodesEvaluated = codes.Count,
            TranslationsPerformed = translated.Count(t => t.WasTranslated),
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<PaRuleDecision?> EvaluatePriorAuthRuleAsync(
        CrdHookRequest request,
        string tenantId,
        string procedureCode,
        CancellationToken ct)
    {
        try
        {
            return await _priorAuthRuleEngine.EvaluateAsync(new PaRuleContext
            {
                TenantId = tenantId,
                StateCode = "TX",
                Lob = PaLineOfBusiness.Medicaid,
                Program = "STAR",
                RequestingProviderNpi = ExtractPractitionerId(request),
                ServicingProviderNpi = ExtractPractitionerId(request),
                MemberId = request.Context?.PatientId ?? string.Empty,
                ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
                ProcedureCodes = [procedureCode],
                DiagnosisCodes = [],
                EstimatedCost = 0m,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Prior Auth Rule Engine unavailable for CRD code {Code}; falling back to CRD classification",
                SanitizeForLog(procedureCode));
            return null;
        }
    }

    private static string ExtractPractitionerId(CrdHookRequest request)
    {
        var userId = request.Context?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
            return string.Empty;

        var slash = userId.LastIndexOf('/');
        return slash >= 0 && slash < userId.Length - 1
            ? userId[(slash + 1)..]
            : userId;
    }

    // ── Classification cache ─────────────────────────────────────────────────

    public CrdCodeClassification GetClassification(string tenantId)
    {
        if (_classificationStore.TryGet(tenantId, out var tenantClassification) && tenantClassification is not null)
            return tenantClassification;
        return _defaultClassification;
    }

    public void SetClassification(string tenantId, CrdCodeClassification classification)
    {
        classification.LoadedAt = DateTimeOffset.UtcNow;
        _classificationStore.Set(tenantId, classification);
    }

    public CrdCodeClassification? GetClassificationOrNull(string tenantId)
        => _classificationStore.GetOrNull(tenantId);

    private static CrdCodeClassification LoadFromConfig(CrdConfig config) => new()
    {
        AuthRequiredCodes = new HashSet<string>(config.AuthRequiredCodes, StringComparer.Ordinal),
        AutoApprovedCodes = new HashSet<string>(config.AutoApprovedCodes, StringComparer.Ordinal),
        DocumentationRequiredCodes = new HashSet<string>(config.DocumentationRequiredCodes, StringComparer.Ordinal),
    };

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            buffer.Append(char.IsControl(ch) ? '_' : ch);
        }
        if (buffer.Length > 256) buffer.Length = 256;
        return buffer.ToString();
    }

    // ── Code extraction ──────────────────────────────────────────────────────

    internal static List<CrdCoding> ExtractCodes(CrdHookRequest request)
    {
        var codes = new List<CrdCoding>();
        var entries = request.Context?.DraftOrders?.Entry;
        if (entries == null) return codes;

        foreach (var entry in entries)
        {
            var resource = entry.Resource;
            if (resource == null) continue;

            // ServiceRequest → code
            var codeableConcept = resource.ResourceType switch
            {
                "ServiceRequest" => resource.Code,
                "MedicationRequest" => resource.MedicationCodeableConcept,
                _ => resource.Code,
            };

            if (codeableConcept?.Coding != null)
            {
                codes.AddRange(codeableConcept.Coding);
            }
        }

        return codes;
    }

    // ── Terminology Service translation ──────────────────────────────────────

    internal async Task<List<TranslatedCode>> TranslateCodesAsync(
        List<CrdCoding> codes, string tenantId, CancellationToken ct)
    {
        var snomedCodes = codes.Where(c => c.System == "http://snomed.info/sct").ToList();
        var nonSnomedCodes = codes.Where(c => c.System != "http://snomed.info/sct").ToList();

        var result = nonSnomedCodes.Select(c => new TranslatedCode(c)).ToList();

        if (snomedCodes.Count == 0)
            return result;

        try
        {
            var requests = snomedCodes.Select(c => new
            {
                system = c.System,
                code = c.Code,
                targetSystem = "http://hl7.org/fhir/sid/icd-10-cm",
                tenantId,
            }).ToList();

            var response = await _terminologyClient.PostAsJsonAsync(
                "fhir/ConceptMap/$batch-translate", requests, ct);

            if (response.IsSuccessStatusCode)
            {
                var translations = await response.Content
                    .ReadFromJsonAsync<List<TerminologyTranslationResult>>(cancellationToken: ct);

                if (translations != null)
                {
                    for (var i = 0; i < snomedCodes.Count && i < translations.Count; i++)
                    {
                        var t = translations[i];
                        if (t.Result && t.Matches is { Count: > 0 })
                        {
                            var match = t.Matches[0];
                            result.Add(new TranslatedCode(
                                snomedCodes[i],
                                new CrdCoding
                                {
                                    System = match.System ?? "http://hl7.org/fhir/sid/icd-10-cm",
                                    Code = match.Code ?? snomedCodes[i].Code,
                                    Display = match.Display,
                                }));
                        }
                        else
                        {
                            result.Add(new TranslatedCode(snomedCodes[i]));
                        }
                    }

                    // Add any remaining SNOMED codes that weren't covered by the response
                    for (var i = translations.Count; i < snomedCodes.Count; i++)
                        result.Add(new TranslatedCode(snomedCodes[i]));

                    return result;
                }
            }

            _logger.LogWarning(
                "Terminology Service returned {StatusCode} — falling back to raw SNOMED codes",
                response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Terminology Service unavailable — falling back to raw SNOMED codes");
        }

        // Graceful degradation: use raw SNOMED codes
        result.AddRange(snomedCodes.Select(c => new TranslatedCode(c)));
        return result;
    }

    // ── Card builders ────────────────────────────────────────────────────────

    private static CrdCard BuildAuthRequiredCard(
        string serviceDisplay,
        PaRuleDecision? decision = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Summary = $"Prior authorization required for {serviceDisplay}",
        Detail = decision is null
            ? "Coverage requires prior authorization. Documentation may be needed."
            : $"Coverage requires prior authorization. Rule: {decision.FiringRuleName}.",
        Indicator = "warning",
        Source = new CrdCardSource
        {
            Topic = new CrdCoding
            {
                System = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp",
                Code = "auth-required",
                Display = "Authorization Required",
            },
        },
    };

    private static CrdCard BuildDocumentationRequiredCard(string serviceDisplay) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Summary = $"Documentation required for {serviceDisplay}",
        Detail = "Additional clinical documentation is needed. Launch DTR to complete required forms.",
        Indicator = "warning",
        Source = new CrdCardSource
        {
            Topic = new CrdCoding
            {
                System = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp",
                Code = "doc-required",
                Display = "Documentation Required",
            },
        },
        Suggestions = new List<CrdCardSuggestion>
        {
            new()
            {
                Label = "Launch DTR to complete documentation",
                Uuid = Guid.NewGuid().ToString(),
                IsRecommended = true,
            },
        },
        Links = new List<CrdCardLink>
        {
            new()
            {
                Label = "Launch DTR Smart App",
                Url = "https://cloudhealthoffice.com/dtr/launch",
                Type = "smart",
            },
        },
    };

    private static CrdCard BuildAutoApprovedCard(
        string serviceDisplay,
        PaRuleDecision? decision = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Summary = $"No prior authorization needed for {serviceDisplay}",
        Detail = decision is null
            ? "This service is auto-approved and does not require prior authorization."
            : $"This service does not require prior authorization. Rule: {decision.FiringRuleName}.",
        Indicator = "info",
        Source = new CrdCardSource
        {
            Topic = new CrdCoding
            {
                System = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp",
                Code = "no-auth",
                Display = "No Authorization Required",
            },
        },
    };

    private static CrdCard BuildNoAuthRequiredCard(string serviceDisplay) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Summary = $"No prior authorization needed for {serviceDisplay}",
        Detail = "Based on current benefit configuration, prior authorization is not required.",
        Indicator = "info",
        Source = new CrdCardSource
        {
            Topic = new CrdCoding
            {
                System = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp",
                Code = "no-auth",
                Display = "No Authorization Required",
            },
        },
    };

    // ── Terminology Service response DTOs ────────────────────────────────────

    private class TerminologyTranslationResult
    {
        public bool Result { get; set; }
        public List<TerminologyMatch>? Matches { get; set; }
    }

    private class TerminologyMatch
    {
        public string? System { get; set; }
        public string? Code { get; set; }
        public string? Display { get; set; }
    }
}
