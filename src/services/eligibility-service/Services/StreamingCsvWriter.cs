using System.Text;
using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Line-at-a-time CSV writer used by the queued batch path. Owns no state
/// beyond the underlying <see cref="TextWriter"/>; callers write rows as
/// results are produced and the writer flushes per-line so peak memory
/// stays bounded regardless of batch size.
/// </summary>
public sealed class StreamingCsvWriter : IAsyncDisposable
{
    private readonly TextWriter _writer;
    private bool _headerWritten;

    public StreamingCsvWriter(TextWriter writer)
    {
        _writer = writer;
    }

    public static readonly string InputHeader =
        "rowNumber,memberId,subscriberId,serviceDate";

    public static readonly string ResultHeader =
        "rowNumber,subscriberId,serviceDate,isEligible,statusCode," +
        "planId,groupNumber,coverageLevel,coverageBeginDate,coverageEndDate,error";

    public async Task WriteHeaderAsync(string header, CancellationToken ct = default)
    {
        await _writer.WriteLineAsync(header.AsMemory(), ct);
        _headerWritten = true;
    }

    public async Task WriteInputRowAsync(BatchEligibilityRow row, CancellationToken ct = default)
    {
        if (!_headerWritten) await WriteHeaderAsync(InputHeader, ct);

        var sb = new StringBuilder(64);
        sb.Append(row.RowNumber).Append(',')
          .Append(Esc(row.MemberId)).Append(',')
          .Append(Esc(row.SubscriberId)).Append(',')
          .Append(row.ServiceDate.ToString("yyyy-MM-dd"));
        await _writer.WriteLineAsync(sb.ToString().AsMemory(), ct);
    }

    public async Task WriteResultRowAsync(BatchEligibilityResultRow row, CancellationToken ct = default)
    {
        if (!_headerWritten) await WriteHeaderAsync(ResultHeader, ct);

        var sb = new StringBuilder(128);
        sb.Append(row.RowNumber).Append(',')
          .Append(Esc(row.SubscriberId)).Append(',')
          .Append(row.ServiceDate.ToString("yyyy-MM-dd")).Append(',')
          .Append(row.IsEligible).Append(',')
          .Append(Esc(row.StatusCode)).Append(',')
          .Append(Esc(row.PlanId)).Append(',')
          .Append(Esc(row.GroupNumber)).Append(',')
          .Append(Esc(row.CoverageLevel)).Append(',')
          .Append(row.CoverageBeginDate?.ToString("yyyy-MM-dd")).Append(',')
          .Append(row.CoverageEndDate?.ToString("yyyy-MM-dd")).Append(',')
          .Append(Esc(row.Error));
        await _writer.WriteLineAsync(sb.ToString().AsMemory(), ct);
    }

    public async Task FlushAsync(CancellationToken ct = default)
        => await _writer.FlushAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _writer.FlushAsync();
        await _writer.DisposeAsync();
    }

    internal static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
