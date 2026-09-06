using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Reads named parts out of a FHIR <c>Parameters</c> response.
///
/// Protocol-neutral: FHIR operations answer with Parameters across CRD, DTR, PAS
/// and PDex, so this stays free of any one IG's vocabulary. It reports what is
/// present and never substitutes a default — an absent parameter means the server
/// did not send one, which a scenario must be able to distinguish from an empty
/// one.
/// </summary>
public static class ParametersExtractor
{
    /// <summary>The resource carried by the first part with this name, if any.</summary>
    public static Resource? Resource(Parameters? parameters, string name) =>
        parameters?.Parameter.FirstOrDefault(part => part.Name == name)?.Resource;

    /// <summary>The resource carried by the first part with this name, as <typeparamref name="T"/>.</summary>
    public static T? Resource<T>(Parameters? parameters, string name) where T : Resource =>
        Resource(parameters, name) as T;

    /// <summary>Every resource carried under this name, for repeating parameters.</summary>
    public static IReadOnlyList<Resource> Resources(Parameters? parameters, string name) =>
        parameters?.Parameter
            .Where(part => part.Name == name && part.Resource is not null)
            .Select(part => part.Resource!)
            .ToList()
        ?? (IReadOnlyList<Resource>)Array.Empty<Resource>();

    /// <summary>The names of every part present, in order, for diagnostics.</summary>
    public static IReadOnlyList<string> PartNames(Parameters? parameters) =>
        parameters?.Parameter.Select(part => part.Name).ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    /// <summary>True when a part with this name is present, whatever it carries.</summary>
    public static bool Has(Parameters? parameters, string name) =>
        parameters?.Parameter.Any(part => part.Name == name) == true;

    /// <summary>
    /// Every OperationOutcome the response carries, under any part name. Servers
    /// differ on where they attach one, and an outcome is worth surfacing wherever
    /// it appears — it is how a server reports what it could not do.
    /// </summary>
    public static IReadOnlyList<OperationOutcome> Outcomes(Parameters? parameters) =>
        parameters?.Parameter
            .Select(part => part.Resource)
            .OfType<OperationOutcome>()
            .ToList()
        ?? (IReadOnlyList<OperationOutcome>)Array.Empty<OperationOutcome>();

    /// <summary>
    /// One-line summaries of an outcome's issues: severity, code and text. Safe for
    /// evidence — carries the server's diagnostic wording, not resource content.
    /// </summary>
    public static IReadOnlyList<string> SummarizeIssues(OperationOutcome? outcome) =>
        outcome?.Issue
            .Select(issue =>
                $"{issue.Severity}/{issue.Code}: {issue.Details?.Text ?? issue.Diagnostics ?? "(no detail)"}")
            .ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();
}
