using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// A Da Vinci DTR <c>$questionnaire-package</c> response.
///
/// A thin, typed view over the Parameters a DTR server returns, built on
/// <see cref="ParametersExtractor"/> and <see cref="PackageResourceIndex"/>. It
/// reports what the server sent and does not assume a package must contain a
/// Library, a ValueSet or anything else the implementation did not return: DTR
/// packages carry exactly the dependencies the questionnaire actually names, so
/// demanding more would fail a conformant server.
///
/// What it does check is internal consistency — that the questionnaire asked for
/// is present, and that whatever dependencies it names are resolvable inside the
/// package.
/// </summary>
public sealed class DtrQuestionnairePackage
{
    /// <summary>DTR IG canonicals, from the IG the pinned implementation installs.</summary>
    public const string OutputParametersProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-qpackage-output-parameters";

    public const string PackageBundleProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/DTR-QPackageBundle";

    public const string StandardQuestionnaireProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-std-questionnaire";

    public const string AdaptiveQuestionnaireProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaire-adapt";

    public const string QuestionnaireResponseProfile =
        "http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/dtr-questionnaireresponse";

    /// <summary>The operation canonical, as the DTR IG defines it.</summary>
    public const string OperationCanonical =
        "http://hl7.org/fhir/us/davinci-dtr/OperationDefinition/questionnaire-package";

    /// <summary>The output parameter carrying the package, per the DTR IG.</summary>
    public const string PackageBundleParameter = "packagebundle";

    private DtrQuestionnairePackage(Parameters parameters)
    {
        Parameters = parameters;
        PackageBundle = ParametersExtractor.Resource<Bundle>(parameters, PackageBundleParameter);
        Index = new PackageResourceIndex(PackageBundle);
        Outcomes = ParametersExtractor.Outcomes(parameters);
    }

    public Parameters Parameters { get; }

    /// <summary>The package Bundle, or null when the server sent none.</summary>
    public Bundle? PackageBundle { get; }

    /// <summary>Canonical index over the package contents.</summary>
    public PackageResourceIndex Index { get; }

    /// <summary>Any OperationOutcome the server attached, wherever it attached it.</summary>
    public IReadOnlyList<OperationOutcome> Outcomes { get; }

    public IReadOnlyList<Questionnaire> Questionnaires =>
        Index.Resources.OfType<Questionnaire>().ToList();

    public IReadOnlyList<QuestionnaireResponse> QuestionnaireResponses =>
        Index.Resources.OfType<QuestionnaireResponse>().ToList();

    /// <summary>Parses a DTR package response. Null when the payload is not Parameters.</summary>
    public static DtrQuestionnairePackage? From(Resource? resource) =>
        resource is Parameters parameters ? new DtrQuestionnairePackage(parameters) : null;

    /// <summary>The Questionnaire matching a canonical, honouring a version suffix.</summary>
    public Questionnaire? Questionnaire(string canonical) => Index.Resolve(canonical) as Questionnaire;

    /// <summary>
    /// True when the questionnaire declares the DTR adaptive profile, which would
    /// require <c>$next-question</c> to complete. Reported rather than assumed:
    /// a standard questionnaire is usable straight from the package.
    /// </summary>
    public static bool IsAdaptive(Questionnaire questionnaire) =>
        questionnaire.Meta?.Profile.Contains(AdaptiveQuestionnaireProfile) == true;

    /// <summary>
    /// Structural problems with the response as a DTR package. Empty means the
    /// package is well formed — it says nothing about whether the questionnaire's
    /// content is clinically right.
    /// </summary>
    public IReadOnlyList<string> ProtocolViolations()
    {
        var problems = new List<string>();

        if (PackageBundle is null)
        {
            problems.Add(
                $"no '{PackageBundleParameter}' parameter; response carried: " +
                $"[{string.Join(", ", ParametersExtractor.PartNames(Parameters))}]");
            return problems;
        }

        if (Questionnaires.Count == 0)
        {
            problems.Add("package bundle contains no Questionnaire");
        }

        foreach (var questionnaire in Questionnaires)
        {
            if (string.IsNullOrWhiteSpace(questionnaire.Url))
            {
                problems.Add($"Questionnaire/{questionnaire.Id ?? "(no id)"} has no canonical url");
            }

            if (questionnaire.Status is null)
            {
                problems.Add($"Questionnaire {questionnaire.Url ?? questionnaire.Id} has no status");
            }
        }

        problems.AddRange(Index.DuplicateCanonicals
            .Select(canonical => $"package defines canonical '{canonical}' more than once"));

        foreach (var questionnaire in Questionnaires)
        {
            var dependencies = PackageResourceIndex.QuestionnaireDependencies(questionnaire);
            problems.AddRange(Index.UnresolvedReferences(dependencies)
                .Select(reference =>
                    $"Questionnaire {questionnaire.Url} depends on '{reference}', which the package does not contain"));
        }

        return problems;
    }

    /// <summary>
    /// A PHI-free inventory of what came back: resource counts and canonicals, no
    /// questionnaire item text, no answers, no patient detail.
    /// </summary>
    public string SafeSummary()
    {
        var counts = Index.ResourceTypeCounts
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}={entry.Value}");

        var parts = new List<string> { $"resources: {string.Join(", ", counts)}" };

        if (Questionnaires.Count > 0)
        {
            parts.Add("questionnaires: " + string.Join(", ", Questionnaires.Select(q =>
                q.Version is null ? q.Url : $"{q.Url}|{q.Version}")));
        }

        var issues = Outcomes.SelectMany(ParametersExtractor.SummarizeIssues).ToList();
        if (issues.Count > 0)
        {
            parts.Add($"outcome issues: {issues.Count}");
        }

        return string.Join("; ", parts);
    }
}
