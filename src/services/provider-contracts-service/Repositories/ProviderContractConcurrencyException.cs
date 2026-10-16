using CloudHealthOffice.Infrastructure.Data;

namespace ProviderContractsService.Repositories;

/// <summary>
/// A provider contract write based on a stale read: another write changed the
/// contract (its <see cref="Models.ProviderContract.Revision"/>) since it was
/// read. Nothing was written; 409 Conflict at the API boundary. The caller
/// re-reads and retries.
/// </summary>
public sealed class ProviderContractConcurrencyException : ConcurrencyConflictException
{
    public ProviderContractConcurrencyException(string contractId, long expectedRevision)
        : base($"Provider contract {contractId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        ContractId = contractId;
        ExpectedRevision = expectedRevision;
    }

    public string ContractId { get; }
    public long ExpectedRevision { get; }
}
