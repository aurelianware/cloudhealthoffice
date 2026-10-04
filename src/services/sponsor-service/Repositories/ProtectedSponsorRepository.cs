using CloudHealthOffice.FieldProtection;
using SponsorService.Models;

namespace SponsorService.Repositories;

/// <summary>
/// Encrypts the sponsor's <see cref="BillingInfo.BillingAccountNumber"/> at
/// rest around the Mongo or Cosmos repository. Writes store it encrypted
/// (<c>enc:v1:...</c>); reads return it decrypted (responses mask it, see
/// MaskedBillingInfoJsonConverter). A value stored before encryption existed
/// (legacy plaintext) is read as is, logged without the number, and stored
/// encrypted by the next create or full update of that sponsor. The
/// status-only write touches no billing field and leaves the stored value as
/// it is.
/// </summary>
public sealed class ProtectedSponsorRepository : ISponsorRepository
{
    public static readonly EventId LegacyPlaintextEvent = new(4817, "SponsorBillingAccountNumberPlaintext");

    private readonly ISponsorRepository _inner;
    private readonly IFieldProtector _protector;
    private readonly ILogger<ProtectedSponsorRepository> _logger;

    public ProtectedSponsorRepository(ISponsorRepository inner, IFieldProtector protector, ILogger<ProtectedSponsorRepository> logger)
    {
        _inner = inner;
        _protector = protector;
        _logger = logger;
    }

    public async Task<Sponsor?> GetByIdAsync(string tenantId, string id)
        => Decrypt(await _inner.GetByIdAsync(tenantId, id));

    public async Task<Sponsor?> GetByGroupNumberAsync(string tenantId, string groupNumber)
        => Decrypt(await _inner.GetByGroupNumberAsync(tenantId, groupNumber));

    public async Task<(IEnumerable<Sponsor> Items, string? ContinuationToken, int TotalCount)> GetPagedAsync(
        string tenantId, SponsorStatus? status = null, bool activeOnly = false, LineOfBusiness? lineOfBusiness = null,
        int pageSize = 20, string? continuationToken = null)
    {
        var (items, token, total) = await _inner.GetPagedAsync(tenantId, status, activeOnly, lineOfBusiness, pageSize, continuationToken);
        var list = items.ToList();
        foreach (var sponsor in list) Decrypt(sponsor);
        return (list, token, total);
    }

    public Task<Sponsor> CreateAsync(Sponsor sponsor) => WriteAsync(sponsor, _inner.CreateAsync);

    public Task<Sponsor> UpdateAsync(Sponsor sponsor) => WriteAsync(sponsor, _inner.UpdateAsync);

    public Task<bool> UpdateStatusAsync(string tenantId, string id, SponsorStatusChange change)
        => _inner.UpdateStatusAsync(tenantId, id, change);

    public Task DeleteAsync(string tenantId, string id) => _inner.DeleteAsync(tenantId, id);

    public Task<bool> ExistsAsync(string tenantId, string groupNumber) => _inner.ExistsAsync(tenantId, groupNumber);

    public Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null) => _inner.GetCountAsync(tenantId, status);

    /// <summary>
    /// Stores the number encrypted; the caller's object keeps (and the returned
    /// object gets) the plaintext, so the response can show its last 4.
    /// </summary>
    private async Task<Sponsor> WriteAsync(Sponsor sponsor, Func<Sponsor, Task<Sponsor>> write)
    {
        var billing = sponsor.BillingInfo;
        var plaintext = billing?.BillingAccountNumber;
        if (billing != null) billing.BillingAccountNumber = _protector.Protect(plaintext);
        try
        {
            var written = await write(sponsor);
            return Decrypt(written, logLegacy: false)!;
        }
        finally
        {
            if (billing != null) billing.BillingAccountNumber = plaintext;
        }
    }

    private Sponsor? Decrypt(Sponsor? sponsor, bool logLegacy = true)
    {
        var billing = sponsor?.BillingInfo;
        if (billing == null || string.IsNullOrEmpty(billing.BillingAccountNumber)) return sponsor;

        if (!_protector.IsProtected(billing.BillingAccountNumber))
        {
            if (logLegacy)
            {
                _logger.LogWarning(LegacyPlaintextEvent,
                    "Sponsor {GroupNumber} in tenant {TenantId} has a billing account number stored before encryption; " +
                    "it is stored encrypted by the next create or update of this sponsor",
                    Sanitize(sponsor!.GroupNumber), Sanitize(sponsor.TenantId));
            }
            return sponsor;
        }

        billing.BillingAccountNumber = _protector.Unprotect(billing.BillingAccountNumber);
        return sponsor;
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
