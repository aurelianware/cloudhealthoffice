using System.Text.Json.Serialization;

namespace FhirService.Models;

public class BulkExportRequest
{
    public string? Type { get; set; }
    public string? Since { get; set; }
    public string? OutputFormat { get; set; }
    public string? GroupId { get; set; }

    /// <summary>The kick-off URL as the client sent it (the manifest's <c>request</c>).</summary>
    public string? RequestUrl { get; set; }

    /// <summary>
    /// The validated caller (token <c>sub</c>, else client id) starting the
    /// export. Only this caller may poll, download or cancel it.
    /// </summary>
    public string? RequestedBy { get; set; }
}

/// <summary>One NDJSON file a completed export wrote.</summary>
public class BulkExportFile
{
    public string Type { get; set; } = string.Empty;

    /// <summary>The file's name in the download URL, e.g. <c>Patient-1.ndjson</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }
}

public class BulkExportJob
{
    public string JobId { get; set; } = string.Empty;
    public BulkExportStatus Status { get; set; } = BulkExportStatus.Accepted;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Request { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public List<string> ResourceTypes { get; set; } = new();
    public string? Since { get; set; }
    public BulkExportManifest? Manifest { get; set; }
    public string? ErrorMessage { get; set; }
    public int ProgressPercent { get; set; }

    /// <summary>See <see cref="BulkExportRequest.RequestedBy"/>.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>The files a completed export wrote; the manifest lists them.</summary>
    public List<BulkExportFile> Files { get; set; } = new();

    /// <summary>When a completed export's files stop being served (the manifest's <c>Expires</c>).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The instant the export's data was read as of (the manifest's <c>transactionTime</c>).</summary>
    public DateTimeOffset? TransactionTime { get; set; }
}

public enum BulkExportStatus
{
    Accepted,
    InProgress,
    Complete,
    Error,
    Cancelled,
}

public class BulkExportManifest
{
    [JsonPropertyName("transactionTime")]
    public string TransactionTime { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("request")]
    public string Request { get; set; } = string.Empty;

    [JsonPropertyName("requiresAccessToken")]
    public bool RequiresAccessToken { get; set; } = true;

    [JsonPropertyName("output")]
    public List<BulkExportOutput> Output { get; set; } = new();

    [JsonPropertyName("error")]
    public List<BulkExportOutput> Error { get; set; } = new();
}

public class BulkExportOutput
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
