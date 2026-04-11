using ClaimsExaminerService.Models;

namespace ClaimsExaminerService.Services;

/// <summary>
/// Fetches a provider's historical RFAI activity for a specific edit code.
/// V1 ships with a no-op default implementation that returns an empty list,
/// because rfai-service does not yet expose a provider/edit aggregate query.
/// The interface and the wiring exist now so plugging in a real implementation
/// later is a swap, not an architectural change — it's a "low-cost enrichment"
/// that costs nothing to scaffold and unlocks a useful prompt signal once the
/// data path is built.
///
/// To wire a real implementation:
///   1. Add an aggregate endpoint on rfai-service such as
///        GET /api/rfai/provider/{npi}/history?editCode={code}
///   2. Implement IProviderRfaiHistoryClient against that endpoint via an
///      injected HttpClient (the same pattern as ClaimsServiceClient).
///   3. Replace the NoOpProviderRfaiHistoryClient registration in Program.cs.
/// </summary>
public interface IProviderRfaiHistoryClient
{
    Task<ProviderRfaiHistory?> GetAsync(
        string providerNpi,
        string editCode,
        string tenantId,
        CancellationToken ct);
}

/// <summary>
/// Default v1 implementation: returns null for every lookup. The orchestrator
/// treats null as "no history available — neutral signal" and the prompt
/// builder simply omits the history section. This keeps the v1 prompt
/// deterministic regardless of whether rfai-service is reachable.
/// </summary>
public class NoOpProviderRfaiHistoryClient : IProviderRfaiHistoryClient
{
    public Task<ProviderRfaiHistory?> GetAsync(
        string providerNpi,
        string editCode,
        string tenantId,
        CancellationToken ct) => Task.FromResult<ProviderRfaiHistory?>(null);
}
