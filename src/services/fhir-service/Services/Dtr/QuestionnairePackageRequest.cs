using Hl7.Fhir.Model;

namespace FhirService.Services.Dtr;

/// <summary>
/// The inputs of DTR <c>Questionnaire/$questionnaire-package</c>, read from the
/// DTR 2.2.0 <c>dtr-qpackage-input-parameters</c> profile:
/// <list type="bullet">
///   <item><c>coverage</c> 1..1 — the member's Coverage (oper-6).</item>
///   <item><c>order</c> 0..* — the Request or Encounter/Appointment the documentation is for.</item>
///   <item><c>questionnaire</c> 0..* — canonicals, optionally version-specific.</item>
///   <item><c>context</c> 0..1 — the CRD/PAS context id.</item>
///   <item><c>changedsince</c> 0..1 — return only packages changed after this instant.</item>
/// </list>
/// At least one of order, questionnaire and context is required (invariant dtr-1).
/// </summary>
public sealed record QuestionnairePackageRequest(
    Coverage Coverage,
    IReadOnlyList<Resource> Orders,
    IReadOnlyList<string> Questionnaires,
    string? Context,
    DateTimeOffset? ChangedSince)
{
    /// <summary>The resource types the operation accepts as <c>order</c>.</summary>
    public static readonly IReadOnlySet<string> OrderTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Appointment", "Encounter", "CommunicationRequest", "DeviceRequest", "MedicationRequest",
        "NutritionOrder", "ServiceRequest", "SupplyRequest", "VisionPrescription",
    };

    /// <summary>
    /// Reads the request. Returns an error message, never a partial request, when
    /// the Parameters break the input profile: the caller answers it with 400.
    /// </summary>
    public static (QuestionnairePackageRequest? Request, string? Error) Parse(Parameters? parameters)
    {
        if (parameters?.Parameter is not { Count: > 0 } all)
            return (null, "The request body must be a Parameters resource (DTR dtr-qpackage-input-parameters).");

        var coverages = all.Where(p => p.Name == "coverage").ToList();
        if (coverages.Count != 1 || coverages[0].Resource is not Coverage coverage)
            return (null, "Exactly one 'coverage' parameter carrying a Coverage resource is required (DTR oper-6).");

        var orders = new List<Resource>();
        foreach (var p in all.Where(p => p.Name == "order"))
        {
            if (p.Resource is null || !OrderTypes.Contains(p.Resource.TypeName))
                return (null, "Each 'order' parameter must carry one of: " +
                              string.Join(", ", OrderTypes.Order(StringComparer.Ordinal)) + ".");
            orders.Add(p.Resource);
        }

        var questionnaires = new List<string>();
        foreach (var p in all.Where(p => p.Name == "questionnaire"))
        {
            // canonical per the profile; uri and string are accepted because
            // CHO's earlier operation took them, and they carry the same text.
            var value = p.Value switch
            {
                Canonical c => c.Value,
                FhirUri u => u.Value,
                FhirString s => s.Value,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(value))
                return (null, "Each 'questionnaire' parameter must carry a canonical.");
            questionnaires.Add(value.Trim());
        }

        string? context = null;
        var contexts = all.Where(p => p.Name == "context").ToList();
        if (contexts.Count > 1)
            return (null, "At most one 'context' parameter is allowed.");
        if (contexts.Count == 1)
        {
            context = (contexts[0].Value as FhirString)?.Value;
            if (string.IsNullOrWhiteSpace(context))
                return (null, "The 'context' parameter must carry a string.");
        }

        DateTimeOffset? changedSince = null;
        var since = all.Where(p => p.Name == "changedsince").ToList();
        if (since.Count > 1)
            return (null, "At most one 'changedsince' parameter is allowed.");
        if (since.Count == 1)
        {
            changedSince = since[0].Value switch
            {
                Instant i => i.Value,
                FhirDateTime d when d.TryToDateTimeOffset(out var dto) => dto,
                _ => null,
            };
            if (changedSince is null)
                return (null, "The 'changedsince' parameter must carry an instant.");
        }

        if (orders.Count == 0 && questionnaires.Count == 0 && context is null)
            return (null, "At least one of 'order', 'questionnaire' and 'context' is required (DTR dtr-1).");

        return (new QuestionnairePackageRequest(coverage, orders, questionnaires, context, changedSince), null);
    }
}

/// <summary>
/// The answer to <c>$questionnaire-package</c>: the output Parameters on
/// success, or the status and OperationOutcome to refuse with (spec-130:
/// unusable source data is a 4xx with an OperationOutcome).
/// </summary>
public sealed record QuestionnairePackageResult(Parameters? Package, int Status, OperationOutcome? Rejection)
{
    public static QuestionnairePackageResult Ok(Parameters package) => new(package, 200, null);

    public static QuestionnairePackageResult Reject(int status, OperationOutcome.IssueType code, string diagnostics)
        => new(null, status, new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = code,
                    Diagnostics = diagnostics,
                },
            ],
        });
}
