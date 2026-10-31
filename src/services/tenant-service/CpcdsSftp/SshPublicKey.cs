using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TenantService.CpcdsSftp;

/// <summary>
/// An OpenSSH public key line (<c>ssh-ed25519 AAAA... comment</c>) as a plan
/// pastes it, checked before it is handed to the SFTP server: a known key
/// type, a blob that decodes and names the same type, and RSA keys of at
/// least <see cref="MinRsaBits"/> bits. Only the type and blob are kept; the
/// comment can carry a person's name or host and is dropped.
/// </summary>
public sealed record SshPublicKey(string Type, string Blob)
{
    public const int MinRsaBits = 3072;

    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "ssh-rsa",
    };

    /// <summary>The line SFTPGo stores: type and blob, no comment.</summary>
    public string Line => $"{Type} {Blob}";

    /// <summary>OpenSSH's <c>SHA256:...</c> fingerprint, safe to show back.</summary>
    public string Fingerprint
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(Blob))).TrimEnd('=');

    /// <summary>The key, or the reason it is refused.</summary>
    public static (SshPublicKey? Key, string? Error) Parse(string? line)
    {
        var parts = (line ?? string.Empty).Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (null, "Not an OpenSSH public key line (\"<type> <base64> [comment]\").");

        var (type, blob) = (parts[0], parts[1]);
        if (!AllowedTypes.Contains(type))
            return (null, $"Key type '{Truncate(type)}' is not accepted; use ssh-ed25519, ecdsa-sha2-nistp256/384/521 or ssh-rsa.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(blob);
        }
        catch (FormatException)
        {
            return (null, "The key data is not base64.");
        }

        var reader = new Reader(bytes);
        if (!reader.TryString(out var embedded) || Encoding.ASCII.GetString(embedded) != type)
            return (null, "The key data does not match its type.");

        if (type == "ssh-rsa")
        {
            if (!reader.TryString(out _) || !reader.TryString(out var modulus))
                return (null, "The RSA key is malformed.");
            var bits = RsaBits(modulus);
            if (bits < MinRsaBits)
                return (null, $"RSA keys must be at least {MinRsaBits} bits; this one is {bits}. ssh-ed25519 is preferred.");
        }
        else if (type == "ssh-ed25519")
        {
            if (!reader.TryString(out var point) || point.Length != 32)
                return (null, "The ed25519 key is malformed.");
        }
        else if (!reader.TryString(out _) || !reader.TryString(out _))
        {
            return (null, "The ECDSA key is malformed.");
        }

        return (new SshPublicKey(type, blob), null);
    }

    private static int RsaBits(ReadOnlySpan<byte> modulus)
    {
        // An mpint: big-endian, with a leading zero byte when the top bit is set.
        var i = 0;
        while (i < modulus.Length && modulus[i] == 0) i++;
        if (i == modulus.Length) return 0;
        var top = modulus[i];
        var topBits = 8 - BitOperations(top);
        return (modulus.Length - i - 1) * 8 + topBits;
    }

    private static int BitOperations(byte b)
    {
        var leading = 0;
        for (var mask = 0x80; mask != 0 && (b & mask) == 0; mask >>= 1) leading++;
        return leading;
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40];

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _rest = data;

        public bool TryString(out ReadOnlySpan<byte> value)
        {
            value = default;
            if (_rest.Length < 4) return false;
            var length = BinaryPrimitives.ReadUInt32BigEndian(_rest);
            if (length > (uint)(_rest.Length - 4)) return false;
            value = _rest.Slice(4, (int)length);
            _rest = _rest[(4 + (int)length)..];
            return true;
        }
    }
}
