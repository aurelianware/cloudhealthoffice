using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using ProviderService.Models;

namespace ProviderService.Security;

/// <summary>
/// Encryption at rest of the secret fields of a <see cref="ProviderBankAccount"/>:
/// <see cref="ProviderBankAccount.RoutingNumber"/>,
/// <see cref="ProviderBankAccount.AccountNumber"/> and
/// <see cref="ProviderBankAccount.TaxId"/>. Stored values are
/// <c>enc:v2:...</c> (<see cref="IFieldProtector"/>, purpose
/// <c>provider-service</c>), bound to the tenant, the provider id and the
/// field, so a value copied to another provider, tenant or field does not
/// decrypt. <c>enc:v1:...</c> (written before binding) still decrypts, and a
/// value without a prefix is legacy plaintext, read as is (unless
/// <c>FieldProtection:RejectPlaintext</c>); both are stored as <c>enc:v2:</c>
/// by the next write or by <c>--encrypt-bank-accounts</c>.
/// </summary>
public static class ProviderBankAccountProtection
{
    /// <summary>The field-protection purpose (and Data Protection application name) of provider-service.</summary>
    public const string Purpose = "provider-service";

    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A copy of <paramref name="account"/> as it is stored: secret fields
    /// encrypted. The last-4 display fields are filled from the plaintext when
    /// missing, so masked reads keep working without decrypting. The argument
    /// is not changed. Throws <see cref="FieldProtectionException"/> when no
    /// key ring is configured and there is something to encrypt.
    /// </summary>
    public static ProviderBankAccount? ForStorage(IFieldProtector protector, ProviderBankAccount? account, string? tenantId, string providerId)
    {
        if (account == null) return null;
        var copy = Clone(account);
        if (!protector.IsProtected(copy.RoutingNumber) && !string.IsNullOrEmpty(copy.RoutingNumber))
            copy.RoutingNumberLast4 ??= Last4(copy.RoutingNumber);
        if (!protector.IsProtected(copy.AccountNumber) && !string.IsNullOrEmpty(copy.AccountNumber))
            copy.AccountNumberLast4 ??= Last4(copy.AccountNumber);
        copy.RoutingNumber = Protect(protector, copy.RoutingNumber, Context(tenantId, providerId, RoutingField));
        copy.AccountNumber = Protect(protector, copy.AccountNumber, Context(tenantId, providerId, AccountField));
        copy.TaxId = Protect(protector, copy.TaxId, Context(tenantId, providerId, TaxIdField));
        return copy;
    }

    public const string RoutingField = "routingNumber";
    public const string AccountField = "accountNumber";
    public const string TaxIdField = "taxId";

    /// <summary>The binding of one secret field of a provider's bank account.</summary>
    public static FieldProtectionContext Context(string? tenantId, string providerId, string field)
        => new(tenantId, providerId, field);

    /// <summary>The record id provider rows bind to: the chain key, or the row id on a legacy row without one.</summary>
    public static string RecordIdOf(Provider provider)
        => string.IsNullOrEmpty(provider.ProviderId) ? provider.Id : provider.ProviderId;

    /// <summary>Encrypts plaintext; a value already encrypted is stored unchanged (even without a key ring).</summary>
    private static string? Protect(IFieldProtector protector, string? value, FieldProtectionContext context)
        => IsCiphertext(value) ? value : protector.Protect(value, context);

    /// <summary>
    /// Decrypts the secret fields in place. Returns whether any of them was
    /// legacy plaintext. Throws <see cref="FieldProtectionException"/> when a
    /// value cannot be decrypted.
    /// </summary>
    public static bool Unprotect(IFieldProtector protector, ProviderBankAccount? account, string? tenantId, string providerId)
    {
        if (account == null) return false;
        var legacy = HasPlaintext(protector, account);
        account.RoutingNumber = protector.Unprotect(account.RoutingNumber, Context(tenantId, providerId, RoutingField));
        account.AccountNumber = protector.Unprotect(account.AccountNumber, Context(tenantId, providerId, AccountField));
        account.TaxId = protector.Unprotect(account.TaxId, Context(tenantId, providerId, TaxIdField));
        return legacy;
    }

    /// <summary>Whether any secret field is not yet bound to its record (plaintext or <c>enc:v1:</c>).</summary>
    public static bool NeedsRebinding(ProviderBankAccount? account)
        => account != null && (Unbound(account.RoutingNumber) || Unbound(account.AccountNumber) || Unbound(account.TaxId));

    private static bool Unbound(string? value) => !string.IsNullOrEmpty(value) && !FieldCiphertext.IsBound(value);

    /// <summary>Whether any secret field is stored in plaintext (written before encryption).</summary>
    public static bool HasPlaintext(IFieldProtector protector, ProviderBankAccount? account)
        => account != null && (IsPlaintext(protector, account.RoutingNumber)
                               || IsPlaintext(protector, account.AccountNumber)
                               || IsPlaintext(protector, account.TaxId));

    /// <summary>Whether any secret field still holds ciphertext (it could not be decrypted).</summary>
    public static bool HasCiphertext(ProviderBankAccount? account)
        => account != null && (IsCiphertext(account.RoutingNumber)
                               || IsCiphertext(account.AccountNumber)
                               || IsCiphertext(account.TaxId));

    public static bool IsCiphertext(string? value) => FieldCiphertext.IsCiphertext(value);

    private static bool IsPlaintext(IFieldProtector protector, string? value)
        => !string.IsNullOrEmpty(value) && !protector.IsProtected(value);

    private static string Last4(string value) => value.Length >= 4 ? value[^4..] : value;

    private static ProviderBankAccount Clone(ProviderBankAccount account)
        => JsonSerializer.Deserialize<ProviderBankAccount>(JsonSerializer.Serialize(account, CloneOptions), CloneOptions)!;
}
