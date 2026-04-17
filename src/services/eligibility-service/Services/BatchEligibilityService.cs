using System.Globalization;
using System.Text;
using System.Text.Json;
using EligibilityService.Adapters;
using EligibilityService.Models;

namespace EligibilityService.Services;

public interface IBatchEligibilityService
{
    /// <summary>
    /// Accept a CSV or JSON payload (≤10,000 rows). Small batches (≤100) run
    /// inline; larger batches are queued and the caller polls
    /// GetJobAsync/GetResultAsync.
    /// </summary>
    Task<BatchEligibilityJob> SubmitAsync(
        string tenantId,
        Stream body,
        string contentType,
        CancellationToken ct = default);

    Task<BatchEligibilityJob?> GetJobAsync(string tenantId, string jobId, CancellationToken ct = default);

    Task<byte[]?> GetResultAsync(string tenantId, string jobId, CancellationToken ct = default);

    /// <summary>
    /// Drive the verification for a single job. Called inline for small
    /// batches and by the queue consumer for large ones.
    /// </summary>
    Task ProcessJobAsync(string tenantId, string jobId, CancellationToken ct = default);
}

public class BatchEligibilityService : IBatchEligibilityService
{
    public const int MaxRows = 10_000;
    public const int InlineThreshold = 100;

    private readonly IBatchJobStore _store;
    private readonly IBatchQueue _queue;
    private readonly EligibilityAdapterFactory _adapters;
    private readonly ILogger<BatchEligibilityService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public BatchEligibilityService(
        IBatchJobStore store,
        IBatchQueue queue,
        EligibilityAdapterFactory adapters,
        ILogger<BatchEligibilityService> logger)
    {
        _store = store;
        _queue = queue;
        _adapters = adapters;
        _logger = logger;
    }

    public async Task<BatchEligibilityJob> SubmitAsync(
        string tenantId,
        Stream body,
        string contentType,
        CancellationToken ct = default)
    {
        var rows = await ParseAsync(body, contentType, ct);
        if (rows.Count == 0)
            throw new ArgumentException("Batch payload contained zero parseable rows.");

        if (rows.Count > MaxRows)
            throw new ArgumentException($"Batch exceeds {MaxRows} row limit (got {rows.Count}).");

        var job = new BatchEligibilityJob
        {
            TenantId = tenantId,
            TotalRows = rows.Count,
            Status = BatchJobStatus.Queued
        };
        await _store.SaveAsync(job, ct);

        // Stash input rows alongside the job as CSV in the result slot-prefixed
        // with "INPUT::" so the processor can pick them up. Keeps the store
        // abstraction tiny.
        var inputCsv = SerializeRowsToCsv(rows);
        await _store.SaveResultAsync(
            tenantId, InputKey(job.Id), Encoding.UTF8.GetBytes(inputCsv), ct);

        if (rows.Count > InlineThreshold)
        {
            job.Queued = true;
            await _store.SaveAsync(job, ct);
            await _queue.EnqueueAsync(new BatchQueueMessage(tenantId, job.Id), ct);
            _logger.LogInformation(
                "Batch eligibility job {JobId} queued ({RowCount} rows > {Threshold})",
                job.Id, rows.Count, InlineThreshold);
        }
        else
        {
            // Small batches run synchronously while the request is still open;
            // the caller still gets a jobId and the same polling endpoint so
            // clients don't need to branch on size.
            await ProcessJobAsync(tenantId, job.Id, ct);
        }

        return (await _store.GetAsync(tenantId, job.Id, ct))!;
    }

    public Task<BatchEligibilityJob?> GetJobAsync(string tenantId, string jobId, CancellationToken ct = default)
        => _store.GetAsync(tenantId, jobId, ct);

    public Task<byte[]?> GetResultAsync(string tenantId, string jobId, CancellationToken ct = default)
        => _store.GetResultAsync(tenantId, jobId, ct);

    public async Task ProcessJobAsync(string tenantId, string jobId, CancellationToken ct = default)
    {
        var job = await _store.GetAsync(tenantId, jobId, ct);
        if (job == null)
        {
            _logger.LogWarning("ProcessJobAsync: job {JobId} not found for tenant {Tenant}",
                jobId, SanitizeForLog(tenantId));
            return;
        }

        if (job.Status == BatchJobStatus.Completed)
            return; // resumable: idempotent on completion

        var inputBytes = await _store.GetResultAsync(tenantId, InputKey(jobId), ct);
        if (inputBytes == null)
        {
            job.Status = BatchJobStatus.Failed;
            job.CompletedDate = DateTime.UtcNow;
            await _store.SaveAsync(job, ct);
            return;
        }

        job.Status = BatchJobStatus.Running;
        job.StartedDate ??= DateTime.UtcNow;
        await _store.SaveAsync(job, ct);

        var rows = ParseCsv(Encoding.UTF8.GetString(inputBytes));
        var adapter = await _adapters.GetAdapterAsync(tenantId, ct);
        var results = new List<BatchEligibilityResultRow>(rows.Count);

        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested)
            {
                job.Status = BatchJobStatus.Cancelled;
                break;
            }

            var resultRow = new BatchEligibilityResultRow
            {
                RowNumber = row.RowNumber,
                SubscriberId = row.Identifier,
                ServiceDate = row.ServiceDate
            };

            try
            {
                var response = await adapter.VerifyEligibilityAsync(new EligibilityAdapterRequest
                {
                    TenantId = tenantId,
                    SubscriberId = row.Identifier,
                    MemberId = row.MemberId,
                    ServiceDate = row.ServiceDate,
                    ServiceTypeCode = "30"
                }, ct);

                resultRow.IsEligible = response.IsEligible;
                resultRow.StatusCode = response.StatusCode;
                resultRow.PlanId = response.PlanId;
                resultRow.GroupNumber = response.GroupNumber;
                resultRow.CoverageLevel = response.CoverageLevel;
                resultRow.CoverageBeginDate = response.CoverageBeginDate;
                resultRow.CoverageEndDate = response.CoverageEndDate;

                if (response.IsEligible) job.SucceededRows++;
                else job.FailedRows++;
            }
            catch (Exception ex)
            {
                resultRow.IsEligible = false;
                resultRow.Error = ex.Message;
                job.FailedRows++;
                if (job.Errors.Count < 20)
                    job.Errors.Add(new BatchRowError
                    {
                        RowNumber = row.RowNumber,
                        SubscriberId = row.Identifier,
                        Message = ex.Message
                    });
            }

            results.Add(resultRow);
            job.ProcessedRows++;
        }

        var csv = SerializeResultsToCsv(results);
        await _store.SaveResultAsync(tenantId, jobId, Encoding.UTF8.GetBytes(csv), ct);

        if (job.Status != BatchJobStatus.Cancelled)
            job.Status = BatchJobStatus.Completed;
        job.CompletedDate = DateTime.UtcNow;
        job.ResultFileUrl = $"/api/v1/eligibility/batch/{job.Id}/result";
        await _store.SaveAsync(job, ct);
    }

    // ── Parsing ──────────────────────────────────────────────────────────

    private static async Task<List<BatchEligibilityRow>> ParseAsync(
        Stream body, string contentType, CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);

        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return ParseJson(text);
        }
        return ParseCsv(text);
    }

    internal static List<BatchEligibilityRow> ParseCsv(string text)
    {
        var rows = new List<BatchEligibilityRow>();
        if (string.IsNullOrWhiteSpace(text)) return rows;

        using var reader = new StringReader(text);
        string? header = reader.ReadLine();
        if (header == null) return rows;

        var cols = SplitCsvLine(header).Select(c => c.Trim().ToLowerInvariant()).ToList();
        var memberIdx = cols.IndexOf("memberid");
        var subIdx = cols.IndexOf("subscriberid");
        var dateIdx = cols.IndexOf("servicedate");
        if (memberIdx < 0 && subIdx < 0)
            throw new ArgumentException("CSV must include memberId or subscriberId column");
        if (dateIdx < 0)
            throw new ArgumentException("CSV must include serviceDate column");

        string? line;
        var rowNumber = 1;
        while ((line = reader.ReadLine()) != null)
        {
            rowNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var values = SplitCsvLine(line);

            var row = new BatchEligibilityRow { RowNumber = rowNumber };
            if (memberIdx >= 0 && memberIdx < values.Count)
                row.MemberId = values[memberIdx]?.Trim();
            if (subIdx >= 0 && subIdx < values.Count)
                row.SubscriberId = values[subIdx]?.Trim();

            if (dateIdx < values.Count &&
                DateTime.TryParse(values[dateIdx], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dt))
            {
                row.ServiceDate = dt.Date;
            }

            if (string.IsNullOrWhiteSpace(row.Identifier)) continue;
            rows.Add(row);
        }

        return rows;
    }

    internal static List<BatchEligibilityRow> ParseJson(string text)
    {
        var raw = JsonSerializer.Deserialize<List<BatchEligibilityRow>>(text, JsonOpts)
                  ?? new List<BatchEligibilityRow>();
        for (var i = 0; i < raw.Count; i++)
            raw[i].RowNumber = i + 2; // 1 = header analogue
        return raw.Where(r => !string.IsNullOrWhiteSpace(r.Identifier)).ToList();
    }

    private static List<string> SplitCsvLine(string line)
    {
        // Minimal CSV splitter: handles quoted fields with embedded commas.
        // Sufficient for the expected payload shape; if customers need the
        // full RFC 4180 behavior we can swap in CsvHelper later.
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '\"') { inQuotes = !inQuotes; continue; }
            if (ch == ',' && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    internal static string SerializeRowsToCsv(List<BatchEligibilityRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("rowNumber,memberId,subscriberId,serviceDate");
        foreach (var r in rows)
        {
            sb.Append(r.RowNumber).Append(',')
              .Append(r.MemberId).Append(',')
              .Append(r.SubscriberId).Append(',')
              .Append(r.ServiceDate.ToString("yyyy-MM-dd"))
              .Append('\n');
        }
        return sb.ToString();
    }

    private static string SerializeResultsToCsv(List<BatchEligibilityResultRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("rowNumber,subscriberId,serviceDate,isEligible,statusCode,planId,groupNumber,coverageLevel,coverageBeginDate,coverageEndDate,error");
        foreach (var r in rows)
        {
            sb.Append(r.RowNumber).Append(',')
              .Append(Esc(r.SubscriberId)).Append(',')
              .Append(r.ServiceDate.ToString("yyyy-MM-dd")).Append(',')
              .Append(r.IsEligible).Append(',')
              .Append(Esc(r.StatusCode)).Append(',')
              .Append(Esc(r.PlanId)).Append(',')
              .Append(Esc(r.GroupNumber)).Append(',')
              .Append(Esc(r.CoverageLevel)).Append(',')
              .Append(r.CoverageBeginDate?.ToString("yyyy-MM-dd")).Append(',')
              .Append(r.CoverageEndDate?.ToString("yyyy-MM-dd")).Append(',')
              .Append(Esc(r.Error))
              .Append('\n');
        }
        return sb.ToString();
    }

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('\"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    internal static string InputKey(string jobId) => $"INPUT::{jobId}";

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
