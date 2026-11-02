using System.Text.RegularExpressions;

namespace DocumentService.Services;

/// <summary>
/// What an upload may contain and where it is stored. Applies to multipart
/// uploads and to pre-signed uploads (checked when they are finalized).
/// <list type="bullet">
/// <item>Only allow-listed content types; for the built-in types the first
/// bytes must match the declared type, so a script or HTML page cannot be
/// stored (and later served) as a "PDF".</item>
/// <item>A size cap (default 64 MiB; the upload endpoint raises Kestrel's request limit to match).</item>
/// <item>Storage paths start with the token's tenant and are built from
/// generated ids only, so no caller-supplied value reaches a path.</item>
/// </list>
/// Configuration: <c>Documents:MaxUploadBytes</c>,
/// <c>Documents:AllowedContentTypes</c> (array).
///
/// <para>Source extracts (<see cref="Models.DocumentTypes.SourceExtract"/>)
/// follow their own rule: CSV, plain text or X12 only, and never any other
/// type, so a member-facing type can't be filed as a spreadsheet; a core's
/// extract is never a PDF. Their cap is <c>Documents:MaxSourceExtractBytes</c>
/// (default 1 GiB): a plan's daily claim file outgrows an attachment's 64 MiB.
/// Above the multipart limit, use the pre-signed upload.</para>
///
/// <para>State submissions (<see cref="Models.DocumentTypes.StateSubmission"/>),
/// files CHO generated for a state regulator, are plain text or CSV only,
/// under the ordinary cap.</para>
/// </summary>
public sealed class DocumentUploadPolicy
{
    /// <summary>64 MiB: what clearinghouses accept for an X12 275 attachment (Stedi's recommended maximum).</summary>
    public const long DefaultMaxUploadBytes = 64L * 1024 * 1024;

    private static readonly Regex PathSegment = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, (string Extension, byte[][] Signatures)> KnownTypes =
        new Dictionary<string, (string, byte[][])>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = (".pdf", [ "%PDF-"u8.ToArray() ]),
            ["image/png"] = (".png", [ [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] ]),
            ["image/jpeg"] = (".jpg", [ [0xFF, 0xD8, 0xFF] ]),
            ["image/tiff"] = (".tif", [ [0x49, 0x49, 0x2A, 0x00], [0x4D, 0x4D, 0x00, 0x2A] ]),
            // C-CDA (CDA R2) clinical documents, the structured form the
            // Attachments IG and provider EHR exports carry. XML has no magic
            // number, so what is checked is that the content is XML at all,
            // with or without a UTF-8 byte-order mark; that is enough to stop a
            // PDF or an image being filed as a clinical document. Content is
            // only ever served as a download with X-Content-Type-Options:
            // nosniff, so XML is not an execution vector here.
            // An X12 interchange starts with its ISA segment (a source extract only).
            ["application/edi-x12"] = (".x12", [ "ISA"u8.ToArray(), [0xEF, 0xBB, 0xBF, (byte)'I', (byte)'S', (byte)'A'] ]),
            ["application/hl7-cda+xml"] = (".xml",
            [
                "<?xml"u8.ToArray(),
                [0xEF, 0xBB, 0xBF, (byte)'<', (byte)'?', (byte)'x', (byte)'m', (byte)'l'],
            ]),
        };

    /// <summary>1 GiB.</summary>
    public const long DefaultMaxSourceExtractBytes = 1024L * 1024 * 1024;

    /// <summary>What a source extract may be.</summary>
    public static readonly IReadOnlySet<string> SourceExtractContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/csv", "text/plain", "application/edi-x12" };

    public DocumentUploadPolicy(long maxUploadBytes, IEnumerable<string> allowedContentTypes, long? maxSourceExtractBytes = null)
    {
        MaxUploadBytes = maxUploadBytes;
        MaxSourceExtractBytes = maxSourceExtractBytes ?? DefaultMaxSourceExtractBytes;
        AllowedContentTypes = new HashSet<string>(allowedContentTypes.Select(NormalizeContentType)!, StringComparer.OrdinalIgnoreCase);
    }

    public static DocumentUploadPolicy Default { get; } = new(DefaultMaxUploadBytes, KnownTypes.Keys);

    public static DocumentUploadPolicy FromConfiguration(IConfiguration configuration)
    {
        var max = configuration.GetValue<long?>("Documents:MaxUploadBytes") ?? DefaultMaxUploadBytes;
        var types = configuration.GetSection("Documents:AllowedContentTypes").Get<string[]>();
        var extracts = configuration.GetValue<long?>("Documents:MaxSourceExtractBytes");
        return new DocumentUploadPolicy(max, types is { Length: > 0 } ? types : KnownTypes.Keys, extracts);
    }

    public long MaxUploadBytes { get; }

    public long MaxSourceExtractBytes { get; }

    private static bool IsSourceExtract(string? documentType)
        => string.Equals(documentType, Models.DocumentTypes.SourceExtract, StringComparison.OrdinalIgnoreCase);

    /// <summary>The size cap for a document of <paramref name="documentType"/>.</summary>
    public long MaxBytesFor(string? documentType)
        => IsSourceExtract(documentType) ? MaxSourceExtractBytes : MaxUploadBytes;

    /// <summary>Whether <paramref name="contentType"/> may be stored as <paramref name="documentType"/>.</summary>
    public bool IsAllowedContentType(string? documentType, string? contentType)
        => IsSourceExtract(documentType)
            ? NormalizeContentType(contentType) is { } t && SourceExtractContentTypes.Contains(t)
            : IsStateSubmission(documentType)
                ? NormalizeContentType(contentType) is { } s && StateSubmissionContentTypes.Contains(s)
                : IsAllowedContentType(contentType);

    /// <summary>What a state submission may be: the text files states take.</summary>
    public static readonly IReadOnlySet<string> StateSubmissionContentTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/plain", "text/csv" };

    private static bool IsStateSubmission(string? documentType)
        => string.Equals(documentType, Models.DocumentTypes.StateSubmission, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Whether a tenant or document id is usable as one storage path segment.</summary>
    public static bool IsSafePathSegment(string? value) => value != null && PathSegment.IsMatch(value);

    /// <summary>
    /// <c>tenants/{tenant}/documents/{documentId}{ext}</c>. The extension comes
    /// from the content type (or a plain alphanumeric file extension), never
    /// from free text. Subjects are links in the record, not path segments, so
    /// a document linked to many subjects is stored once.
    /// </summary>
    public static string BuildStoragePath(string tenantId, string documentId, string contentType, string? fileName)
    {
        if (!IsSafePathSegment(tenantId))
            throw new ArgumentException("Tenant id cannot be used in a storage path.", nameof(tenantId));
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

        return $"tenants/{tenantId}/documents/{documentId}{ext}";
    }
}
