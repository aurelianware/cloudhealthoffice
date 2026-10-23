using System.Diagnostics;
using System.Text.Json;
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
    private readonly ICrdContextResolver _contextResolver;
    private readonly ILogger<CrdService> _logger;

    public CrdService(
        IHttpClientFactory httpClientFactory,
        IPriorAuthRuleEngine priorAuthRuleEngine,
        IOptions<CrdConfig> config,
        ICrdClassificationStore classificationStore,
        ICrdContextResolver contextResolver,
        ILogger<CrdService> logger)
    {
        _terminologyClient = httpClientFactory.CreateClient("TerminologyService");
        _priorAuthRuleEngine = priorAuthRuleEngine;
        _defaultClassification = LoadFromConfig(config.Value);
        _classificationStore = classificationStore;
        _contextResolver = contextResolver;
        _logger = logger;
    }

    public async Task<CrdEvaluationResult> EvaluateCoverageRequirementsAsync(
        CrdHookRequest request, string tenantId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Extract codes from draftOrders, remembering which order carried each
        //    one. The code alone was enough while the answer was a card; a system
        //    action has to be attached to the resource it is about.
        var coded = ExtractCodedOrders(request);
        var codes = coded.Select(c => c.Coding).ToList();
        var entryOfCode = new Dictionary<CrdCoding, int>(ReferenceEqualityComparer.Instance);
        foreach (var (entryIndex, coding) in coded)
            entryOfCode[coding] = entryIndex;

        // 2. Translate SNOMED codes if needed
        var translated = await TranslateCodesAsync(codes, tenantId, ct);

        // 3. Resolve the member context ONCE, before any code is evaluated.
        //    It is the same member for every draft order in the hook, and a
        //    per-code lookup would read CHO's stores n times to get n identical
        //    answers.
        var context = await _contextResolver.ResolveAsync(
            request, tenantId, ExtractCodes(request).Select(c => c.Code).ToList(), ct);

        if (!context.IsResolved)
        {
            _logger.LogInformation(
                "CRD could not resolve a member context ({Failure}); the rule engine will not be called "
                + "and determinations come from the explicit classification alone",
                context.Failure);
        }

        // 4. Evaluate each code against benefit configuration (O(1) HashSet lookup)
        var classification = GetClassification(tenantId);
        var cards = new List<CrdCard>();
        var assertions = new List<(int EntryIndex, CrdDetermination Determination,
            CrdIndeterminateReason? Reason, CrdCoding Coding)>();

        foreach (var tc in translated)
        {
            var effectiveCode = tc.EffectiveCode;
            var display = tc.TranslatedCoding?.Display ?? tc.OriginalCode.Display ?? effectiveCode;
            var coding = tc.TranslatedCoding ?? tc.OriginalCode;
            var entryIndex = entryOfCode.TryGetValue(tc.OriginalCode, out var index) ? index : -1;

            try
            {
                var outcome = await EvaluateCodeAsync(
                    context, tenantId, classification, effectiveCode, display, ct);
                cards.Add(outcome.Card);
                assertions.Add((entryIndex, outcome.Determination, outcome.Reason, coding));
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
                assertions.Add((entryIndex, CrdDetermination.Indeterminate,
                    CrdIndeterminateReason.Technical, coding));
            }
        }

        // A draft order with nothing to classify — text only, or a
        // MedicationRequest by medicationReference — still gets an answer, and
        // the only honest one is UNKNOWN. Before, it got no card at all, which
        // an EHR shows exactly like "no requirements" (D-21).
        foreach (var (entryIndex, label) in UncodedOrderEntries(request))
        {
            cards.Add(BuildRequirementsUnknownCard($"{label} order (no coded service)"));
            assertions.Add((entryIndex, CrdDetermination.Indeterminate,
                CrdIndeterminateReason.NoCodedService, Coding: new CrdCoding()));
        }

        sw.Stop();
        return new CrdEvaluationResult
        {
            Cards = cards,
            SystemActions = BuildSystemActions(request, assertions),
            CodesEvaluated = codes.Count,
            TranslationsPerformed = translated.Count(t => t.WasTranslated),
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Turns the determinations into CDS Hooks system actions: the EHR's own
    /// draft order, with CHO's <c>coverage-information</c> extension added.
    ///
    /// <para>
    /// Returns nothing when the request's prefetch carries no active Coverage.
    /// The extension requires a Coverage reference, and CHO will not invent one:
    /// a fabricated reference inside a resource the EHR is being asked to store
    /// is worse than the card-only answer this replaces. The cards still carry
    /// the determination in that case, exactly as before.
    /// </para>
    /// </summary>
    private static List<CdsHooksSystemAction> BuildSystemActions(
        CrdHookRequest request,
        IReadOnlyList<(int EntryIndex, CrdDetermination Determination,
            CrdIndeterminateReason? Reason, CrdCoding Coding)> assertions)
    {
        var coverageReference = CrdPrefetch.ActiveCoverageReference(request);
        if (coverageReference is null)
        {
            return [];
        }

        var entries = request.Context?.DraftOrders?.Entry;
        if (entries is null)
        {
            return [];
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var actions = new List<CdsHooksSystemAction>();

        foreach (var group in assertions
                     .Where(a => a.EntryIndex >= 0 && a.EntryIndex < entries.Count)
                     .GroupBy(a => a.EntryIndex))
        {
            var raw = entries[group.Key].RawResource;
            if (raw is not { ValueKind: JsonValueKind.Object } resource)
            {
                continue;
            }

            var extensions = group.Select(assertion => new CrdCoverageInformation
            {
                CoverageReference = coverageReference,
                // Unique per assertion, as the extension requires. CHO does not
                // persist it yet, so it identifies the assertion within this
                // response and cannot be resolved later — a limitation recorded
                // in the gap matrix rather than papered over with a value that
                // looks durable.
                CoverageAssertionId = Guid.NewGuid().ToString(),
                Date = today,
                Determination = assertion.Determination,
                Reason = assertion.Reason,
                BillingCodeSystem = string.IsNullOrWhiteSpace(assertion.Coding.Code)
                    ? null
                    : assertion.Coding.System,
                BillingCode = string.IsNullOrWhiteSpace(assertion.Coding.Code) ? null : assertion.Coding.Code,
            }.ToExtension()).ToList();

            actions.Add(new CdsHooksSystemAction
            {
                Type = "update",
                Description = "Coverage requirements determined by Cloud Health Office",
                Resource = WithExtensions(resource, extensions),
            });
        }

        return actions;
    }

    /// <summary>
    /// The EHR's resource with the given extensions appended to its own, and
    /// nothing else changed.
    ///
    /// <para>
    /// Rebuilt from the raw JSON rather than from the parsed model, because the
    /// parsed model only knows the few fields CRD evaluation reads. A system
    /// action asks the EHR to store this resource; handing back a version that
    /// had quietly lost its subject or its requester would be a data-loss bug
    /// dressed as a conformance feature.
    /// </para>
    /// </summary>
    private static JsonElement WithExtensions(
        JsonElement resource, IReadOnlyList<Dictionary<string, object?>> extensions)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var property in resource.EnumerateObject())
            {
                if (property.NameEquals("extension"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WritePropertyName("extension");
            writer.WriteStartArray();

            if (resource.TryGetProperty("extension", out var existing)
                && existing.ValueKind == JsonValueKind.Array)
            {
                foreach (var extension in existing.EnumerateArray())
                {
                    extension.WriteTo(writer);
                }
            }

            foreach (var extension in extensions)
            {
                JsonSerializer.Serialize(writer, extension);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
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
        => UncodedOrderEntries(request).Select(u => u.Label).ToList();

    /// <summary>
    /// The same uncoded orders, with the index of the entry each came from — the
    /// system action has to be attached to the order it is about.
    /// </summary>
    internal static List<(int EntryIndex, string Label)> UncodedOrderEntries(CrdHookRequest request)
    {
        var uncoded = new List<(int, string)>();
        var entries = request.Context?.DraftOrders?.Entry;
        for (var index = 0; index < (entries?.Count ?? 0); index++)
        {
            var resource = entries![index].Resource;
            if (resource == null) continue;
            var concept = resource.ResourceType == "MedicationRequest"
                ? resource.MedicationCodeableConcept
                : resource.Code;
            if (concept?.Coding is not { Count: > 0 } || concept.Coding.All(c => string.IsNullOrWhiteSpace(c.Code)))
                uncoded.Add((index, KnownOrderTypes.TryGetValue(resource.ResourceType ?? string.Empty, out var known)
                    ? known
                    : "Draft"));
        }
        return uncoded;
    }

    /// <summary>One code's answer: the card an EHR displays and the determination behind it.</summary>
    private readonly record struct CrdCodeOutcome(
        CrdCard Card, CrdDetermination Determination, CrdIndeterminateReason? Reason);

    private async Task<CrdCodeOutcome> EvaluateCodeAsync(
        CrdContextResolution context, string tenantId, CrdCodeClassification classification,
        string effectiveCode, string display, CancellationToken ct)
    {
        // The engine is called only with a context read from the member CHO
        // actually holds. Before, it was called with a hard-coded
        // TX / Medicaid / STAR context for every member in every state, so
        // whatever that rule set said was reported as this member's coverage
        // determination (D-21, second part; G-05). An unresolved member means
        // no rule set applies — not that a different member's does.
        var ruleDecision = context.IsResolved
            ? await EvaluatePriorAuthRuleAsync(context.Context!, effectiveCode, ct)
            : null;

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
            return new(BuildAuthRequiredCard(display, ruleDecision),
                CrdDetermination.AuthorizationRequired, null);
        if (classification.AuthRequiredCodes.Contains(effectiveCode))
            return new(BuildAuthRequiredCard(display), CrdDetermination.AuthorizationRequired, null);
        if (classification.DocumentationRequiredCodes.Contains(effectiveCode))
            return new(BuildDocumentationRequiredCard(display),
                CrdDetermination.DocumentationRequired,
                CrdIndeterminateReason.DocumentationOnlyClassification);
        if (classification.AutoApprovedCodes.Contains(effectiveCode))
            return new(BuildAutoApprovedCard(display), CrdDetermination.NoAuthorizationRequired, null);

        // Unknown is not one case: the member could not be resolved, the engine
        // could not answer, or nothing classifies the code. The IG's reason
        // value set distinguishes the first two, and a provider deciding what
        // to do next needs to know which it is — "we cannot find your patient"
        // and "try again later" call for different actions.
        return new(
            BuildRequirementsUnknownCard(display),
            CrdDetermination.Indeterminate,
            context.Failure switch
            {
                CrdContextFailure.MemberNotFound => CrdIndeterminateReason.MemberNotFound,
                CrdContextFailure.NoActiveCoverage => CrdIndeterminateReason.NoActiveCoverage,
                CrdContextFailure.ContextIncomplete => CrdIndeterminateReason.CoverageContextIncomplete,
                CrdContextFailure.Unavailable => CrdIndeterminateReason.Technical,
                _ => ruleDecision is null
                    ? CrdIndeterminateReason.Technical
                    : CrdIndeterminateReason.Unclassified,
            });
    }

    private async Task<PaRuleDecision?> EvaluatePriorAuthRuleAsync(
        PaRuleContext context,
        string procedureCode,
        CancellationToken ct)
    {
        try
        {
            // The resolved context carries every code in the hook, for rules that
            // look across an order set; this call is about one of them.
            return await _priorAuthRuleEngine.EvaluateAsync(
                context with { ProcedureCodes = [procedureCode] }, ct);
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
        => ExtractCodedOrders(request).Select(c => c.Coding).ToList();

    /// <summary>
    /// The same codes, each paired with the index of the draft order that
    /// carried it. A card names a service in prose; a system action has to name
    /// the resource, so the link back to the entry cannot be dropped on the way.
    /// </summary>
    internal static List<(int EntryIndex, CrdCoding Coding)> ExtractCodedOrders(CrdHookRequest request)
    {
        var codes = new List<(int, CrdCoding)>();
        var entries = request.Context?.DraftOrders?.Entry;
        if (entries == null) return codes;

        for (var index = 0; index < entries.Count; index++)
        {
            var resource = entries[index].Resource;
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
                codes.AddRange(codeableConcept.Coding
                    .Where(c => !string.IsNullOrWhiteSpace(c.Code))
                    .Select(c => (index, c)));
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
