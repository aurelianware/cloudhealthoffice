using System.Security.Cryptography;

namespace ConsentService.Services;

/// <summary>
/// Thrown when an envelope references a key version that is listed in the
/// service's <c>AcceptedKeyVersions</c> window but whose underlying secret
/// cannot be resolved from the
/// <see cref="CloudHealthOffice.Infrastructure.Configuration.ISecretProvider"/>.
/// Distinct from a plain <see cref="CryptographicException"/> (which
/// indicates tampered ciphertext or the wrong key) so that callers can
/// distinguish "rotation migration failure — page ops" from "possible
/// tamper — audit the caller".
/// </summary>
public sealed class StaleEncryptionKeyException : CryptographicException
{
    public string KeyVersion { get; }

    public StaleEncryptionKeyException(string keyVersion, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        KeyVersion = keyVersion;
    }
}
