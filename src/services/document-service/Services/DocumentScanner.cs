using DocumentService.Models;

namespace DocumentService.Services;

/// <summary>
/// Malware scanning seam (docs/architecture/document-store.md §4, §10 question 2).
/// A deployment registers a real scanner (ClamAV in the cluster, Microsoft
/// Defender for Storage, GuardDuty Malware Protection for S3, or ClamAV on
/// Cloud Run beside a GCS bucket). Content is never served while the scan says
/// Infected or Failed; Pending content is served only where
/// <c>Documents:AllowUnscannedContent</c> is on.
/// </summary>
public interface IDocumentScanner
{
    Task<DocumentScan> ScanAsync(Stream content, string contentType, CancellationToken ct);
}

/// <summary>
/// The default: scans nothing and says so. Documents stay Pending, which is
/// honest: nobody has looked at the bytes.
/// </summary>
public sealed class NoDocumentScanner : IDocumentScanner
{
    public const string EngineName = "none";

    public Task<DocumentScan> ScanAsync(Stream content, string contentType, CancellationToken ct) =>
        Task.FromResult(new DocumentScan { Status = ScanStatus.Pending, Engine = EngineName, At = null });
}

public sealed class DocumentServiceOptions
{
    public const string SectionName = "Documents";

    /// <summary>
    /// Serve content no scanner has passed (Pending). Off by default: with no
    /// scanner configured, a deployment that serves content does so knowingly.
    /// Infected and Failed are never served, whatever this says.
    /// </summary>
    public bool AllowUnscannedContent { get; set; }

    /// <summary>Lifetime of a pre-signed upload link.</summary>
    public TimeSpan UploadLinkLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
