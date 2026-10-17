using System.Diagnostics;
using System.Net.Http.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using FhirService.Models;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Logging;

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
            try
            {
                cards.Add(await EvaluateCodeAsync(request, tenantId, classification, effectiveCode, display, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One code failing to evaluate is an UNKNOWN answer for that
                // code, never a missing card or a failed hook (D-21).
                _logger.LogWarning(ex, "CRD evaluation failed for code {Code}; answering unknown",
                    LogSanitizer.Sanitize(effectiveCode, 256));
                cards.Add(BuildRequirementsUnknownCard(display));
            }
        }

        // A draft order with nothing to classify — text only, or a
        // MedicationRequest by medicationReference — still gets an answer, and
        // the only honest one is UNKNOWN. Before, it got no card at all, which
        // an EHR shows exactly like "no requirements" (D-21).
        foreach (var uncoded in UncodedOrders(request))
            cards.Add(BuildRequirementsUnknownCard($"{uncoded} order (no coded service)"));

        sw.Stop();
        return new CrdEvaluationResult
        {
            Cards = cards,
            CodesEvaluated = codes.Count,
            TranslationsPerformed = translated.Count(t => t.WasTranslated),
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// CDS Hooks draft-order resource types a card may name. resourceType is
    /// caller-supplied, so anything else is labelled "Draft" rather than echoed
    /// into a card the EHR displays. Canonical spelling from this set.
    /// </summary>
    private static readonly HashSet<string> KnownOrderTypes = new(StringComparer.Ordinal)
    {
        "ServiceRequest", "MedicationRequest", "DeviceRequest", "NutritionOrder",
        "VisionPrescription", "CommunicationRequest", "Appointment",
    };

    /// <summary>Order types (known ones by name, others as "Draft") of draft orders with no code to evaluate.</summary>
    internal static List<string> UncodedOrders(CrdHookRequest request)
    {
        var uncoded = new List<string>();
        foreach (var entry in request.Context?.DraftOrders?.Entry ?? [])
        {
            var resource = entry.Resource;
            if (resource == null) continue;
            var concept = resource.ResourceType == "MedicationRequest"
                ? resource.MedicationCodeableConcept
                : resource.Code;
            if (concept?.Coding is not { Count: > 0 } || concept.Coding.All(c => string.IsNullOrWhiteSpace(c.Code)))
                uncoded.Add(KnownOrderTypes.TryGetValue(resource.ResourceType ?? string.Empty, out var known)
                    ? known
                    : "Draft");
        }
        return uncoded;
    }

    private async Task<CrdCard> EvaluateCodeAsync(
        CrdHookRequest request, string tenantId, CrdCodeClassification classification,
        string effectiveCode, string display, CancellationToken ct)
    {
        var ruleDecision = await EvaluatePriorAuthRuleAsync(
            request, tenantId, effectiveCode, ct);

        // Fail closed (D-21): "no prior authorization needed" only on an
        // explicit, positive determination. Today that is an explicit
        // classification listing the code as auto-approved: the tenant's
        // own classification, or — when the tenant has none — the
        // deployment-wide CrdConfig.AutoApprovedCodes. Both are an
        // administrator's explicit statement. Everything else that is not
        // a "required" answer is UNKNOWN:
        //   - a code nothing classifies;
        //   - the rule engine unavailable, no-op, erroring or timing out
        //     (EvaluatePriorAuthRuleAsync returns null, or a NoOp /
        //     NoRuleMatch / NoRulesConfigured / RuleError Pend);
        //   - an engine Approve. CRD calls the engine with a fixed
        //     TX / Medicaid / STAR context, no diagnoses, no member DOB,
        //     no provider taxonomy and cost 0, so an Approve here (e.g.
        //     DiagnosisRequired passing because no diagnosis was sent) is
        //     not a determination that PA is not required. Until CRD
        //     derives a real context (rest of D-21), it is not trusted.
        // A "required" answer from either source still wins: erring toward
        // requiring authorization is the safe direction.
        if (ruleDecision.IsPriorAuthRequired())
            return BuildAuthRequiredCard(display, ruleDecision);
        if (classification.AuthRequiredCodes.Contains(effectiveCode))
            return BuildAuthRequiredCard(display);
        if (classification.DocumentationRequiredCodes.Contains(effectiveCode))
            return BuildDocumentationRequiredCard(display);
        if (classification.AutoApprovedCodes.Contains(effectiveCode))
            return BuildAutoApprovedCard(display);
        return BuildRequirementsUnknownCard(display);
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
                LogSanitizer.Sanitize(procedureCode, 256));
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
                // A blank code classifies nothing; the order is answered once,
                // as uncoded (UncodedOrders), not also as an "unknown" blank code.
                codes.AddRange(codeableConcept.Coding.Where(c => !string.IsNullOrWhiteSpace(c.Code)));
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

            // terminology-service takes the tenant from the token. The call names the
            // tenant in X-Tenant-ID like every CHO-to-CHO call: a CHO caller's token is
            // forwarded (the header must agree with it), a SMART caller's is swapped
            // for fhir-service's service token for this tenant
            // (SmartCallerOutboundHandler), and with no caller the shared handler
            // mints that service token from this header.
            using var batchRequest = new HttpRequestMessage(HttpMethod.Post, "fhir/ConceptMap/$batch-translate")
            {
                Content = JsonContent.Create(requests),
            };
            batchRequest.Headers.Add(
                CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware.TenantHeaderName, tenantId);
            var response = await _terminologyClient.SendAsync(batchRequest, ct);

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

    /// <summary>
    /// The ONLY "not required" card: issued when the tenant's classification
    /// explicitly lists the code as auto-approved.
    /// </summary>
    private static CrdCard BuildAutoApprovedCard(string serviceDisplay) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Summary = $"No prior authorization needed for {serviceDisplay}",
        Detail = "This service is auto-approved and does not require prior authorization.",
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

    /// <summary>
    /// Fail-closed answer (D-21): CHO could not positively determine the
    /// coverage requirements. Never phrased as "not required".
    /// </summary>
    internal static CrdCard BuildRequirementsUnknownCard(string serviceDisplay) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Summary = $"Coverage requirements could not be determined for {serviceDisplay}",
        Detail = "Cloud Health Office could not determine whether prior authorization is required for this "
                 + "service. Contact the payer, or submit a prior authorization request for review, before "
                 + "proceeding. This is not a determination that prior authorization is not required.",
        Indicator = "warning",
        Source = new CrdCardSource
        {
            Topic = new CrdCoding
            {
                System = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp",
                Code = "info-needed",
                Display = "Coverage Requirements Unknown",
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
