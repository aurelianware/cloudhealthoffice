using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using ProviderService.Models;

namespace ProviderService.Security;

/// <summary>
/// Encryption at rest of the secret fields of a <see cref="ProviderBankAccount"/>:
/// <see cref="ProviderBankAccount.RoutingNumber"/>,
/// <see cref="ProviderBankAccount.AccountNumber"/> and
/// <see cref="ProviderBankAccount.TaxId"/>. Stored values are
/// <c>enc:v1:...</c> (<see cref="IFieldProtector"/>, purpose
/// <c>provider-service</c>); a value without the prefix is legacy plaintext,
/// read as is and stored encrypted by the next write.
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
    public static ProviderBankAccount? ForStorage(IFieldProtector protector, ProviderBankAccount? account)
    {
        if (account == null) return null;
        var copy = Clone(account);
        if (!protector.IsProtected(copy.RoutingNumber) && !string.IsNullOrEmpty(copy.RoutingNumber))
            copy.RoutingNumberLast4 ??= Last4(copy.RoutingNumber);
        if (!protector.IsProtected(copy.AccountNumber) && !string.IsNullOrEmpty(copy.AccountNumber))
            copy.AccountNumberLast4 ??= Last4(copy.AccountNumber);
        copy.RoutingNumber = Protect(protector, copy.RoutingNumber);
        copy.AccountNumber = Protect(protector, copy.AccountNumber);
        copy.TaxId = Protect(protector, copy.TaxId);
        return copy;
    }

    /// <summary>Encrypts plaintext; a value already encrypted is stored unchanged (even without a key ring).</summary>
    private static string? Protect(IFieldProtector protector, string? value)
        => IsCiphertext(value) ? value : protector.Protect(value);

    /// <summary>
    /// Decrypts the secret fields in place. Returns whether any of them was
    /// legacy plaintext. Throws <see cref="FieldProtectionException"/> when a
    /// value cannot be decrypted.
    /// </summary>
    public static bool Unprotect(IFieldProtector protector, ProviderBankAccount? account)
    {
        if (account == null) return false;
        var legacy = HasPlaintext(protector, account);
        account.RoutingNumber = protector.Unprotect(account.RoutingNumber);
        account.AccountNumber = protector.Unprotect(account.AccountNumber);
        account.TaxId = protector.Unprotect(account.TaxId);
        return legacy;
    }

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

    public static bool IsCiphertext(string? value)
        => value != null && value.StartsWith(DataProtectionFieldProtector.Prefix, StringComparison.Ordinal);

    private static bool IsPlaintext(IFieldProtector protector, string? value)
        => !string.IsNullOrEmpty(value) && !protector.IsProtected(value);

    private static string Last4(string value) => value.Length >= 4 ? value[^4..] : value;

    private static ProviderBankAccount Clone(ProviderBankAccount account)
        => JsonSerializer.Deserialize<ProviderBankAccount>(JsonSerializer.Serialize(account, CloneOptions), CloneOptions)!;
}
