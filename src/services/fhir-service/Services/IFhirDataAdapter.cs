using Hl7.Fhir.Model;
using FhirService.Models;

namespace FhirService.Services;

/// <summary>
/// Abstraction over the CHO domain data layer. The registered implementation is
/// <see cref="ChoNative.RoutingFhirDataAdapter"/>, which sends each resource to the
/// source <c>FhirAdapters:Sources</c> binds (ADR 018): Patient and Coverage to
/// member-service and coverage-service via <see cref="ChoNative.ChoNativeFhirDataAdapter"/>
/// when bound to ChoNative; everything else, and the default, to <see cref="MockFhirDataAdapter"/>.
/// </summary>
public interface IFhirDataAdapter
{
    // ── Patient ──────────────────────────────────────────────────────────────
    Task<Patient?> GetPatientAsync(string id, string tenantId, CancellationToken ct = default);
    Task<(IReadOnlyList<Patient> Items, int Total)> SearchPatientsAsync(
        PatientSearchParams search, string tenantId, CancellationToken ct = default);

    // ── Coverage ──────────────────────────────────────────────────────────────
    Task<Coverage?> GetCoverageAsync(string id, string tenantId, CancellationToken ct = default);
    Task<(IReadOnlyList<Coverage> Items, int Total)> SearchCoverageAsync(
        CoverageSearchParams search, string tenantId, CancellationToken ct = default);

    // ── Encounter ─────────────────────────────────────────────────────────────
    Task<Encounter?> GetEncounterAsync(string id, string tenantId, CancellationToken ct = default);
    Task<(IReadOnlyList<Encounter> Items, int Total)> SearchEncountersAsync(
        EncounterSearchParams search, string tenantId, CancellationToken ct = default);

    // ── Claim ─────────────────────────────────────────────────────────────────
    Task<Claim?> GetClaimAsync(string id, string tenantId, CancellationToken ct = default);
    Task<(IReadOnlyList<Claim> Items, int Total)> SearchClaimsAsync(
        ClaimSearchParams search, string tenantId, CancellationToken ct = default);
}
