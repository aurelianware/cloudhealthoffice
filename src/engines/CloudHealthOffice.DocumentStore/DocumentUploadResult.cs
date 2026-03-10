namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// Metadata returned after a successful document upload.
/// </summary>
public record DocumentUploadResult
{
    /// <summary>Canonical URI of the stored blob.</summary>
    public required Uri Uri { get; init; }

    /// <summary>Container (bucket) the blob was stored in.</summary>
    public required string Container { get; init; }

    /// <summary>Blob name / path within the container.</summary>
    public required string BlobName { get; init; }

    /// <summary>Number of bytes written.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>SHA-256 hex digest of the uploaded content, if computed by the provider.</summary>
    public string? ContentHash { get; init; }
}
