using System.Text.Json.Serialization;

namespace FhirService.Models;

public class BulkExportRequest
{
    public string? Type { get; set; }
    public string? Since { get; set; }
    public string? OutputFormat { get; set; }
    public string? GroupId { get; set; }
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
