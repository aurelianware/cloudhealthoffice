using System.Text.RegularExpressions;

namespace MemberDocumentService.Services;

/// <summary>
/// What an upload may contain and where it is stored. Applies to multipart
/// uploads and to pre-signed uploads (checked when they are finalized).
/// <list type="bullet">
/// <item>Only allow-listed content types; for the built-in types the first
/// bytes must match the declared type, so a script or HTML page cannot be
/// stored (and later served) as a "PDF".</item>
/// <item>A size cap (default 25 MB, below Kestrel's 30 MB request limit).</item>
/// <item>Blob paths start with the token's tenant, and every path segment is
/// validated, so no caller-supplied value can address another tenant's blobs.</item>
/// </list>
/// Configuration: <c>MemberDocuments:MaxUploadBytes</c>,
/// <c>MemberDocuments:AllowedContentTypes</c> (array).
/// </summary>
public sealed class MemberDocumentUploadPolicy
{
    public const long DefaultMaxUploadBytes = 25L * 1024 * 1024;

    private static readonly Regex PathSegment = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, (string Extension, byte[][] Signatures)> KnownTypes =
        new Dictionary<string, (string, byte[][])>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = (".pdf", [ "%PDF-"u8.ToArray() ]),
            ["image/png"] = (".png", [ [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] ]),
            ["image/jpeg"] = (".jpg", [ [0xFF, 0xD8, 0xFF] ]),
            ["image/tiff"] = (".tif", [ [0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A] ]),
        };

    public MemberDocumentUploadPolicy(long maxUploadBytes, IEnumerable<string> allowedContentTypes)
    {
        MaxUploadBytes = maxUploadBytes;
        AllowedContentTypes = new HashSet<string>(allowedContentTypes.Select(NormalizeContentType)!, StringComparer.OrdinalIgnoreCase);
    }

    public static MemberDocumentUploadPolicy Default { get; } = new(DefaultMaxUploadBytes, KnownTypes.Keys);

    public static MemberDocumentUploadPolicy FromConfiguration(IConfiguration configuration)
    {
        var max = configuration.GetValue<long?>("MemberDocuments:MaxUploadBytes") ?? DefaultMaxUploadBytes;
        var types = configuration.GetSection("MemberDocuments:AllowedContentTypes").Get<string[]>();
        return new MemberDocumentUploadPolicy(max, types is { Length: > 0 } ? types : KnownTypes.Keys);
    }

    public long MaxUploadBytes { get; }

    public IReadOnlySet<string> AllowedContentTypes { get; }

    /// <summary>The media type without parameters, lower-cased; null when empty.</summary>
    public static string? NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return null;
        var semi = contentType.IndexOf(';');
        return (semi < 0 ? contentType : contentType[..semi]).Trim().ToLowerInvariant();
    }

    public bool IsAllowedContentType(string? contentType)
        => NormalizeContentType(contentType) is { } t && AllowedContentTypes.Contains(t);

    /// <summary>
    /// Whether <paramref name="prefix"/> (the first bytes of the content) is
    /// consistent with the declared type. Types without a known signature pass.
    /// </summary>
    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> prefix)
    {
        if (!KnownTypes.TryGetValue(NormalizeContentType(contentType) ?? string.Empty, out var known))
            return true;
        foreach (var signature in known.Signatures)
        {
            if (prefix.Length >= signature.Length && prefix[..signature.Length].SequenceEqual(signature))
                return true;
        }
        return false;
    }

    /// <summary>Whether a tenant or member id is usable as one blob path segment.</summary>
    public static bool IsSafePathSegment(string? value) => value != null && PathSegment.IsMatch(value);

    /// <summary>
    /// <c>tenants/{tenant}/members/{member}/{documentId}{ext}</c>. The extension
    /// comes from the content type (or a plain alphanumeric file extension),
    /// never from free text.
    /// </summary>
    public static string BuildBlobPath(string tenantId, string memberId, string documentId, string contentType, string? fileName)
    {
        if (!IsSafePathSegment(tenantId))
            throw new ArgumentException("Tenant id cannot be used in a storage path.", nameof(tenantId));
        if (!IsSafePathSegment(memberId))
            throw new ArgumentException("Member id cannot be used in a storage path.", nameof(memberId));
        if (!IsSafePathSegment(documentId))
            throw new ArgumentException("Document id cannot be used in a storage path.", nameof(documentId));

        string ext;
        if (KnownTypes.TryGetValue(NormalizeContentType(contentType) ?? string.Empty, out var known))
        {
            ext = known.Extension;
        }
        else
        {
            ext = Path.GetExtension(fileName ?? string.Empty);
            if (!Regex.IsMatch(ext, "^\\.[A-Za-z0-9]{1,10}$")) ext = string.Empty;
        }

        return $"tenants/{tenantId}/members/{memberId}/{documentId}{ext}";
    }
}
