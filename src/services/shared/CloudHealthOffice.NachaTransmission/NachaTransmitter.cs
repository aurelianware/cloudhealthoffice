using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>A generated NACHA file on its way to the tenant's bank. Never serialized to a response.</summary>
public sealed class NachaTransmissionRequest
{
    public required string TenantId { get; init; }

    /// <summary>The file reference the drafts or disbursements carry (NACHA-XXXXXXXX).</summary>
    public required string FileReference { get; init; }

    /// <summary>The name the bank receives the file under.</summary>
    public required string FileName { get; init; }

    /// <summary>The NACHA file. Holds full routing and account numbers.</summary>
    public required string Content { get; init; }

    /// <summary>Billing run / capitation run, when the file came from one.</summary>
    public string? RunId { get; init; }

    /// <summary>The batch request or release this file belongs to.</summary>
    public string? BatchId { get; init; }

    /// <summary>Token subject of the user who released (or retried) the payment.</summary>
    public required string TransmittedBy { get; init; }
}

/// <summary>
/// What an approver sees after a file went to the bank: no account numbers,
/// no file content.
/// </summary>
public sealed class NachaTransmissionReceipt
{
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("fileReference")] public string FileReference { get; set; } = string.Empty;
    [JsonPropertyName("remoteFileName")] public string RemoteFileName { get; set; } = string.Empty;
    /// <summary>Host and directory (never credentials).</summary>
    [JsonPropertyName("destination")] public string Destination { get; set; } = string.Empty;
    [JsonPropertyName("byteSize")] public long ByteSize { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
    [JsonPropertyName("totalDebitAmount")] public decimal TotalDebitAmount { get; set; }
    [JsonPropertyName("totalCreditAmount")] public decimal TotalCreditAmount { get; set; }
    [JsonPropertyName("transmittedAt")] public DateTime TransmittedAt { get; set; }
    [JsonPropertyName("transmittedBy")] public string TransmittedBy { get; set; } = string.Empty;
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("batchId")] public string? BatchId { get; set; }
}

/// <summary>
/// The file did not reach the bank. <see cref="Exception.Message"/> is safe to
/// show and log: it never holds a credential, a secret name's value or file content.
/// </summary>
public sealed class NachaTransmissionException : Exception
{
    public NachaTransmissionException(string message, bool notConfigured = false, bool deliveryUnknown = false) : base(message)
    {
        NotConfigured = notConfigured;
        DeliveryUnknown = deliveryUnknown;
    }

    /// <summary>The tenant has no usable transmission configuration (as opposed to a failed attempt).</summary>
    public bool NotConfigured { get; }

    /// <summary>
    /// The file may have reached the bank (the upload finished but the rename's
    /// outcome could not be established, or a file of that name is already in
    /// the drop). Sending it again could pay twice: verify with the bank first.
    /// </summary>
    public bool DeliveryUnknown { get; }
}

/// <summary>Sends a NACHA file to the tenant's bank.</summary>
public interface INachaTransmitter
{
    /// <summary>
    /// Uploads atomically (temporary name, then rename) and returns the receipt,
    /// or throws <see cref="NachaTransmissionException"/>.
    /// </summary>
    Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default);
}

internal static class NachaFileNames
{
    private static readonly Regex Safe = new("^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    /// <summary>A file name the bank drop accepts: no path, no hidden file.</summary>
    public static string Require(string fileName)
    {
        if (!Safe.IsMatch(fileName) || fileName.StartsWith('.') || fileName.Contains("..", StringComparison.Ordinal))
            throw new NachaTransmissionException("The NACHA file name is not a plain file name.");
        return fileName;
    }

    /// <summary>The temporary name a file is written under before it is renamed into place.</summary>
    public static string Temporary(string fileName) => $".{fileName}.{Guid.NewGuid():N}.part";

    public static NachaTransmissionReceipt Receipt(
        NachaTransmissionRequest request, NachaFileFacts facts, string remoteFileName, string destination, DateTime at)
        => new()
        {
            TenantId = request.TenantId,
            FileReference = request.FileReference,
            RemoteFileName = remoteFileName,
            Destination = destination,
            ByteSize = facts.ByteSize,
            Sha256 = facts.Sha256,
            EntryCount = facts.EntryCount,
            TotalDebitAmount = facts.TotalDebitAmount,
            TotalCreditAmount = facts.TotalCreditAmount,
            TransmittedAt = at,
            TransmittedBy = request.TransmittedBy,
            RunId = request.RunId,
            BatchId = request.BatchId,
        };
}
