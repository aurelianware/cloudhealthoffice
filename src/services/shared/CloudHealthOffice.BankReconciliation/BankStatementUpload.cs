using Microsoft.AspNetCore.Http;

namespace CloudHealthOffice.BankReconciliation;

/// <summary>What a BAI2 upload answers.</summary>
public sealed class BankStatementUploadResult
{
    public BankStatementIngestStatus IngestStatus { get; init; }

    /// <summary>The stored file (no content; accounts masked).</summary>
    public BankStatementFile Statement { get; init; } = new();

    public IReadOnlyList<string> Errors { get; init; } = [];

    public int Matched { get; init; }
    public int Applied { get; init; }
    public int Suspense { get; init; }
    public int Returns { get; init; }
    public int Ignored { get; init; }
    public int AlreadyDecided { get; init; }
    public int InProgress { get; init; }

    /// <summary>The decision for each transaction this service reconciled.</summary>
    public IReadOnlyList<ReconciliationItem> Items { get; init; } = [];

    public static BankStatementUploadResult From(BankStatementIngestResult ingest, BankReconciliationSummary? summary) => new()
    {
        IngestStatus = ingest.Status,
        Statement = ingest.Statement,
        Errors = ingest.Errors,
        Matched = summary?.Matched ?? 0,
        Applied = summary?.Applied ?? 0,
        Suspense = summary?.Suspense ?? 0,
        Returns = summary?.Returns ?? 0,
        Ignored = summary?.Ignored ?? 0,
        AlreadyDecided = summary?.AlreadyDecided ?? 0,
        InProgress = summary?.InProgress ?? 0,
        Items = summary?.Items ?? [],
    };
}

public static class BankStatementUpload
{
    /// <summary>The request body, or null when it is larger than <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
