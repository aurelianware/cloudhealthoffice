using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using Hl7.Fhir.Model;

namespace FhirService.Services;

public interface IPasAutoAdjudicator
{
    /// <param name="claim">The PAS Claim being decided.</param>
    /// <param name="context">The request bundle. Read for clinical context only.</param>
    /// <param name="tenantId">
    /// The AUTHENTICATED tenant of the request (from the validated token). It
    /// selects the tenant's PA rule set. Never taken from the bundle: a
    /// <c>meta.tag</c> in the request body is caller-controlled (D-12).
    /// </param>
    /// <param name="basis">
    /// What the request was mapped to before deciding (line of business,
    /// providers): the same values the authorization record stores, so the
    /// record never states a basis the decision did not use.
    /// </param>
    /// <param name="timeoutMs">Time budget; the adjudicator pends when it runs out.</param>
    /// <param name="ct">Request cancellation.</param>
    Task<Models.PasDecisionResult> TryDecideAsync(
        Claim claim,
        Bundle context,
        string tenantId,
        PasDecisionBasis basis,
        int timeoutMs,
        CancellationToken ct = default);
}

/// <summary>
/// The mapped request values a decision is made on. Built once, by
/// <c>PasController</c> from <see cref="Pas.PasAuthorizationMapper"/>'s output,
/// and stored with the record; the adjudicator does not re-derive them from the
/// Claim. There is no "unknown" line of business here: a request whose line of
/// business cannot be determined is pended without being adjudicated.
/// </summary>
public sealed record PasDecisionBasis(
    PaLineOfBusiness LineOfBusiness,
    string? RequestingProviderNpi,
    string? ServicingProviderNpi)
{
    /// <summary>The first requested service date (the record's <c>RequestedServiceDateFrom</c>).</summary>
    public required DateOnly ServiceDate { get; init; }

    /// <summary>The requested services' codes, with their systems, as stored.</summary>
    public IReadOnlyList<PasProcedureCode> ProcedureCodes { get; init; } = [];

    public string? MemberId { get; init; }

    public DateOnly? PatientDateOfBirth { get; init; }

    /// <summary>The stored diagnosis codes (ICD-10-CM, else ICD-10), in order.</summary>
    public IReadOnlyList<string> DiagnosisCodes { get; init; } = [];

    /// <summary>The first requested service's place of service, as stored.</summary>
    public string? PlaceOfServiceCode { get; init; }

    /// <summary>The basis for a mapped record: the same values the record stores.</summary>
    public static PasDecisionBasis From(PaLineOfBusiness lineOfBusiness, Pas.PasAuthorizationRecordRequest record)
        => new(lineOfBusiness, record.RequestingProviderNPI, record.ServicingProviderNPI)
        {
            ServiceDate = DateOnly.FromDateTime(record.RequestedServiceDateFrom
                ?? throw new ArgumentException("A mapped record always has a requested service date.", nameof(record))),
            ProcedureCodes = record.RequestedServices
                .Select(s => new PasProcedureCode(s.ProductOrServiceSystem, s.ProcedureCode)).ToList(),
            MemberId = record.MemberId,
            PatientDateOfBirth = record.PatientDateOfBirth is { } dob ? DateOnly.FromDateTime(dob) : null,
            DiagnosisCodes = record.DiagnosisCodes.Select(d => d.Code).ToList(),
            PlaceOfServiceCode = record.RequestedServices.Select(s => s.PlaceOfServiceCode)
                .FirstOrDefault(p => !string.IsNullOrEmpty(p)),
        };
}

/// <summary>A requested service's code and the system it is in.</summary>
public sealed record PasProcedureCode(string? System, string Code);
