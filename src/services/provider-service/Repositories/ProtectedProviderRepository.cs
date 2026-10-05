using CloudHealthOffice.FieldProtection;
using ProviderService.Models;
using ProviderService.Security;

namespace ProviderService.Repositories;

/// <summary>
/// Encrypts the legacy bank-account copy on provider documents and version
/// rows (<see cref="Provider.BankAccount"/>: routing, account and tax numbers)
/// at rest around the Mongo or Cosmos repository.
///
/// <list type="bullet">
///   <item>Every write of a whole row (create, update, drafts, activate and
///   supersede, state changes) stores the numbers encrypted
///   and bound to the row's tenant and provider (<c>enc:v2:...</c>). Without a key ring outside Development the write
///   fails (<see cref="FieldProtectionException"/>, answered 503) rather than
///   store plaintext. The caller's object keeps the plaintext.</item>
///   <item>Reads return the numbers decrypted. A value written before
///   encryption (legacy plaintext) is read as is and stored encrypted by the
///   next write of that row; rows never written again are re-encrypted by
///   <c>--encrypt-bank-accounts</c>.</item>
///   <item>Provider reads (claims, rosters, FHIR) do not need the numbers and
///   never show them (responses are masked), so a value that cannot be
///   decrypted does not fail the read: it is left encrypted on the object and
///   logged (event 4708, no number). Anything that needs the numbers refuses
///   a value still encrypted (the full read answers 503), and a later write
///   stores the ciphertext unchanged.</item>
/// </list>
/// The field-level writes (integrity, panel gating, credentialing projections)
/// touch no bank field and pass through.
/// </summary>
public sealed class ProtectedProviderRepository : IProviderRepository
{
    public static readonly EventId UndecryptableEvent = new(4708, "ProviderRowBankAccountUndecryptable");

    private readonly IProviderRepository _inner;
    private readonly IFieldProtector _protector;
    private readonly ILogger<ProtectedProviderRepository> _logger;

    public ProtectedProviderRepository(IProviderRepository inner, IFieldProtector protector, ILogger<ProtectedProviderRepository> logger)
    {
        _inner = inner;
        _protector = protector;
        _logger = logger;
    }

    // ── reads ──────────────────────────────────────────────────────────

    public async Task<Provider?> GetByIdAsync(string id) => Read(await _inner.GetByIdAsync(id));

    public async Task<Provider?> GetByNPIAsync(string npi) => Read(await _inner.GetByNPIAsync(npi));

    public async Task<IEnumerable<Provider>> SearchAsync(
        string? name, string? specialty, string? zipCode, string? state, string? planId,
        LineOfBusiness? lineOfBusiness, ProviderType? providerType, bool? acceptingNewPatients,
        int page, int pageSize, string? firstName = null, string? lastName = null, string? city = null)
        => ReadAll(await _inner.SearchAsync(name, specialty, zipCode, state, planId, lineOfBusiness, providerType,
            acceptingNewPatients, page, pageSize, firstName, lastName, city));

    public async Task<IReadOnlyList<Provider>> ListNetworkRosterAsync(
        NetworkRosterQuery query, NetworkRosterSort sort, int skip, CancellationToken ct = default)
        => ReadAll(await _inner.ListNetworkRosterAsync(query, sort, skip, ct));

    public async Task<Provider?> GetLatestActiveAsync(string providerId, DateTime asOf)
        => Read(await _inner.GetLatestActiveAsync(providerId, asOf));

    public async Task<Provider?> GetVersionAsync(string providerId, string versionId)
        => Read(await _inner.GetVersionAsync(providerId, versionId));

    public async Task<(IReadOnlyList<Provider> Items, string? ContinuationToken)> ListVersionsAsync(
        string providerId, int pageSize, string? continuationToken)
    {
        var (items, token) = await _inner.ListVersionsAsync(providerId, pageSize, continuationToken);
        return (ReadAll(items), token);
    }

    public async Task<IReadOnlyList<Provider>> ListProvidersForIntegrityRefreshAsync(
        string tenantId, DateTimeOffset dueBefore, bool includeNeverVerified, int skip, int pageSize, CancellationToken ct = default)
        => ReadAll(await _inner.ListProvidersForIntegrityRefreshAsync(tenantId, dueBefore, includeNeverVerified, skip, pageSize, ct));

    public async Task<IReadOnlyList<Provider>> ListProvidersForPanelGatingBackfillAsync(
        string tenantId, int skip, int pageSize, CancellationToken ct = default)
        => ReadAll(await _inner.ListProvidersForPanelGatingBackfillAsync(tenantId, skip, pageSize, ct));

    // ── whole-row writes ───────────────────────────────────────────────

    public Task<Provider> CreateAsync(Provider provider) => WriteAsync(provider, _inner.CreateAsync);

    public Task<Provider> UpdateAsync(Provider provider) => WriteAsync(provider, _inner.UpdateAsync);

    public Task<Provider> CreateDraftAsync(Provider draft) => WriteAsync(draft, _inner.CreateDraftAsync);

    public Task<Provider> UpdateDraftAsync(Provider draft) => WriteAsync(draft, _inner.UpdateDraftAsync);

    public Task<Provider> ReplaceVersionRowAsync(Provider version) => WriteAsync(version, _inner.ReplaceVersionRowAsync);

    public async Task<Provider> ActivateAndSupersedeAsync(Provider draftToActivate, Provider? predecessor)
    {
        var draftAccount = draftToActivate.BankAccount;
        var predecessorAccount = predecessor?.BankAccount;
        // Both encrypted before either row is written.
        var draftStored = ForStorage(draftToActivate, draftAccount);
        var predecessorStored = predecessor == null ? null : ForStorage(predecessor, predecessorAccount);
        draftToActivate.BankAccount = draftStored;
        if (predecessor != null) predecessor.BankAccount = predecessorStored;
        try
        {
            var written = await _inner.ActivateAndSupersedeAsync(draftToActivate, predecessor);
            return ReferenceEquals(written, draftToActivate) ? written : Read(written)!;
        }
        finally
        {
            draftToActivate.BankAccount = draftAccount;
            if (predecessor != null) predecessor.BankAccount = predecessorAccount;
        }
    }

    public Task DeleteAsync(string id) => _inner.DeleteAsync(id);

    // ── field-level writes and counts: no bank field ───────────────────

    public Task<bool> UpdateIntegrityProjectionAsync(string tenantId, string providerId, int? integrityScore, string? integrityRating,
        DateTimeOffset? lastVerifiedAt, DateTimeOffset? nextVerificationDue, CancellationToken ct = default)
        => _inner.UpdateIntegrityProjectionAsync(tenantId, providerId, integrityScore, integrityRating, lastVerifiedAt, nextVerificationDue, ct);

    public Task<IReadOnlyList<string>> ListProviderTenantIdsAsync(CancellationToken ct = default)
        => _inner.ListProviderTenantIdsAsync(ct);

    public Task<long> CountStaleProvidersAsync(string tenantId, DateTimeOffset staleBefore, CancellationToken ct = default)
        => _inner.CountStaleProvidersAsync(tenantId, staleBefore, ct);

    public Task<bool> UpdatePanelGatingDefaultsAsync(string tenantId, string providerId, int participationIndex,
        PanelGatingFields fields, CancellationToken ct = default)
        => _inner.UpdatePanelGatingDefaultsAsync(tenantId, providerId, participationIndex, fields, ct);

    public Task<bool> UpdateCredentialingProjectionAsync(string tenantId, string providerId, CredentialingStatus status,
        DateTime? credentialingDate, DateTime? recredentialingDueDate, CancellationToken ct = default)
        => _inner.UpdateCredentialingProjectionAsync(tenantId, providerId, status, credentialingDate, recredentialingDueDate, ct);

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Stores the row with its bank numbers encrypted; the caller's object
    /// keeps (and the returned object gets) the plaintext.
    /// </summary>
    private async Task<Provider> WriteAsync(Provider provider, Func<Provider, Task<Provider>> write)
    {
        var plaintext = provider.BankAccount;
        provider.BankAccount = ForStorage(provider, plaintext);
        try
        {
            var written = await write(provider);
            return ReferenceEquals(written, provider) ? written : Read(written)!;
        }
        finally
        {
            provider.BankAccount = plaintext;
        }
    }

    private List<Provider> ReadAll(IEnumerable<Provider> providers)
    {
        var list = providers.ToList();
        foreach (var provider in list) Read(provider);
        return list;
    }

    private Provider? Read(Provider? provider)
    {
        var account = provider?.BankAccount;
        if (account == null) return provider;

        try
        {
            ProviderBankAccountProtection.Unprotect(_protector, account, provider!.TenantId, ProviderBankAccountProtection.RecordIdOf(provider));
        }
        catch (FieldProtectionException ex)
        {
            // Leave whatever did not decrypt as stored (encrypted). Never log a value.
            var recordId = ProviderBankAccountProtection.RecordIdOf(provider!);
            account.RoutingNumber = TryUnprotect(account.RoutingNumber,
                ProviderBankAccountProtection.Context(provider!.TenantId, recordId, ProviderBankAccountProtection.RoutingField));
            account.AccountNumber = TryUnprotect(account.AccountNumber,
                ProviderBankAccountProtection.Context(provider.TenantId, recordId, ProviderBankAccountProtection.AccountField));
            account.TaxId = TryUnprotect(account.TaxId,
                ProviderBankAccountProtection.Context(provider.TenantId, recordId, ProviderBankAccountProtection.TaxIdField));
            _logger.LogError(UndecryptableEvent,
                "The bank-account copy on provider {ProviderId} (version {VersionId}) in tenant {TenantId} could not be decrypted " +
                "({Reason}); it stays encrypted on this read and nothing that needs the numbers can use it",
                Sanitize(provider!.ProviderId), Sanitize(provider.VersionId), Sanitize(provider.TenantId), ex.Message);
        }
        return provider;
    }

    /// <summary>The row's account as stored: encrypted and bound to the row's tenant and provider.</summary>
    private ProviderBankAccount? ForStorage(Provider row, ProviderBankAccount? account)
        => ProviderBankAccountProtection.ForStorage(_protector, account, row.TenantId, ProviderBankAccountProtection.RecordIdOf(row));

    private string? TryUnprotect(string? value, FieldProtectionContext context)
    {
        try
        {
            return _protector.Unprotect(value, context);
        }
        catch (FieldProtectionException)
        {
            return value;
        }
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
