using CloudHealthOffice.Infrastructure.ReferenceData;
using CHO.TerminologyService.Models;

namespace CHO.TerminologyService.Services.CodeSystemCatalog;

internal static class BuiltInIcd10CmCatalog
{
    public const string System = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string Source = "BuiltInIcd10CmCatalog";
    private const string Version = "mcc-seed-2026";

    public static IReadOnlyList<CodeSystemConcept> Concepts { get; } =
        SyntheticIcd10CmCatalog.Diagnoses
            .Select(diagnosis => Concept(diagnosis.Code, diagnosis.Display))
            .ToList();

    private static CodeSystemConcept Concept(string code, string display)
    {
        return new CodeSystemConcept
        {
            System = System,
            Code = code,
            Display = display,
            Version = Version,
            Source = Source
        };
    }
}
