namespace CloudHealthOffice.DocumentStore;

/// <summary>
/// Object naming for the bucket-backed providers (Google Cloud Storage, S3).
///
/// Bucket names are global across all customers of a cloud, so a deployment
/// gets one bucket and each CHO container becomes the first path segment:
/// container "member-documents", name "tenants/t1/members/m1/doc.pdf" is
/// object "member-documents/tenants/t1/members/m1/doc.pdf". A container name
/// is a single segment, so one container's prefix can never reach into
/// another's.
/// </summary>
internal static class DocumentKeys
{
    public static string ObjectName(string container, string blobName)
    {
        if (string.IsNullOrWhiteSpace(container) || container.Contains('/') || container is "." or "..")
            throw new ArgumentException("A container is one non-empty path segment.", nameof(container));
        if (string.IsNullOrWhiteSpace(blobName) || blobName.StartsWith('/'))
            throw new ArgumentException("A document name is non-empty and relative.", nameof(blobName));
        if (blobName.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("A document name may not contain '.' or '..' segments.", nameof(blobName));

        return $"{container}/{blobName}";
    }

    // Local copy on purpose: this library pins Microsoft.Extensions.*.Abstractions
    // to 10.x (VersionOverride) while CloudHealthOffice.Infrastructure builds on
    // 8.x, so it does not reference Infrastructure. Every other project uses
    // CloudHealthOffice.Infrastructure.Logging.LogSanitizer.Sanitize.
    public static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    public static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? tags) =>
        tags is null ? new Dictionary<string, string>() : new Dictionary<string, string>(tags);
}
