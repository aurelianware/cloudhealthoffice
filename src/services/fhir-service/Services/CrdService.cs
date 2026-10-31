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
    private readonly IDtrService? _dtrService;
    private readonly ICrdAssertionStore? _assertionStore;
    private readonly string? _dtrLaunchUrl;
    private readonly TimeSpan _assertionRetention;

    public CrdService(
        IHttpClientFactory httpClientFactory,
        IPriorAuthRuleEngine priorAuthRuleEngine,
        IOptions<CrdConfig> config,
        ICrdClassificationStore classificationStore,
        ICrdContextResolver contextResolver,
        ILogger<CrdService> logger,
        IDtrService? dtrService = null,
        ICrdAssertionStore? assertionStore = null,
        IOptions<DtrConfig>? dtrConfig = null)
    {
        _terminologyClient = httpClientFactory.CreateClient("TerminologyService");
        _priorAuthRuleEngine = priorAuthRuleEngine;
        _defaultClassification = LoadFromConfig(config.Value);
        _classificationStore = classificationStore;
        _contextResolver = contextResolver;
        _logger = logger;
        // Optional so a host without DTR still answers CRD: then no form is
        // named, no assertion is kept and no app is offered, exactly as before.
        _dtrService = dtrService;
        _assertionStore = assertionStore;
        _dtrLaunchUrl = string.IsNullOrWhiteSpace(dtrConfig?.Value.DtrLaunchUrl)
            ? null
            : dtrConfig.Value.DtrLaunchUrl.Trim();
        _assertionRetention = TimeSpan.FromDays(Math.Max(1, config.Value.AssertionRetentionDays));
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
        var assertions = new List<CrdAssertionDraft>();

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
                assertions.Add(new(entryIndex, outcome.Determination, outcome.Reason, coding, display, cards.Count - 1));
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
                assertions.Add(new(entryIndex, CrdDetermination.Indeterminate,
                    CrdIndeterminateReason.Technical, coding, display, cards.Count - 1));
            }
        }

        // A draft order with nothing to classify — text only, or a
        // MedicationRequest by medicationReference — still gets an answer, and
        // the only honest one is UNKNOWN. Before, it got no card at all, which
        // an EHR shows exactly like "no requirements" (D-21).
        foreach (var (entryIndex, label) in UncodedOrderEntries(request))
        {
            cards.Add(BuildRequirementsUnknownCard($"{label} order (no coded service)"));
            assertions.Add(new(entryIndex, CrdDetermination.Indeterminate,
                CrdIndeterminateReason.NoCodedService, new CrdCoding(), label, cards.Count - 1));
        }

        // 5. Documentation-required answers name the DTR forms that collect the
        //    documentation (resp-38), and their cards say how to complete them.
        var questionnaires = await QuestionnairesForAsync(assertions, tenantId, ct);
        var (systemActions, issued) = BuildSystemActions(request, tenantId, assertions, questionnaires);
        await RememberAsync(issued, tenantId, ct);

        foreach (var assertion in assertions.Where(a => a.Determination == CrdDetermination.DocumentationRequired))
        {
            var forms = questionnaires.TryGetValue(assertion, out var q) ? q : [];
            var record = issued.FirstOrDefault(i => ReferenceEquals(i.Draft, assertion)).Record;
            cards[assertion.CardIndex] = BuildDocumentationRequiredCard(
                assertion.Display, forms, record, _dtrLaunchUrl);
        }

        sw.Stop();
        return new CrdEvaluationResult
        {
            Cards = cards,
            SystemActions = systemActions,
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
    private static (List<CdsHooksSystemAction> Actions,
        List<(CrdAssertionDraft Draft, CrdCoverageAssertion Record)> Issued) BuildSystemActions(
        CrdHookRequest request,
        string tenantId,
        IReadOnlyList<CrdAssertionDraft> assertions,
        IReadOnlyDictionary<CrdAssertionDraft, IReadOnlyList<string>> questionnaires)
    {
        var issued = new List<(CrdAssertionDraft, CrdCoverageAssertion)>();
        var coverageReference = CrdPrefetch.ActiveCoverageReference(request);
        if (coverageReference is null)
        {
            return ([], issued);
        }

        var entries = request.Context?.DraftOrders?.Entry;
        if (entries is null)
        {
            return ([], issued);
        }

        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
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

            var orderReference = OrderReference(resource);
            var extensions = new List<Dictionary<string, object?>>();
            foreach (var assertion in group)
            {
                var hasCode = !string.IsNullOrWhiteSpace(assertion.Coding.Code);
                var forms = questionnaires.TryGetValue(assertion, out var q) ? q : [];
                var info = new CrdCoverageInformation
                {
                    CoverageReference = coverageReference,
                    // Unique per assertion, and kept (ICrdAssertionStore), so a
                    // DTR client can quote it back as the $questionnaire-package
                    // context (DTR spec-107).
                    CoverageAssertionId = Guid.NewGuid().ToString(),
                    Date = today,
                    Determination = assertion.Determination,
                    Reason = assertion.Reason,
                    BillingCodeSystem = hasCode ? assertion.Coding.System : null,
                    BillingCode = hasCode ? assertion.Coding.Code : null,
                    Questionnaires = forms,
                };
                extensions.Add(info.ToExtension());
                issued.Add((assertion, new CrdCoverageAssertion
                {
                    TenantId = tenantId,
                    AssertionId = info.CoverageAssertionId,
                    CoverageReference = coverageReference,
                    OrderReference = orderReference,
                    BillingCodeSystem = info.BillingCodeSystem,
                    BillingCode = info.BillingCode,
                    Determination = assertion.Determination,
                    Questionnaires = info.Determination == CrdDetermination.DocumentationRequired ? forms : [],
                    AssertedAt = now,
                    ExpiresAt = now, // set when stored, from the configured retention
                }));
            }

            actions.Add(new CdsHooksSystemAction
            {
                Type = "update",
                Description = "Coverage requirements determined by Cloud Health Office",
                Resource = WithExtensions(resource, extensions),
            });
        }

        return (actions, issued);
    }

    /// <summary><c>{resourceType}/{id}</c> of the draft order, when it carries an id.</summary>
    private static string? OrderReference(JsonElement resource)
        => resource.TryGetProperty("resourceType", out var type) && type.ValueKind == JsonValueKind.String
           && resource.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(id.GetString())
            ? $"{type.GetString()}/{id.GetString()}"
            : null;

    /// <summary>
    /// The DTR forms for each documentation-required answer: the tenant's
    /// active questionnaires whose focus use context lists the evaluated code,
    /// the same rule <c>$questionnaire-package</c> applies to the order. A lookup
    /// failure names no form rather than failing the hook: the answer is still
    /// "documentation required", and the order still selects the form in DTR.
    /// </summary>
    private async Task<Dictionary<CrdAssertionDraft, IReadOnlyList<string>>> QuestionnairesForAsync(
        IReadOnlyList<CrdAssertionDraft> assertions, string tenantId, CancellationToken ct)
    {
        var result = new Dictionary<CrdAssertionDraft, IReadOnlyList<string>>(ReferenceEqualityComparer.Instance);
        if (_dtrService is null)
        {
            return result;
        }

        foreach (var assertion in assertions.Where(a => a.Determination == CrdDetermination.DocumentationRequired))
        {
            if (string.IsNullOrWhiteSpace(assertion.Coding.Code) || string.IsNullOrWhiteSpace(assertion.Coding.System))
            {
                continue;
            }

            try
            {
                result[assertion] = await _dtrService.FindQuestionnaireCanonicalsAsync(
                    [new Hl7.Fhir.Model.Coding(assertion.Coding.System, assertion.Coding.Code)], tenantId, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CRD could not look up DTR questionnaires for code {Code}",
                    LogSanitizer.Sanitize(assertion.Coding.Code, 256));
            }
        }

        return result;
    }

    /// <summary>
    /// Keeps the assertions CHO just issued, so their ids resolve in DTR. A
    /// store failure is logged and the hook still answers: the determination is
    /// right either way, and the order and named canonicals still select the
    /// forms in DTR, only the context id would not resolve.
    /// </summary>
    private async Task RememberAsync(
        IReadOnlyList<(CrdAssertionDraft Draft, CrdCoverageAssertion Record)> issued,
        string tenantId, CancellationToken ct)
    {
        if (_assertionStore is null || issued.Count == 0)
        {
            return;
        }

        try
        {
            await _assertionStore.SaveAsync(
                issued.Select(i => i.Record with { ExpiresAt = i.Record.AssertedAt + _assertionRetention }).ToList(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CRD could not store {Count} coverage assertion(s) for tenant {TenantId}; their ids will not resolve in DTR",
                issued.Count, LogSanitizer.Sanitize(tenantId));
        }
    }

    /// <summary>One code's answer, before it becomes a system action and a stored assertion.</summary>
    private sealed record CrdAssertionDraft(
        int EntryIndex, CrdDetermination Determination, CrdIndeterminateReason? Reason,
        CrdCoding Coding, string Display, int CardIndex);

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
            return new(BuildDocumentationRequiredCard(display, [], null, null),
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

    /// <summary>
    /// The documentation-required card. CHO ships no DTR app (decision Q-6): the
    /// provider completes the form in the EHR's own DTR client, which CRD
    /// resp-40 requires the EHR to offer when coverage-information says
    /// doc-needed. So by default this is an instructions card, with no link and
    /// no suggestion, that names the forms.
    ///
    /// <para>
    /// When a tenant does run a DTR SMART app and configures its launch URL
    /// (<c>Cms0057:Dtr:DtrLaunchUrl</c>), the card becomes a launch-SMART card
    /// (CRDHooksResponse-launchSMART: a smart link and a suggestion with no
    /// actions). Its <c>appContext</c> carries what the app needs to call
    /// <c>$questionnaire-package</c>: the questionnaire canonicals, the
    /// <c>coverage-assertion-id</c> as <c>context</c> (DTR spec-107), and the
    /// order and Coverage references. The order and Coverage also reach the app
    /// through the EHR's SMART <c>fhirContext</c> (DTR spec-113); appContext is
    /// for what only CRD knows.
    /// </para>
    /// </summary>
    internal static CrdCard BuildDocumentationRequiredCard(
        string serviceDisplay,
        IReadOnlyList<string> questionnaires,
        CrdCoverageAssertion? assertion,
        string? dtrLaunchUrl)
    {
        var card = new CrdCard
        {
            Uuid = Guid.NewGuid().ToString(),
            Summary = $"Documentation required for {serviceDisplay}",
            Detail = "Additional clinical documentation is needed for this service. "
                     + "Complete the payer's documentation in your DTR application."
                     + (assertion is not null && questionnaires.Count > 0
                         ? " The order's coverage information names the form to use."
                         : string.Empty),
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
        };

        if (dtrLaunchUrl is not null)
        {
            card.Suggestions.Add(new CrdCardSuggestion
            {
                Label = "Launch DTR to complete documentation",
                Uuid = Guid.NewGuid().ToString(),
                IsRecommended = true,
            });
            card.Links.Add(new CrdCardLink
            {
                Label = "Complete documentation in DTR",
                Url = dtrLaunchUrl,
                Type = "smart",
                AppContext = DtrAppContext(questionnaires, assertion),
            });
        }

        return card;
    }

    /// <summary>
    /// The launch link's <c>appContext</c>: a JSON object, sent as the string
    /// CDS Hooks requires. Members are omitted when unknown.
    /// </summary>
    internal static string DtrAppContext(IReadOnlyList<string> questionnaires, CrdCoverageAssertion? assertion)
    {
        var context = new Dictionary<string, object>();
        if (questionnaires.Count > 0) context["questionnaire"] = questionnaires;
        if (assertion is not null)
        {
            context["context"] = assertion.AssertionId;
            context["coverage"] = assertion.CoverageReference;
            if (assertion.OrderReference is not null) context["order"] = assertion.OrderReference;
        }
        return JsonSerializer.Serialize(context);
    }

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
