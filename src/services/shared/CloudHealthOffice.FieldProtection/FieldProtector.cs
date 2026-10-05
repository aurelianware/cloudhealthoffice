using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace CloudHealthOffice.FieldProtection;

/// <summary>
/// Where a protected value lives: the tenant, the record (a stable id of the
/// entity that owns the value, e.g. the provider id or sponsor id) and the
/// field. A value written with a context (<c>enc:v2:</c>) decrypts only with
/// the same context, so a ciphertext copied to another record, tenant or field
/// does not decrypt.
/// </summary>
public readonly record struct FieldProtectionContext
{
    public FieldProtectionContext(string? tenantId, string recordId, string field)
    {
        if (string.IsNullOrEmpty(recordId)) throw new ArgumentException("A record id is required.", nameof(recordId));
        if (string.IsNullOrEmpty(field)) throw new ArgumentException("A field name is required.", nameof(field));
        TenantId = tenantId ?? string.Empty;
        RecordId = recordId;
        Field = field;
    }

    /// <summary>The tenant; empty only for legacy rows written without one.</summary>
    public string TenantId { get; }

    public string RecordId { get; }

    public string Field { get; }
}

/// <summary>Recognises stored ciphertext of every format.</summary>
public static class FieldCiphertext
{
    /// <summary>Not bound to a record (kept readable; new record fields are written as <see cref="BoundPrefix"/>).</summary>
    public const string UnboundPrefix = "enc:v1:";

    /// <summary>Bound to a <see cref="FieldProtectionContext"/>.</summary>
    public const string BoundPrefix = "enc:v2:";

    /// <summary>Whether <paramref name="value"/> is stored ciphertext (any version).</summary>
    public static bool IsCiphertext(string? value)
        => value != null && (value.StartsWith(UnboundPrefix, StringComparison.Ordinal)
                             || value.StartsWith(BoundPrefix, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="value"/> is ciphertext bound to its record (<c>enc:v2:</c>).</summary>
    public static bool IsBound(string? value)
        => value != null && value.StartsWith(BoundPrefix, StringComparison.Ordinal);
}

/// <summary>
/// Encrypts individual sensitive fields (bank routing and account numbers)
/// before they are stored, and decrypts them after they are read.
///
/// <para>
/// Record fields are protected with a <see cref="FieldProtectionContext"/>:
/// <c>enc:v2:</c> followed by ciphertext bound to the tenant, record and field.
/// Values without a record (a held file) use the context-free overloads:
/// <c>enc:v1:</c>. Record fields written before binding existed (<c>enc:v1:</c>)
/// still decrypt through the context overloads (unless
/// <c>FieldProtection:RejectUnbound</c> is on, in which case that throws
/// <see cref="FieldProtectionException"/>), and values written before
/// encryption existed (legacy plaintext) are returned unchanged and reported
/// through <see cref="IsProtected"/>, unless <c>FieldProtection:RejectPlaintext</c>
/// is on (set it once <c>--encrypt-bank-accounts</c> has run), in which case
/// reading plaintext throws <see cref="FieldProtectionException"/>.
/// </para>
/// </summary>
public interface IFieldProtector
{
    /// <summary>The value as it should be stored (<c>enc:v1:</c>). Null and empty pass through; an already protected value is returned unchanged.</summary>
    string? Protect(string? plaintext);

    /// <summary>The plaintext of a stored <c>enc:v1:</c> value. A legacy plaintext value is returned as is (or refused, see the interface remarks).</summary>
    string? Unprotect(string? stored);

    /// <summary>Whether a stored value carries a protected prefix (any version).</summary>
    bool IsProtected(string? stored);

    /// <summary>
    /// The value as it should be stored, bound to <paramref name="context"/>
    /// (<c>enc:v2:</c>). Null and empty pass through; a value that is already
    /// ciphertext is returned unchanged (it was read but could not be decrypted).
    /// </summary>
    string? Protect(string? plaintext, FieldProtectionContext context) => Protect(plaintext);

    /// <summary>
    /// The plaintext of a stored value of the record and field in
    /// <paramref name="context"/>: <c>enc:v2:</c> bound to that context,
    /// <c>enc:v1:</c>, or legacy plaintext (returned as is, or refused).
    /// </summary>
    string? Unprotect(string? stored, FieldProtectionContext context) => Unprotect(stored);
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
    /// <summary>The context-free prefix (<see cref="FieldCiphertext.UnboundPrefix"/>).</summary>
    public const string Prefix = FieldCiphertext.UnboundPrefix;

    /// <summary>Root purpose; the service's purpose is a sub-purpose, so two services never read each other's values.</summary>
    public const string RootPurpose = "CloudHealthOffice.FieldProtection.v1";

    /// <summary>Root purpose of values bound to a record (<c>enc:v2:</c>).</summary>
    public const string BoundRootPurpose = "CloudHealthOffice.FieldProtection.v2";

    internal const string UnboundRefused =
        "A protected field holds a value encrypted before record binding (enc:v1), and FieldProtection:RejectUnbound is on. " +
        "Run the service's --encrypt-bank-accounts command, or fix the record.";

    internal const string PlaintextRefused =
        "A protected field holds a value stored before encryption, and FieldProtection:RejectPlaintext is on. " +
        "Run the service's --encrypt-bank-accounts command, or fix the record.";

    private readonly IDataProtector _protector;
    private readonly IDataProtector _boundRoot;
    private readonly bool _rejectPlaintext;
    private readonly bool _rejectUnbound;

    /// <param name="rejectPlaintext">Refuse legacy plaintext on read (<c>FieldProtection:RejectPlaintext</c>).</param>
    /// <param name="rejectUnbound">
    /// Refuse an <c>enc:v1:</c> value read through a context overload, i.e. a
    /// record field not yet bound to its record (<c>FieldProtection:RejectUnbound</c>).
    /// The context-free overload still reads <c>enc:v1:</c>: that is its format.
    /// </param>
    public DataProtectionFieldProtector(
        IDataProtectionProvider provider, string purpose, bool rejectPlaintext = false, bool rejectUnbound = false)
    {
        if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("A purpose is required.", nameof(purpose));
        _protector = provider.CreateProtector(RootPurpose, purpose);
        _boundRoot = provider.CreateProtector(BoundRootPurpose, purpose);
        _rejectPlaintext = rejectPlaintext;
        _rejectUnbound = rejectUnbound;
    }

    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        return Prefix + _protector.Protect(plaintext);
    }

    public string? Protect(string? plaintext, FieldProtectionContext context)
    {
        if (string.IsNullOrEmpty(plaintext) || IsProtected(plaintext)) return plaintext;
        return FieldCiphertext.BoundPrefix + Bound(context).Protect(plaintext);
    }

    public string? Unprotect(string? stored)
    {
        if (FieldCiphertext.IsBound(stored))
        {
            throw new FieldProtectionException(
                "A protected field is bound to its record and can only be read with that record's context.");
        }
        return UnprotectUnboundOrPlaintext(stored);
    }

    public string? Unprotect(string? stored, FieldProtectionContext context)
    {
        if (FieldCiphertext.IsBound(stored))
            return Decrypt(Bound(context), stored![FieldCiphertext.BoundPrefix.Length..]);
        if (_rejectUnbound && stored != null && stored.StartsWith(FieldCiphertext.UnboundPrefix, StringComparison.Ordinal))
            throw new FieldProtectionException(UnboundRefused);
        return UnprotectUnboundOrPlaintext(stored);
    }

    public bool IsProtected(string? stored) => FieldCiphertext.IsCiphertext(stored);

    private string? UnprotectUnboundOrPlaintext(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return _rejectPlaintext ? throw new FieldProtectionException(PlaintextRefused) : stored;
        return Decrypt(_protector, stored[Prefix.Length..]);
    }

    private IDataProtector Bound(FieldProtectionContext context)
        // Prefixed parts: an empty tenant stays a distinct, non-empty purpose.
        => _boundRoot.CreateProtector("tenant:" + context.TenantId, "record:" + context.RecordId, "field:" + context.Field);

    private static string Decrypt(IDataProtector protector, string ciphertext)
    {
        try
        {
            return protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            // Never include the value: it may be a (partly) readable number.
            throw new FieldProtectionException(
                "A protected field could not be decrypted: the key ring does not hold its key, the value was altered, " +
                "or it belongs to another record.", ex);
        }
    }
}

/// <summary>
/// Registered when no key ring is configured outside Development: nothing can
/// be encrypted or decrypted, so every write of a protected field fails
/// instead of storing plaintext or using keys that only this pod holds.
/// Legacy plaintext values can still be read (unless
/// <c>FieldProtection:RejectPlaintext</c> is on).
/// </summary>
public sealed class UnconfiguredFieldProtector : IFieldProtector
{
    public const string Message =
        "Field encryption is not configured: set FieldProtection:KeyRing:BlobUri and " +
        "FieldProtection:KeyRing:KeyVaultKeyId (see docs/security/bank-account-data.md).";

    private readonly bool _rejectPlaintext;

    public UnconfiguredFieldProtector() : this(false) { }

    public UnconfiguredFieldProtector(bool rejectPlaintext) => _rejectPlaintext = rejectPlaintext;

    public string? Protect(string? plaintext)
        => string.IsNullOrEmpty(plaintext) ? plaintext : throw new FieldProtectionException(Message);

    public string? Protect(string? plaintext, FieldProtectionContext context) => Protect(plaintext);

    public string? Unprotect(string? stored)
    {
        if (IsProtected(stored)) throw new FieldProtectionException(Message);
        if (_rejectPlaintext && !string.IsNullOrEmpty(stored))
            throw new FieldProtectionException(DataProtectionFieldProtector.PlaintextRefused);
        return stored;
    }

    public string? Unprotect(string? stored, FieldProtectionContext context) => Unprotect(stored);

    public bool IsProtected(string? stored) => FieldCiphertext.IsCiphertext(stored);
}
