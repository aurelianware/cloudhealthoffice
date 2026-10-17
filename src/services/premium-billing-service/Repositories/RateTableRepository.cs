using PremiumBillingService.Models;

namespace PremiumBillingService.Repositories;

/// <summary>
/// Stored rate table versions of the request's tenant. Versions are only ever
/// inserted, never replaced or deleted: an invoice line's
/// <c>(RateTableId, RateTableVersion)</c> must keep resolving to the rates it
/// was billed with.
/// </summary>
public interface IRateTableRepository
{
    /// <summary>Every version of every rate table of the tenant.</summary>
    Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync();

    /// <summary>Every version of one rate table, oldest first.</summary>
    Task<IReadOnlyList<RateTableRecord>> ListVersionsAsync(string rateTableId);

    Task<RateTableRecord?> GetVersionAsync(string rateTableId, int version);

    /// <summary>
    /// Inserts a version. Throws <see cref="RateTableVersionConflictException"/>
    /// when that version of the table already exists.
    /// </summary>
    Task<RateTableRecord> CreateVersionAsync(RateTableRecord record);
}

public sealed class RateTableVersionConflictException : Exception
{
    public RateTableVersionConflictException(string rateTableId, int version)
        : base($"Version {version} of rate table {rateTableId} already exists; re-read the table and retry")
    {
    }
}
