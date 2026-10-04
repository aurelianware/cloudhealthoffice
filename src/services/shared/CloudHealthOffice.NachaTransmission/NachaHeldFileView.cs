using System.Text.Json.Serialization;

namespace CloudHealthOffice.NachaTransmission;

/// <summary>A held NACHA file as an API shows it: what it is and why it waits. Never the file.</summary>
public sealed class NachaHeldFileView
{
    [JsonPropertyName("fileReference")] public string FileReference { get; init; } = string.Empty;
    [JsonPropertyName("fileName")] public string FileName { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("reason")] public string Reason { get; init; } = string.Empty;
    [JsonPropertyName("entryCount")] public int EntryCount { get; init; }
    [JsonPropertyName("totalDebitAmount")] public decimal TotalDebitAmount { get; init; }
    [JsonPropertyName("totalCreditAmount")] public decimal TotalCreditAmount { get; init; }
    [JsonPropertyName("byteSize")] public long ByteSize { get; init; }
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = string.Empty;
    [JsonPropertyName("runId")] public string? RunId { get; init; }
    [JsonPropertyName("releasedBy")] public string ReleasedBy { get; init; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; }
    [JsonPropertyName("expiresAt")] public DateTime ExpiresAt { get; init; }
    [JsonPropertyName("attempts")] public int Attempts { get; init; }
    [JsonPropertyName("lastAttemptBy")] public string? LastAttemptBy { get; init; }
    [JsonPropertyName("retrievalCount")] public int RetrievalCount { get; init; }

    public static NachaHeldFileView From(NachaHeldFile f) => new()
    {
        FileReference = f.FileReference,
        FileName = f.FileName,
        Status = f.Status.ToString(),
        Reason = f.Reason,
        EntryCount = f.EntryCount,
        TotalDebitAmount = f.TotalDebitAmount,
        TotalCreditAmount = f.TotalCreditAmount,
        ByteSize = f.ByteSize,
        Sha256 = f.Sha256,
        RunId = f.RunId,
        ReleasedBy = f.ReleasedBy,
        CreatedAt = f.CreatedAt,
        ExpiresAt = f.ExpiresAt,
        Attempts = f.Attempts,
        LastAttemptBy = f.LastAttemptBy,
        RetrievalCount = f.Retrievals.Count,
    };
}

/// <summary>Body of a platform admin's retrieval: why they need the file.</summary>
public sealed class RetrieveNachaFileRequest
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}
