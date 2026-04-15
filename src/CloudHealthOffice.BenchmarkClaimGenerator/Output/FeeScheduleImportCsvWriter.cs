using System.Text;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Generates CSV files for fee schedule import into the FeeScheduleEngine.
/// </summary>
public class FeeScheduleImportCsvWriter
{
    private readonly string _outputDirectory;
    private readonly ILogger _logger;

    /// <summary>Header row for fee schedule import CSV.</summary>
    public const string FeeScheduleCsvHeader =
        "FeeScheduleId,ProcedureCode,Modifier,PlaceOfService,AllowedAmount,EffectiveDate,TermDate";

    /// <summary>
    /// Initializes a new instance of the <see cref="FeeScheduleImportCsvWriter"/> class.
    /// </summary>
    /// <param name="outputDirectory">Directory for output CSV files.</param>
    /// <param name="logger">Optional logger.</param>
    public FeeScheduleImportCsvWriter(string outputDirectory, ILogger? logger = null)
    {
        _outputDirectory = outputDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Write fee schedule CSV files, one per fee schedule.
    /// </summary>
    /// <param name="feeSchedules">All synthetic fee schedules.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of generated file paths.</returns>
    public async Task<List<string>> WriteAsync(
        List<SyntheticFeeSchedule> feeSchedules,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);
        var filePaths = new List<string>();

        foreach (var fs in feeSchedules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = $"MCC-feeschedule-{fs.FeeScheduleId.Replace("FS-", "").ToLowerInvariant()}.csv";
            var filePath = Path.Combine(_outputDirectory, fileName);

            var sb = new StringBuilder(fs.Lines.Count * 128);
            sb.AppendLine(FeeScheduleCsvHeader);

            foreach (var line in fs.Lines)
            {
                sb.Append(EscapeCsv(fs.FeeScheduleId)); sb.Append(',');
                sb.Append(EscapeCsv(line.ProcedureCode)); sb.Append(',');
                sb.Append(EscapeCsv(line.Modifier ?? "")); sb.Append(',');
                sb.Append(EscapeCsv(line.PlaceOfService ?? "")); sb.Append(',');
                sb.Append(line.AllowedAmount.ToString("F2")); sb.Append(',');
                sb.Append(line.EffectiveDate.ToString("yyyy-MM-dd")); sb.Append(',');
                sb.Append(line.TermDate?.ToString("yyyy-MM-dd") ?? "");
                sb.AppendLine();
            }

            await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
            filePaths.Add(filePath);
            _logger.LogInformation("Wrote fee schedule {Id} with {Lines} lines to {File}",
                fs.FeeScheduleId, fs.Lines.Count, filePath);
        }

        return filePaths;
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
