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
    public const int ProcessingChunkSize = 100;

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
        var isJson = !string.IsNullOrEmpty(contentType) &&
                     contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

        // Buffer the incoming request into memory to count rows and decide
        // inline vs queued. We can't stream before we know the row count.
        // Reading into a string is bounded by ASP.NET Core's request-size
        // limits; anything larger than that gets rejected before we see it.
        string text;
        using (var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(ct);
        }

        var job = new BatchEligibilityJob
        {
            TenantId = tenantId,
            Status = BatchJobStatus.Queued
        };

        int totalRows;
        if (isJson)
        {
            var rows = ParseJson(text);
            totalRows = rows.Count;
            ValidateRowCount(totalRows);
            job.TotalRows = totalRows;
            await _store.SaveAsync(job, ct);
            await PersistInputRowsAsync(tenantId, job.Id, rows, queued: totalRows > InlineThreshold, ct);
        }
        else
        {
            // CSV path: stream-parse once to count + normalize straight into storage.
            totalRows = await PersistInputStreamAsync(tenantId, job.Id, text,
                queued: /*decided after count*/ false, validateOnly: true, ct);
            ValidateRowCount(totalRows);
            job.TotalRows = totalRows;
            await _store.SaveAsync(job, ct);
            // Second pass writes the normalized CSV. For inline we buffer; for
            // queued we stream to the store (blob if available).
            await PersistInputStreamAsync(tenantId, job.Id, text,
                queued: totalRows > InlineThreshold, validateOnly: false, ct);
        }

        if (totalRows > InlineThreshold)
        {
            job.Queued = true;
            await _store.SaveAsync(job, ct);
            await _queue.EnqueueAsync(new BatchQueueMessage(tenantId, job.Id), ct);
            _logger.LogInformation(
                "Batch eligibility job {JobId} queued ({RowCount} rows > {Threshold})",
                job.Id, totalRows, InlineThreshold);
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

        if (job.Status == BatchJobStatus.Completed ||
            job.Status == BatchJobStatus.Cancelled)
        {
            return;
        }

        var inputStream = await _store.OpenResultStreamAsync(tenantId, InputKey(jobId), ct);
        if (inputStream == null)
        {
            job.Status = BatchJobStatus.Failed;
            job.CompletedDate = DateTime.UtcNow;
            await _store.SaveAsync(job, ct);
            return;
        }

        job.Status = BatchJobStatus.Running;
        job.StartedDate ??= DateTime.UtcNow;
        job.ProcessedRows = 0;
        job.SucceededRows = 0;
        job.FailedRows = 0;
        job.Errors.Clear();
        await _store.SaveAsync(job, ct);

        var adapter = await _adapters.GetAdapterAsync(tenantId, ct);

        // Stream input → verify in fixed-size chunks → stream output.
        // Nothing proportional to row count stays in memory.
        using var resultBuffer = new MemoryStream();
        using (inputStream)
        await using (var resultWriter = new StreamingCsvWriter(
            new StreamWriter(resultBuffer, Encoding.UTF8, leaveOpen: true)))
        {
            await resultWriter.WriteHeaderAsync(StreamingCsvWriter.ResultHeader, ct);

            using var inputReader = new StreamReader(inputStream, Encoding.UTF8);
            var chunk = new List<BatchEligibilityRow>(ProcessingChunkSize);

            await foreach (var row in StreamingCsvParser.ParseAsync(inputReader, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    job.Status = BatchJobStatus.Cancelled;
                    break;
                }
                chunk.Add(row);
                if (chunk.Count >= ProcessingChunkSize)
                {
                    await ProcessChunkAsync(chunk, tenantId, adapter, job, resultWriter, ct);
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0 && job.Status != BatchJobStatus.Cancelled)
            {
                await ProcessChunkAsync(chunk, tenantId, adapter, job, resultWriter, ct);
                chunk.Clear();
            }

            await resultWriter.FlushAsync(ct);
        }

        resultBuffer.Position = 0;
        await _store.SaveResultStreamAsync(tenantId, jobId, resultBuffer, ct);

        if (job.Status != BatchJobStatus.Cancelled)
            job.Status = BatchJobStatus.Completed;
        job.CompletedDate = DateTime.UtcNow;
        job.ResultFileUrl = $"/api/v1/eligibility/batch/{job.Id}/result";
        await _store.SaveAsync(job, ct);
    }

    private async Task ProcessChunkAsync(
        List<BatchEligibilityRow> chunk,
        string tenantId,
        IEligibilityAdapter adapter,
        BatchEligibilityJob job,
        StreamingCsvWriter writer,
        CancellationToken ct)
    {
        foreach (var row in chunk)
        {
            if (ct.IsCancellationRequested)
            {
                job.Status = BatchJobStatus.Cancelled;
                return;
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

            await writer.WriteResultRowAsync(resultRow, ct);
            job.ProcessedRows++;
        }
    }

    // ── Submission-time persistence ──────────────────────────────────────

    /// <summary>
    /// Streams CSV text through the parser and, unless <paramref name="validateOnly"/>
    /// is true, writes the normalized input CSV to the store. Returns the
    /// validated row count.
    /// </summary>
    private async Task<int> PersistInputStreamAsync(
        string tenantId, string jobId, string text, bool queued, bool validateOnly, CancellationToken ct)
    {
        var count = 0;
        using var textReader = new StringReader(text);

        if (validateOnly)
        {
            await foreach (var _ in StreamingCsvParser.ParseAsync(textReader, ct))
                count++;
            return count;
        }

        using var memory = new MemoryStream();
        await using (var writer = new StreamingCsvWriter(
            new StreamWriter(memory, Encoding.UTF8, leaveOpen: true)))
        {
            await writer.WriteHeaderAsync(StreamingCsvWriter.InputHeader, ct);
            await foreach (var row in StreamingCsvParser.ParseAsync(textReader, ct))
            {
                await writer.WriteInputRowAsync(row, ct);
                count++;
            }
            await writer.FlushAsync(ct);
        }

        memory.Position = 0;
        if (queued)
            await _store.SaveResultStreamAsync(tenantId, InputKey(jobId), memory, ct);
        else
            await _store.SaveResultAsync(tenantId, InputKey(jobId), memory.ToArray(), ct);

        return count;
    }

    private async Task PersistInputRowsAsync(
        string tenantId, string jobId, List<BatchEligibilityRow> rows, bool queued, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await using (var writer = new StreamingCsvWriter(
            new StreamWriter(memory, Encoding.UTF8, leaveOpen: true)))
        {
            await writer.WriteHeaderAsync(StreamingCsvWriter.InputHeader, ct);
            foreach (var row in rows)
                await writer.WriteInputRowAsync(row, ct);
            await writer.FlushAsync(ct);
        }
        memory.Position = 0;

        if (queued)
            await _store.SaveResultStreamAsync(tenantId, InputKey(jobId), memory, ct);
        else
            await _store.SaveResultAsync(tenantId, InputKey(jobId), memory.ToArray(), ct);
    }

    private static void ValidateRowCount(int totalRows)
    {
        if (totalRows == 0)
            throw new ArgumentException("Batch payload contained zero parseable rows.");
        if (totalRows > MaxRows)
            throw new ArgumentException($"Batch exceeds {MaxRows} row limit (got {totalRows}).");
    }

    // ── JSON parsing (unchanged) ─────────────────────────────────────────

    internal static List<BatchEligibilityRow> ParseJson(string text)
    {
        var raw = JsonSerializer.Deserialize<List<BatchEligibilityRow>>(text, JsonOpts)
                  ?? new List<BatchEligibilityRow>();
        for (var i = 0; i < raw.Count; i++)
        {
            raw[i].RowNumber = i + 2;
            if (raw[i].ServiceDate == default)
                throw new ArgumentException(
                    $"Row {raw[i].RowNumber}: serviceDate missing or not a valid ISO-8601 date.");
        }
        return raw.Where(r => !string.IsNullOrWhiteSpace(r.Identifier)).ToList();
    }

    // ── Legacy ParseCsv kept internal for any callers / tests still using it.
    // New code uses StreamingCsvParser.

    internal static List<BatchEligibilityRow> ParseCsv(string text)
    {
        var rows = new List<BatchEligibilityRow>();
        if (string.IsNullOrWhiteSpace(text)) return rows;
        using var reader = new StringReader(text);
        foreach (var row in StreamingCsvParser
                    .ParseAsync(reader, CancellationToken.None)
                    .ToBlockingEnumerable())
        {
            rows.Add(row);
        }
        return rows;
    }

    internal static string InputKey(string jobId) => $"INPUT::{jobId}";

    private static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
