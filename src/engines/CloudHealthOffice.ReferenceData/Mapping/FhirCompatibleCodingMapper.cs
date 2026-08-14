using CloudHealthOffice.ReferenceData.Domain;

namespace CloudHealthOffice.ReferenceData.Mapping;

/// <summary>
/// SDK-independent FHIR wire shape. FHIR adapters may copy this shape into the
/// SDK version they own without introducing FHIR dependencies into the domain.
/// </summary>
public sealed record FhirCompatibleCoding(
    string? System,
    string Code,
    string? Version,
    string? Display);

public static class FhirCompatibleCodingMapper
{
    public static FhirCompatibleCoding Map(ChoCoding coding) =>
        new(coding.CodeSystemUri, coding.Code, coding.Version, coding.Display);
}
