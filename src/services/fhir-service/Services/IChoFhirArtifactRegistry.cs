using Hl7.Fhir.Model;

namespace FhirService.Services;

/// <summary>
/// Read-only registry of Cloud Health Office-authored FHIR conformance
/// artifacts (StructureDefinitions, CodeSystems, ValueSets, OperationDefinitions).
/// Loaded once at startup from embedded resources; no runtime filesystem
/// dependency.
/// </summary>
public interface IChoFhirArtifactRegistry
{
    StructureDefinition?  GetStructureDefinition(string id);
    CodeSystem?           GetCodeSystem(string id);
    ValueSet?             GetValueSet(string id);
    OperationDefinition?  GetOperationDefinition(string id);

    IReadOnlyList<StructureDefinition> AllStructureDefinitions { get; }
    IReadOnlyList<CodeSystem>          AllCodeSystems          { get; }
    IReadOnlyList<ValueSet>            AllValueSets            { get; }
    IReadOnlyList<OperationDefinition> AllOperationDefinitions { get; }
}
