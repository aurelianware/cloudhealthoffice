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
    public NachaTransmissionException(string message, bool notConfigured = false, bool deliveryUnknown = false, bool hostKeyRejected = false) : base(message)
    {
        NotConfigured = notConfigured;
        DeliveryUnknown = deliveryUnknown;
        HostKeyRejected = hostKeyRejected;
    }

    /// <summary>
    /// The server presented an SSH host key that is not the pinned one (or none
    /// was verified). Nothing was authenticated or sent. A security event (the
    /// bank changed its key, or something is in the path): never re-pin
    /// automatically; confirm the new key with the bank out of band.
    /// </summary>
    public bool HostKeyRejected { get; }

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

/// <summary>
/// TODO(PGP): encryption of the file for banks that require OpenPGP on top of
/// SFTP. Not implemented and not wired: the repository has no OpenPGP library or
/// key-handling pattern yet. When added, the transmitter calls it after the
/// approved-hash check and before the upload, with the bank's public key read
/// from Key Vault (<c>nacha--{tenantId}--pgp-public-key</c>); receipts keep the
/// SHA-256 of the clear file (what was approved) and add the encrypted bytes'
/// hash and size, and reconciliation compares the encrypted size.
/// See docs/operations/NACHA-BANK-TRANSMISSION-RUNBOOK.md, section 9.
/// </summary>
public interface INachaFileEncryptor
{
    /// <summary>The bytes to upload for <paramref name="clearFile"/>, and the name suffix (e.g. ".pgp").</summary>
    Task<(byte[] Encrypted, string FileNameSuffix)> EncryptAsync(string tenantId, byte[] clearFile, CancellationToken cancellationToken = default);
}

/// <summary>What a look at the bank's drop found for one file name.</summary>
public enum NachaRemoteFilePresence
{
    /// <summary>A file of that name and of the expected byte size is in the drop.</summary>
    Present,

    /// <summary>
    /// No file of that name is in the drop. Not proof it never arrived: many
    /// banks move a file out of the drop once they collect it.
    /// </summary>
    Absent,

    /// <summary>A file of that name is there, but not of the expected size: proof of nothing.</summary>
    DifferentSize,
}

/// <summary>The answer of <see cref="INachaRemoteFileProbe.CheckAsync"/>. Never carries file content.</summary>
public sealed class NachaRemoteFileCheck
{
    public required NachaRemoteFilePresence Presence { get; init; }

    /// <summary>Host and directory (never credentials).</summary>
    public required string Destination { get; init; }

    /// <summary>The remote file's size, when it is there and the server reported one.</summary>
    public long? RemoteByteSize { get; init; }
}

/// <summary>
/// Looks in the tenant's bank drop for one file name, read-only (nothing is
/// written, renamed or deleted). Used to reconcile a transmission whose outcome
/// is unknown before anything could be sent again. Same settings, pinned host
/// key and credentials as <see cref="INachaTransmitter"/>.
/// </summary>
public interface INachaRemoteFileProbe
{
    /// <summary>
    /// Whether <paramref name="fileName"/> is in the drop with
    /// <paramref name="expectedByteSize"/> bytes. Throws
    /// <see cref="NachaTransmissionException"/> when the drop cannot be reached or listed.
    /// </summary>
    Task<NachaRemoteFileCheck> CheckAsync(string tenantId, string fileName, long expectedByteSize, CancellationToken cancellationToken = default);
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
