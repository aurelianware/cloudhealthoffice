using FhirService.Models;
using Hl7.Fhir.Model;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services.ChoNative;

/// <summary>
/// The registered <see cref="IFhirDataAdapter"/> (ADR 018): sends each resource
/// to the source <see cref="FhirDataBindings"/> names — CHO's own services for
/// Patient, Coverage, Encounter and Claim when configured, the synthetic mock
/// otherwise. The bindings are fixed at start-up; nothing chooses a source per
/// request.
/// </summary>
public sealed class RoutingFhirDataAdapter : IFhirDataAdapter
{
    private readonly FhirDataBindings _bindings;
    private readonly MockFhirDataAdapter _mock;
    private readonly ChoNativeFhirDataAdapter? _native;

    public RoutingFhirDataAdapter(FhirDataBindings bindings, MockFhirDataAdapter mock, ChoNativeFhirDataAdapter? native = null)
    {
        _bindings = bindings;
        _mock = mock;
        _native = native;
        if (bindings.AnyNative && native is null)
            throw new InvalidOperationException("A FHIR resource is bound to ChoNative but the CHO-native adapter is not registered.");
    }

    private bool Native(string source) => source == FhirAdapterSources.ChoNative;

    public Task<Patient?> GetPatientAsync(string id, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Patient) ? _native!.GetPatientAsync(id, tenantId, ct) : _mock.GetPatientAsync(id, tenantId, ct);

    public Task<(IReadOnlyList<Patient> Items, int Total)> SearchPatientsAsync(
        PatientSearchParams search, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Patient) ? _native!.SearchPatientsAsync(search, tenantId, ct) : _mock.SearchPatientsAsync(search, tenantId, ct);

    public Task<Coverage?> GetCoverageAsync(string id, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Coverage) ? _native!.GetCoverageAsync(id, tenantId, ct) : _mock.GetCoverageAsync(id, tenantId, ct);

    public Task<(IReadOnlyList<Coverage> Items, int Total)> SearchCoverageAsync(
        CoverageSearchParams search, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Coverage) ? _native!.SearchCoverageAsync(search, tenantId, ct) : _mock.SearchCoverageAsync(search, tenantId, ct);

    public Task<Encounter?> GetEncounterAsync(string id, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Encounter) ? _native!.GetEncounterAsync(id, tenantId, ct) : _mock.GetEncounterAsync(id, tenantId, ct);

    public Task<(IReadOnlyList<Encounter> Items, int Total)> SearchEncountersAsync(
        EncounterSearchParams search, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Encounter) ? _native!.SearchEncountersAsync(search, tenantId, ct) : _mock.SearchEncountersAsync(search, tenantId, ct);

    public Task<Claim?> GetClaimAsync(string id, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Claim) ? _native!.GetClaimAsync(id, tenantId, ct) : _mock.GetClaimAsync(id, tenantId, ct);

    public Task<(IReadOnlyList<Claim> Items, int Total)> SearchClaimsAsync(
        ClaimSearchParams search, string tenantId, CancellationToken ct = default)
        => Native(_bindings.Claim) ? _native!.SearchClaimsAsync(search, tenantId, ct) : _mock.SearchClaimsAsync(search, tenantId, ct);
}
