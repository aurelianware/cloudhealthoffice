namespace CapitationService.Models;

/// <summary>
/// The provider contract fields each capitation rate config keeps a copy of
/// (see <see cref="CapitationRateConfig"/>). Sent by provider-contracts-service
/// when a contract changes; the contract is the source of truth.
/// </summary>
public sealed class ParentContractFields
{
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNpi { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; }

    /// <summary>Set by the receiving controller from the caller's token; a body value is ignored.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SyncedBy { get; set; }
}

/// <summary>How many rate configs a parent-field sync reached, and when.</summary>
public sealed record ParentFieldsSyncResult(long RateConfigsUpdated, DateTime SyncedAt);
