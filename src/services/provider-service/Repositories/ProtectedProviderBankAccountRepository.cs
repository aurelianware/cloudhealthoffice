using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using ProviderService.Models;
using ProviderService.Security;

namespace ProviderService.Repositories;

/// <summary>
/// Encrypts the bank numbers in <c>ProviderBankAccounts</c> at rest around the
/// Mongo or Cosmos store: the active account, every change's proposed account
/// (full numbers while pending) and previous account. Saves store them
/// encrypted (<c>enc:v1:...</c>); without a key ring outside Development the
/// save fails (<see cref="FieldProtectionException"/>, answered 503) and
/// nothing is stored. Reads return them decrypted; a value that does not
/// decrypt fails the read (503), since these records are what payments use.
/// Legacy plaintext is read as is, logged (event 4707, never the number) and
/// stored encrypted by the record's next save or by
/// <c>--encrypt-bank-accounts</c>.
/// </summary>
public sealed class ProtectedProviderBankAccountRepository : IProviderBankAccountRepository
{
    public static readonly EventId LegacyPlaintextEvent = new(4707, "ProviderBankAccountPlaintext");

    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    private readonly IProviderBankAccountRepository _inner;
    private readonly IFieldProtector _protector;
    private readonly ILogger<ProtectedProviderBankAccountRepository> _logger;

    public ProtectedProviderBankAccountRepository(
        IProviderBankAccountRepository inner,
        IFieldProtector protector,
        ILogger<ProtectedProviderBankAccountRepository> logger)
    {
        _inner = inner;
        _protector = protector;
        _logger = logger;
    }

    public async Task<ProviderBankAccountRecord?> GetAsync(string tenantId, string providerId, CancellationToken ct = default)
    {
        var record = await _inner.GetAsync(tenantId, providerId, ct);
        if (record == null) return null;

        var legacy = ProviderBankAccountProtection.Unprotect(_protector, record.Active);
        foreach (var change in record.Changes)
        {
            legacy |= ProviderBankAccountProtection.Unprotect(_protector, change.Proposed);
            legacy |= ProviderBankAccountProtection.Unprotect(_protector, change.PreviousAccount);
        }

        if (legacy)
        {
            _logger.LogWarning(LegacyPlaintextEvent,
                "The bank-account record of provider {ProviderId} in tenant {TenantId} holds numbers stored before encryption; " +
                "they are stored encrypted by its next save (or run provider-service --encrypt-bank-accounts)",
                Sanitize(record.ProviderId), Sanitize(record.TenantId));
        }
        return record;
    }

    public async Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        var stored = Clone(record);
        stored.Active = ProviderBankAccountProtection.ForStorage(_protector, stored.Active);
        foreach (var change in stored.Changes)
        {
            change.Proposed = ProviderBankAccountProtection.ForStorage(_protector, change.Proposed);
            change.PreviousAccount = ProviderBankAccountProtection.ForStorage(_protector, change.PreviousAccount);
        }

        var saved = await _inner.SaveAsync(stored, expectedRevision, ct);
        // What the store stamps on the record it wrote.
        record.Id = stored.Id;
        record.Revision = stored.Revision;
        record.UpdatedAt = stored.UpdatedAt;
        return saved;
    }

    private static ProviderBankAccountRecord Clone(ProviderBankAccountRecord record)
        => JsonSerializer.Deserialize<ProviderBankAccountRecord>(JsonSerializer.Serialize(record, CloneOptions), CloneOptions)!;

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
