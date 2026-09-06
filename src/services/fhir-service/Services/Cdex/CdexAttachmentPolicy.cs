using System.Security.Cryptography;
using System.Text;

namespace FhirService.Services.Cdex;

/// <summary>
/// What CHO will accept as documentation on a pended prior authorization, and
/// how a stored artifact is named.
///
/// Pure and deterministic on purpose: these limits are the ones the acceptance
/// suite drives directly, and they are the same limits whichever intake path a
/// submission arrives on.
/// </summary>
public static class CdexAttachmentPolicy
{
    /// <summary>Container the submitted artifacts are written to. Never caller-controlled.</summary>
    public const string StorageContainer = "cdex-attachments";

    /// <summary>Largest single artifact CHO will store, decoded.</summary>
    public const long MaxAttachmentBytes = 20L * 1024 * 1024;

    /// <summary>Largest total payload in one <c>$submit-attachment</c> call, decoded.</summary>
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    /// <summary>Most artifacts one call may carry.</summary>
    public const int MaxAttachmentsPerSubmission = 10;

    /// <summary>Longest title CHO will retain. Titles are metadata, never a path.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>
    /// Content types CHO accepts as clinical documentation.
    ///
    /// An ALLOW-LIST, and a narrow one: these are the formats the HL7
    /// Attachments IG and the X12 275 transaction actually carry. Anything else
    /// — archives, executables, office macro formats, anything CHO would have to
    /// guess at — is refused rather than stored and hoped for.
    ///
    /// Deliberately WIDER than the gateway's own claim-attachment list (PDF and
    /// images), because a prior-authorization question is commonly answered with
    /// a C-CDA or FHIR document, and NARROWER than "whatever the sender sends".
    /// </summary>
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf",
            "image/jpeg",
            "image/png",
            "image/tiff",
            "text/plain",
            "text/rtf",
            "application/rtf",
            "text/xml",
            "application/xml",
            // C-CDA / CDA R2 documents, the Attachments IG's structured form.
            "application/hl7-cda+xml",
            "application/fhir+json",
            "application/json",
        };

    public static bool IsAllowedContentType(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType)
           && AllowedContentTypes.Contains(Normalize(contentType));

    /// <summary>The accepted content types, for documentation and error messages.</summary>
    public static IReadOnlyCollection<string> SupportedContentTypes
        => AllowedContentTypes.OrderBy(t => t, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Strips any parameters a caller appended (<c>application/pdf; charset=…</c>)
    /// so a well-formed variant is not refused, and lower-cases for comparison.
    /// </summary>
    private static string Normalize(string contentType)
    {
        var separator = contentType.IndexOf(';');
        var bare = separator >= 0 ? contentType[..separator] : contentType;
        return bare.Trim();
    }

    /// <summary>
    /// Identity of ONE submission: the tenant, the case it answers, the tracking
    /// id quoted, and the content itself.
    ///
    /// Content-derived on purpose. A retry of the same document lands on the same
    /// id and is recognised as a replay; a genuinely DIFFERENT document under the
    /// same request gets a different id and is appended as an additional
    /// response rather than silently overwriting the first.
    /// </summary>
    public static string SubmissionId(
        string tenantId, string caseId, string trackingId, string contentHash)
        => Sha256Hex($"{tenantId}|{caseId}|{trackingId}|{contentHash}")[..32];

    public static string Sha256Hex(ReadOnlySpan<byte> content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string Sha256Hex(string value)
        => Sha256Hex(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Keeps a caller-supplied title as METADATA and nothing more: path
    /// separators, traversal segments and control characters are removed, and the
    /// result is truncated. It is never used to name a blob — the storage key is
    /// derived from server-side values only — so this is defence in depth, not
    /// the defence.
    /// </summary>
    public static string? SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var cleaned = new StringBuilder(Math.Min(title.Length, MaxTitleLength));
        foreach (var c in title)
        {
            if (cleaned.Length >= MaxTitleLength) break;
            if (char.IsControl(c) || c is '/' or '\\') continue;
            cleaned.Append(c);
        }

        var result = cleaned.ToString().Replace("..", string.Empty, StringComparison.Ordinal).Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Restricts a value to characters safe in a storage key. The shared
    /// attachment content store derives the key from these server-side values —
    /// tenant, case, submission id, checksum and validated content type — so no
    /// part of a caller's filename or title ever reaches a storage path.
    /// </summary>
    public static string Slug(string value)
    {
        var slug = new StringBuilder(value.Length);
        foreach (var c in value)
            slug.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return slug.ToString();
    }
}

/// <summary>
/// The verdict of scanning submitted content before it is stored.
///
/// <see cref="Scanned"/> is separate from <see cref="Clean"/> on purpose: a
/// scanner that did not run has not pronounced content clean, and the recorded
/// scan status must say <c>Unknown</c> rather than <c>Safe</c>. Collapsing the
/// two would turn "we have no scanner" into "we checked and it was fine".
/// </summary>
public sealed record AttachmentScanResult(bool Clean, bool Scanned, string? Reason = null)
{
    /// <summary>Scanned, and clean.</summary>
    public static readonly AttachmentScanResult CleanResult = new(Clean: true, Scanned: true);

    /// <summary>Not scanned at all. Not a passing verdict — an absent one.</summary>
    public static readonly AttachmentScanResult NotScanned = new(Clean: true, Scanned: false);

    public static AttachmentScanResult Rejected(string reason)
        => new(Clean: false, Scanned: true, reason);
}

/// <summary>
/// The seam a malware/content scanner plugs into.
///
/// Cloud Health Office has no scanning infrastructure today, and this PR does
/// NOT invent one: shipping a hand-rolled scanner would be worse than an honest
/// seam, because it would look like protection. What exists here is the call
/// site, on the path every submission takes before its bytes are written, and a
/// default implementation that scans nothing and says so at startup.
///
/// Deployment integration: register an implementation of this interface that
/// calls the engagement's scanning service (ICAP, Defender for Storage
/// on-upload, or a sidecar) in place of
/// <see cref="UnscannedAttachmentContentScanner"/>. A non-clean verdict refuses
/// the submission before anything is stored or recorded.
/// </summary>
public interface IAttachmentContentScanner
{
    Task<AttachmentScanResult> ScanAsync(
        ReadOnlyMemory<byte> content, string contentType, CancellationToken ct = default);
}

/// <summary>
/// The default scanner: it does not scan. Named for what it is so nobody reads
/// a passing verdict from it as evidence that content was checked.
/// </summary>
public sealed class UnscannedAttachmentContentScanner : IAttachmentContentScanner
{
    private readonly ILogger<UnscannedAttachmentContentScanner> _logger;
    private int _warned;

    public UnscannedAttachmentContentScanner(ILogger<UnscannedAttachmentContentScanner> logger)
        => _logger = logger;

    public Task<AttachmentScanResult> ScanAsync(
        ReadOnlyMemory<byte> content, string contentType, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _logger.LogWarning(
                "No attachment content scanner is registered — submitted documents are stored "
                + "without malware scanning. Register an IAttachmentContentScanner for deployment.");
        }

        return System.Threading.Tasks.Task.FromResult(AttachmentScanResult.NotScanned);
    }
}
