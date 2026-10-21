using System.Text.RegularExpressions;
using CloudHealthOffice.Infrastructure.Gateways;

namespace AttachmentService.Services;

/// <summary>Upload limits for <c>POST api/attachments</c>. Bound from <c>Attachments:Upload</c>.</summary>
public sealed class AttachmentUploadOptions
{
    public const string SectionName = "Attachments:Upload";

    /// <summary>
    /// Largest file accepted. Defaults to the outbound gateway's limit (Stedi's
    /// recommended 64 MB, <see cref="ClaimAttachmentOptions.StediRecommendedMaxBytes"/>)
    /// so nothing is accepted here that cannot be forwarded.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = ClaimAttachmentOptions.StediRecommendedMaxBytes;

    /// <summary>Allowance for the multipart framing and form fields on top of the file.</summary>
    public long FormOverheadBytes { get; set; } = 1024 * 1024;
}

/// <summary>A document format attachment-service stores, and what it is stored as.</summary>
public sealed record AttachmentFormat(string Format, string ContentType, string Extension, string[] FileExtensions);

/// <summary>
/// What an uploaded attachment may be. Every value that reaches a storage
/// path or a Content-Type comes from here, never from the client: the
/// <c>DocumentFormat</c> and file name only select an entry.
/// </summary>
public static partial class AttachmentUploadRules
{
    /// <summary>
    /// The formats the outbound attachment gateway accepts
    /// (<see cref="ClaimAttachmentOptions.StediContentTypes"/>): PDF, TIFF, JPEG, PNG.
    /// </summary>
    public static readonly IReadOnlyList<AttachmentFormat> Formats = new[]
    {
        new AttachmentFormat("PDF", "application/pdf", ".pdf", new[] { ".pdf" }),
        new AttachmentFormat("TIFF", "image/tiff", ".tiff", new[] { ".tif", ".tiff" }),
        new AttachmentFormat("JPEG", "image/jpeg", ".jpg", new[] { ".jpg", ".jpeg" }),
        new AttachmentFormat("PNG", "image/png", ".png", new[] { ".png" }),
    };

    /// <summary>The format named by a <c>DocumentFormat</c> value (PDF, TIF, JPG, ...); null if not allowed.</summary>
    public static AttachmentFormat? ResolveFormat(string? documentFormat)
    {
        var value = documentFormat?.Trim().TrimStart('.').ToUpperInvariant();
        return value switch
        {
            "PDF" => Formats[0],
            "TIF" or "TIFF" => Formats[1],
            "JPG" or "JPEG" => Formats[2],
            "PNG" => Formats[3],
            _ => null,
        };
    }

    /// <summary>The Content-Type to serve a stored attachment with.</summary>
    public static string ContentTypeFor(string? documentFormat) =>
        ResolveFormat(documentFormat)?.ContentType ?? "application/octet-stream";

    /// <summary>The file extension to serve a stored attachment with.</summary>
    public static string ExtensionFor(string? documentFormat) =>
        ResolveFormat(documentFormat)?.Extension ?? ".bin";

    /// <summary>
    /// A claim, authorization or appeal id: letters, digits, '-' and '_' only,
    /// at most 128 characters. Anything else (a '/', "..", whitespace) is
    /// rejected rather than rewritten, so two different ids never share a
    /// storage folder.
    /// </summary>
    public static bool IsValidParentId(string? id) => id is not null && ParentIdPattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParentIdPattern();

    /// <summary>
    /// An attachment control number (275 TRN02 / 837 PWK06) is one token: it
    /// is looked up, logged and kept as a catalog source id.
    /// </summary>
    public static bool IsValidControlNumber(string? value) => value is not null && ControlNumberPattern().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex ControlNumberPattern();

    /// <summary>True when the file name's extension belongs to <paramref name="format"/>.</summary>
    public static bool ExtensionMatches(string? fileName, AttachmentFormat format)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return format.FileExtensions.Contains(extension, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when the content starts with <paramref name="format"/>'s file
    /// signature, so a renamed executable or HTML page is not stored as a PDF.
    /// Leaves a seekable stream at position 0.
    /// </summary>
    public static async Task<bool> HasSignatureAsync(Stream content, AttachmentFormat format, CancellationToken ct = default)
    {
        var header = new byte[8];
        var read = 0;
        while (read < header.Length)
        {
            var n = await content.ReadAsync(header.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        if (content.CanSeek) content.Position = 0;

        return StartsWithSignature(header.AsSpan(0, read), format);
    }

    private static bool StartsWithSignature(ReadOnlySpan<byte> h, AttachmentFormat format) =>
        format.Format switch
        {
            "PDF" => h.StartsWith("%PDF-"u8),
            "TIFF" => h.StartsWith("II*\0"u8) || h.StartsWith("MM\0*"u8),
            "JPEG" => h.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            "PNG" => h.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            _ => false,
        };
}
