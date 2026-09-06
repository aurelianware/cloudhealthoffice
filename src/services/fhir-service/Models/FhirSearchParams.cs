using Microsoft.AspNetCore.Mvc;

namespace FhirService.Models;

/// <summary>
/// Common FHIR search parameters present on every resource type.
/// </summary>
public class FhirSearchParamsBase
{
    [FromQuery(Name = "_id")]
    public string? Id { get; set; }

    [FromQuery(Name = "_lastUpdated")]
    public string? LastUpdated { get; set; }

    /// <summary>Comma-separated include directives, e.g. Coverage:payor</summary>
    [FromQuery(Name = "_include")]
    public List<string> Include { get; set; } = [];

    /// <summary>Comma-separated reverse-include directives, e.g. Coverage:beneficiary</summary>
    [FromQuery(Name = "_revinclude")]
    public List<string> RevInclude { get; set; } = [];

    /// <summary>Page size (FHIR _count). Clamped to server max in the controller.</summary>
    [FromQuery(Name = "_count")]
    public int Count { get; set; } = 20;

    /// <summary>1-based page number. The server encodes this into the Bundle next/prev links.</summary>
    [FromQuery(Name = "_page")]
    public int Page { get; set; } = 1;
}

public class PatientSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "name")]
    public string? Name { get; set; }

    [FromQuery(Name = "family")]
    public string? Family { get; set; }

    [FromQuery(Name = "given")]
    public string? Given { get; set; }

    [FromQuery(Name = "birthdate")]
    public string? BirthDate { get; set; }

    [FromQuery(Name = "identifier")]
    public string? Identifier { get; set; }

    [FromQuery(Name = "gender")]
    public string? Gender { get; set; }
}

public class CoverageSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "beneficiary")]
    public string? Beneficiary { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    [FromQuery(Name = "type")]
    public string? Type { get; set; }
}

public class EobSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "created")]
    public string? Created { get; set; }

    [FromQuery(Name = "type")]
    public string? Type { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }
}

public class EncounterSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "date")]
    public string? Date { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    [FromQuery(Name = "type")]
    public string? Type { get; set; }
}

public class ClaimSearchParams : FhirSearchParamsBase
{
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    [FromQuery(Name = "created")]
    public string? Created { get; set; }

    [FromQuery(Name = "status")]
    public string? Status { get; set; }

    [FromQuery(Name = "use")]
    public string? Use { get; set; }
}

/// <summary>
/// Search parameters for the USCDI clinical resource types (PAT-02).
///
/// Exactly what <c>ClinicalResourceController</c> honours and exactly what the
/// CapabilityStatement advertises — <c>_id</c>, <c>patient</c>, and
/// <c>subject</c> where FHIR R4 defines it — plus the shared paging parameters.
/// Nothing aspirational: a <c>category</c> or <c>code</c> field here that no
/// query applied would be a promise the server does not keep. See
/// docs/architecture/clinical-fhir.md for the limitation and why it is stated
/// rather than papered over.
/// </summary>
public class ClinicalSearchParams : FhirSearchParamsBase
{
    /// <summary>FHIR <c>patient</c> — accepts <c>Patient/123</c> or <c>123</c>.</summary>
    [FromQuery(Name = "patient")]
    public string? Patient { get; set; }

    /// <summary>
    /// FHIR <c>subject</c>, for the types whose R4 definition has one.
    /// AllergyIntolerance, Device and Immunization define only <c>patient</c>, and
    /// a <c>subject</c> on those is refused rather than ignored.
    /// </summary>
    [FromQuery(Name = "subject")]
    public string? Subject { get; set; }
}
