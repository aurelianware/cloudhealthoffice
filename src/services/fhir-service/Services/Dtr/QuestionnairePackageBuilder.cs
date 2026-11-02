using Hl7.Fhir.Model;

namespace FhirService.Services.Dtr;

/// <summary>
/// One questionnaire chosen for a <c>$questionnaire-package</c> response, and the
/// orders it is to be completed for (they become the draft response's
/// <c>qr-context</c>).
/// </summary>
public sealed record SelectedQuestionnaire(Questionnaire Questionnaire, IReadOnlyList<Resource> Orders);

/// <summary>
/// Builds the DTR 2.2.0 <c>$questionnaire-package</c> output: a Parameters
/// (<c>dtr-qpackage-output-parameters</c>) with one <c>packagebundle</c> per
/// questionnaire and an <c>outcome</c> for anything the caller should know.
///
/// Each package Bundle (<c>DTR-QPackageBundle</c>) holds, in order:
/// <list type="number">
///   <item>the Questionnaire (dtrb-1, oper-12);</item>
///   <item>one draft, in-progress QuestionnaireResponse naming it, the member and
///         the Coverage and orders it is for (oper-18);</item>
///   <item>every Library it names through <c>cqf-library</c>, and recursively
///         every Library those depend on (oper-12), with CQL and ELM (oper-13);</item>
///   <item>every ValueSet the questionnaire or those Libraries reference
///         (oper-14), expanded when small enough to enumerate (oper-15).</item>
/// </list>
///
/// Pure: no store, no clock other than the one passed in, so the same inputs
/// always build the same package apart from generated ids.
/// </summary>
public sealed class QuestionnairePackageBuilder
{
    public const string OutputParametersProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-qpackage-output-parameters";

    public const string PackageBundleProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/DTR-QPackageBundle";

    public const string QuestionnaireResponseProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse";

    public const string QrCoverageExtension =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/qr-coverage";

    public const string QrContextExtension =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/qr-context";

    public const string IntendedUseExtension =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/intendedUse";

    public const string CqfLibraryExtension = "http://hl7.org/fhir/StructureDefinition/cqf-library";

    /// <summary>CRD's DocReason code system (the CRD "temp" code system).</summary>
    public const string CrdTempCodeSystem = "http://hl7.org/fhir/us/davinci-crd/CodeSystem/temp";

    /// <summary>
    /// Value sets with fewer entries than this are expanded in the package
    /// (oper-15: "expansions under 40 entries SHALL be expanded").
    /// </summary>
    public const int ExpansionThreshold = 40;

    /// <summary>The <c>qr-context</c> extension accepts only these order types.</summary>
    private static readonly HashSet<string> QrContextTypes = new(StringComparer.Ordinal)
    {
        "DeviceRequest", "MedicationRequest", "NutritionOrder", "ServiceRequest", "Encounter", "Appointment",
    };

    private readonly DtrContentLibrary _content;

    public QuestionnairePackageBuilder(DtrContentLibrary content) => _content = content;

    /// <summary>
    /// The package for each selection. A questionnaire whose package has not
    /// changed since <paramref name="changedSince"/> is left out. Problems that do
    /// not stop the operation (a dependency CHO cannot supply, an unexpandable
    /// value set) are added to <paramref name="issues"/>.
    /// </summary>
    public IReadOnlyList<Bundle> Build(
        IEnumerable<SelectedQuestionnaire> selections,
        Coverage coverage,
        DateTimeOffset now,
        DateTimeOffset? changedSince,
        List<OperationOutcome.IssueComponent> issues,
        string? fhirBaseUrl = null)
    {
        var bundles = new List<Bundle>();
        foreach (var selection in selections)
        {
            var adaptive = DtrContentLibrary.IsAdaptive(selection.Questionnaire);
            var questionnaire = adaptive
                ? AdaptiveSearchForm(selection.Questionnaire, fhirBaseUrl)
                : selection.Questionnaire;
            var canonical = Canonical(questionnaire);
            var (libraries, valueSets) = Dependencies(questionnaire, canonical, now, issues);

            if (changedSince is { } since && LastChanged(questionnaire, libraries, valueSets) is { } changed
                && changed <= since)
            {
                continue;
            }

            var bundle = new Bundle
            {
                Id = Guid.NewGuid().ToString("N"),
                Meta = new Meta { Profile = new[] { PackageBundleProfile } },
                Identifier = new Identifier("urn:ietf:rfc:3986", $"urn:uuid:{Guid.NewGuid()}"),
                Type = Bundle.BundleType.Collection,
                Timestamp = now,
            };
            Add(bundle, questionnaire);
            var draft = DraftResponse(canonical, coverage, selection.Orders, now);
            if (adaptive)
            {
                // spec-25: the response to an adaptive form references a
                // contained Questionnaire derived from it; $next-question fills it.
                draft.Meta.Profile = new[] { AdaptiveQuestionnaireEngine.AdaptiveResponseProfile };
                draft.Contained.Add(AdaptiveQuestionnaireEngine.EmptyContained(questionnaire, AdaptiveUrl(questionnaire)));
                draft.Questionnaire = $"#{AdaptiveQuestionnaireEngine.ContainedId}";
            }
            Add(bundle, draft);
            foreach (var library in libraries) Add(bundle, library);
            foreach (var valueSet in valueSets) Add(bundle, valueSet);
            bundles.Add(bundle);
        }
        return bundles;
    }

    /// <summary>Wraps the bundles and issues in the output Parameters.</summary>
    public static Parameters Wrap(IEnumerable<Bundle> bundles, IReadOnlyList<OperationOutcome.IssueComponent> issues)
    {
        var parameters = new Parameters { Meta = new Meta { Profile = new[] { OutputParametersProfile } } };
        foreach (var bundle in bundles)
            parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "packagebundle", Resource = bundle });
        if (issues.Count > 0)
        {
            parameters.Parameter.Add(new Parameters.ParameterComponent
            {
                Name = "outcome",
                Resource = new OperationOutcome { Issue = issues.ToList() },
            });
        }
        return parameters;
    }

    /// <summary>The questionnaire's version-specific canonical (oper-16).</summary>
    /// <summary>
    /// The published form of an adaptive questionnaire: no items (DTR
    /// AdaptiveQuestionnaire-Search), and a <c>questionnaireAdaptive</c> URL
    /// that is this server's own FHIR base, the base the client already holds a
    /// Backend Services token for (spec-24).
    /// </summary>
    private static Questionnaire AdaptiveSearchForm(Questionnaire stored, string? fhirBaseUrl)
    {
        var form = (Questionnaire)stored.DeepCopy();
        form.Item = new List<Questionnaire.ItemComponent>();
        form.Meta ??= new Meta();
        form.Meta.Profile = new[] { DtrContentLibrary.AdaptiveSearchProfile };
        if (!string.IsNullOrEmpty(fhirBaseUrl))
        {
            form.Extension.RemoveAll(e => e.Url == DtrContentLibrary.QuestionnaireAdaptiveExtension);
            form.Extension.Add(new Extension(DtrContentLibrary.QuestionnaireAdaptiveExtension,
                new FhirUrl(fhirBaseUrl.TrimEnd('/'))));
        }
        return form;
    }

    private static string AdaptiveUrl(Questionnaire form)
        => form.Extension.FirstOrDefault(e => e.Url == DtrContentLibrary.QuestionnaireAdaptiveExtension)?.Value switch
        {
            FhirUrl url => url.Value,
            FhirUri uri => uri.Value,
            _ => "",
        };

    public static string Canonical(Questionnaire questionnaire)
        => questionnaire.Version is { Length: > 0 } v ? $"{questionnaire.Url}|{v}" : questionnaire.Url;

    // ── Dependencies ─────────────────────────────────────────────────────────

    private (List<Library> Libraries, List<ValueSet> ValueSets) Dependencies(
        Questionnaire questionnaire, string canonical, DateTimeOffset now,
        List<OperationOutcome.IssueComponent> issues)
    {
        var libraries = new List<Library>();
        var valueSets = new List<ValueSet>();
        var seenLibraries = new HashSet<string>(StringComparer.Ordinal);
        var seenValueSets = new HashSet<string>(StringComparer.Ordinal);

        var pendingLibraries = new Queue<string>(questionnaire.Extension
            .Where(e => e.Url == CqfLibraryExtension)
            .Select(e => (e.Value as Canonical)?.Value)
            .OfType<string>());
        var valueSetRefs = new List<string>(AnswerValueSets(questionnaire.Item));

        while (pendingLibraries.TryDequeue(out var reference))
        {
            if (!seenLibraries.Add(reference)) continue;
            var library = _content.ResolveLibrary(reference);
            if (library is null)
            {
                issues.Add(Warning(OperationOutcome.IssueType.NotFound,
                    $"Questionnaire {canonical} depends on Library {reference}, which this server does not hold; " +
                    "the package does not include it."));
                continue;
            }
            // One version per Library name (oper-17 allows treating more as an error).
            if (libraries.Any(l => l.Name == library.Name)) continue;
            libraries.Add(library);

            foreach (var related in library.RelatedArtifact
                         .Where(r => r.Type == RelatedArtifact.RelatedArtifactType.DependsOn && r.Resource is not null))
            {
                if (related.Resource.Contains("/Library/", StringComparison.Ordinal))
                    pendingLibraries.Enqueue(related.Resource);
                else if (related.Resource.Contains("/ValueSet/", StringComparison.Ordinal))
                    valueSetRefs.Add(related.Resource);
            }
        }

        foreach (var reference in valueSetRefs)
        {
            if (!seenValueSets.Add(reference)) continue;
            var valueSet = _content.ResolveValueSet(reference);
            if (valueSet is null)
            {
                issues.Add(Warning(OperationOutcome.IssueType.NotFound,
                    $"Questionnaire {canonical} references ValueSet {reference}, which this server does not hold; " +
                    "the package does not include it."));
                continue;
            }
            Expand(valueSet, now, issues);
            valueSets.Add(valueSet);
        }

        return (libraries, valueSets);
    }

    private static IEnumerable<string> AnswerValueSets(IEnumerable<Questionnaire.ItemComponent> items)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.AnswerValueSet)) yield return item.AnswerValueSet;
            foreach (var nested in AnswerValueSets(item.Item)) yield return nested;
        }
    }

    /// <summary>
    /// Expands a value set whose compose is an explicit list of concepts. CHO's
    /// value sets are written that way; one that uses filters or imports is
    /// shipped unexpanded with a warning, because expanding it needs a
    /// terminology server.
    /// </summary>
    /// <summary>
    /// Expands <paramref name="valueSet"/> in place for <c>ValueSet/$expand</c>,
    /// whatever its size, keeping only concepts whose code or display contains
    /// <paramref name="filter"/> when one is given. False, with an issue, when
    /// the ValueSet is not an explicit concept list.
    /// </summary>
    public static bool TryExpand(
        ValueSet valueSet, DateTimeOffset now, string? filter, List<OperationOutcome.IssueComponent> issues)
    {
        var include = valueSet.Compose?.Include ?? new List<ValueSet.ConceptSetComponent>();
        if (!Enumerable(valueSet, include))
        {
            issues.Add(new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.TooCostly,
                Diagnostics = $"ValueSet {valueSet.Url}|{valueSet.Version} is not an explicit concept list; " +
                              "this server runs no terminology service to expand it.",
            });
            return false;
        }

        var contains = Contains(include)
            .Where(c => string.IsNullOrEmpty(filter)
                        || c.Code.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (c.Display?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
        valueSet.Expansion = new ValueSet.ExpansionComponent
        {
            Identifier = $"urn:uuid:{Guid.NewGuid()}",
            TimestampElement = new FhirDateTime(now),
            Total = contains.Count,
            Contains = contains,
        };
        if (!string.IsNullOrEmpty(filter))
        {
            valueSet.Expansion.Parameter.Add(new ValueSet.ParameterComponent
            {
                Name = "filter",
                Value = new FhirString(filter),
            });
        }
        return true;
    }

    private static bool Enumerable(ValueSet valueSet, List<ValueSet.ConceptSetComponent> include)
        => include.Count > 0
           && valueSet.Compose!.Exclude.Count == 0
           && include.All(i => i.Filter.Count == 0 && i.ValueSetElement.Count == 0 && i.Concept.Count > 0);

    private static IEnumerable<ValueSet.ContainsComponent> Contains(IEnumerable<ValueSet.ConceptSetComponent> include)
        => include.SelectMany(i => i.Concept.Select(c => new ValueSet.ContainsComponent
        {
            System = i.System,
            Version = i.Version,
            Code = c.Code,
            Display = c.Display,
        }));

    private static void Expand(ValueSet valueSet, DateTimeOffset now, List<OperationOutcome.IssueComponent> issues)
    {
        var include = valueSet.Compose?.Include ?? new List<ValueSet.ConceptSetComponent>();
        var enumerable = include.Count > 0
                         && valueSet.Compose!.Exclude.Count == 0
                         && include.All(i => i.Filter.Count == 0 && i.ValueSetElement.Count == 0 && i.Concept.Count > 0);
        if (!enumerable)
        {
            issues.Add(Warning(OperationOutcome.IssueType.TooCostly,
                $"ValueSet {valueSet.Url}|{valueSet.Version} is not an explicit concept list and is sent unexpanded."));
            return;
        }

        var contains = include
            .SelectMany(i => i.Concept.Select(c => new ValueSet.ContainsComponent
            {
                System = i.System,
                Version = i.Version,
                Code = c.Code,
                Display = c.Display,
            }))
            .ToList();
        if (contains.Count >= ExpansionThreshold) return;

        valueSet.Expansion = new ValueSet.ExpansionComponent
        {
            Identifier = $"urn:uuid:{Guid.NewGuid()}",
            TimestampElement = new FhirDateTime(now),
            Total = contains.Count,
            Contains = contains,
        };
    }

    private static DateTimeOffset? LastChanged(Questionnaire q, IEnumerable<Library> libraries, IEnumerable<ValueSet> valueSets)
        => new[] { q.Meta?.LastUpdated }
            .Concat(libraries.Select(l => l.Meta?.LastUpdated))
            .Concat(valueSets.Select(v => v.Meta?.LastUpdated))
            .Max();

    // ── Draft response ───────────────────────────────────────────────────────

    /// <summary>
    /// The initial QuestionnaireResponse (oper-18). Every reference in it points at
    /// a resource the caller sent, so it resolves on the DTR client's own FHIR
    /// endpoint (spec-139). Answers are left to the client's CQL.
    /// </summary>
    private static QuestionnaireResponse DraftResponse(
        string questionnaireCanonical, Coverage coverage, IEnumerable<Resource> orders, DateTimeOffset now)
    {
        var response = new QuestionnaireResponse
        {
            Id = $"draft-{Guid.NewGuid():N}",
            Meta = new Meta { Profile = new[] { QuestionnaireResponseProfile } },
            Questionnaire = questionnaireCanonical,
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
            Subject = (ResourceReference)coverage.Beneficiary.DeepCopy(),
            Authored = now.ToString("yyyy-MM-ddTHH:mm:ssK"),
        };

        response.Extension.Add(new Extension(QrCoverageExtension,
            new ResourceReference($"Coverage/{coverage.Id}")));

        foreach (var order in orders.Where(o => QrContextTypes.Contains(o.TypeName) && !string.IsNullOrEmpty(o.Id)))
        {
            response.Extension.Add(new Extension(QrContextExtension,
                new ResourceReference($"{order.TypeName}/{order.Id}")));
        }

        // The documentation supports a prior authorization request: CHO's DTR
        // forms are chosen by CRD's pa-needed / doc-needed determinations.
        response.Extension.Add(new Extension(IntendedUseExtension,
            new CodeableConcept(CrdTempCodeSystem, "withpa", "Include with prior authorization request")));

        return response;
    }

    private static void Add(Bundle bundle, Resource resource)
        => bundle.Entry.Add(new Bundle.EntryComponent
        {
            FullUrl = $"urn:uuid:{Guid.NewGuid()}",
            Resource = resource,
        });

    internal static OperationOutcome.IssueComponent Warning(OperationOutcome.IssueType code, string text)
        => new()
        {
            Severity = OperationOutcome.IssueSeverity.Warning,
            Code = code,
            Diagnostics = text,
        };

    internal static OperationOutcome.IssueComponent Information(string text)
        => new()
        {
            Severity = OperationOutcome.IssueSeverity.Information,
            Code = OperationOutcome.IssueType.Informational,
            Diagnostics = text,
        };
}
