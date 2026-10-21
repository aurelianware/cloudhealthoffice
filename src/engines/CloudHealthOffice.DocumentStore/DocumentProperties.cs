namespace CloudHealthOffice.DocumentStore;

/// <summary>What the store knows about one stored document.</summary>
public sealed record DocumentProperties
{
    /// <summary>Stored size in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Content type recorded at upload, if any.</summary>
    public string? ContentType { get; init; }

    /// <summary>The document's tags (empty when it has none).</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// A pre-signed, write-only upload link for one document. The client sends
/// the bytes with <see cref="Method"/> to <see cref="Url"/>, including every
/// header in <see cref="Headers"/> exactly as given; the providers differ in
/// what they require (Azure needs <c>x-ms-blob-type</c>), so clients copy the
/// headers rather than knowing which store is behind the link.
/// </summary>
public sealed record DocumentUploadLink
{
    public required Uri Url { get; init; }

    /// <summary>HTTP method for the upload. Always PUT for the current providers.</summary>
    public string Method { get; init; } = "PUT";

    /// <summary>Headers the upload request must carry.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public required DateTimeOffset ExpiresAtUtc { get; init; }
}
