using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace CloudHealthOffice.FieldProtection;

/// <summary>
/// Encrypts individual sensitive fields (bank routing and account numbers)
/// before they are stored, and decrypts them after they are read.
///
/// <para>
/// A protected value is <c>enc:v1:</c> followed by the ciphertext, so stored
/// documents can hold protected values next to values written before
/// encryption existed (legacy plaintext). <see cref="Unprotect"/> returns a
/// legacy value unchanged and reports it through <see cref="IsProtected"/>;
/// callers re-save it protected on their next write.
/// </para>
/// </summary>
public interface IFieldProtector
{
    /// <summary>The value as it should be stored. Null and empty pass through; an already protected value is returned unchanged.</summary>
    string? Protect(string? plaintext);

    /// <summary>The plaintext of a stored value. A legacy plaintext value is returned as is.</summary>
    string? Unprotect(string? stored);

    /// <summary>Whether a stored value carries the protected prefix.</summary>
    bool IsProtected(string? stored);
}

/// <summary>Field protection is not usable: no key ring is configured, or a value cannot be decrypted.</summary>
public sealed class FieldProtectionException : Exception
{
    public FieldProtectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// <see cref="IFieldProtector"/> over ASP.NET Core Data Protection
/// (authenticated encryption, keys from the configured key ring, rotated by
/// Data Protection; old keys stay in the ring so older values still decrypt).
/// </summary>
public sealed class DataProtectionFieldProtector : IFieldProtector
{
    public const string Prefix = "enc:v1:";

    /// <summary>Root purpose; the service's purpose is a sub-purpose, so two services never read each other's values.</summary>
    public const string RootPurpose = "CloudHealthOffice.FieldProtection.v1";

    private readonly IDataProtector _protector;

    public DataProtectionFieldProtector(IDataProtectionProvider provider, string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("A purpose is required.", nameof(purpose));
        _protector = provider.CreateProtector(RootPurpose, purpose);
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        return Prefix + _protector.Protect(plaintext);
    }

    public string? Unprotect(string? stored)
    {
        if (!IsProtected(stored)) return stored;
        try
        {
            return _protector.Unprotect(stored![Prefix.Length..]);
        }
        catch (CryptographicException ex)
        {
            // Never include the value: it may be a (partly) readable number.
            throw new FieldProtectionException(
                "A protected field could not be decrypted: the key ring does not hold its key, or the value was altered.", ex);
        }
    }

    public bool IsProtected(string? stored)
        => stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>
/// Registered when no key ring is configured outside Development: nothing can
/// be encrypted or decrypted, so every write of a protected field fails
/// instead of storing plaintext or using keys that only this pod holds.
/// Legacy plaintext values can still be read.
/// </summary>
public sealed class UnconfiguredFieldProtector : IFieldProtector
{
    public const string Message =
        "Field encryption is not configured: set FieldProtection:KeyRing:BlobUri and " +
        "FieldProtection:KeyRing:KeyVaultKeyId (see docs/security/bank-account-data.md).";

    public string? Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? plaintext : throw new FieldProtectionException(Message);

    public string? Unprotect(string? stored)
        => IsProtected(stored) ? throw new FieldProtectionException(Message) : stored;

    public bool IsProtected(string? stored)
        => stored != null && stored.StartsWith(DataProtectionFieldProtector.Prefix, StringComparison.Ordinal);
}
