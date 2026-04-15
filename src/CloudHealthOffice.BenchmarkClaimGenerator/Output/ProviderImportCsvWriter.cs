using System.Text;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Output;

/// <summary>
/// Generates CSV files for provider-service import endpoint consumption.
/// Produces separate files for individual and organizational providers.
/// </summary>
public class ProviderImportCsvWriter
{
    private readonly string _outputDirectory;
    private readonly ILogger _logger;

    /// <summary>Header row for provider import CSV.</summary>
    public const string ProviderCsvHeader =
        "NPI,TaxId,ProviderType,FirstName,LastName,OrganizationName,Taxonomy,Specialty," +
        "NetworkStatus,CredentialingStatus,Address1,City,State,Zip,Phone,EffectiveDate,TermDate," +
        "ContractType,FeeScheduleId";

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderImportCsvWriter"/> class.
    /// </summary>
    /// <param name="outputDirectory">Directory for output CSV files.</param>
    /// <param name="logger">Optional logger.</param>
    public ProviderImportCsvWriter(string outputDirectory, ILogger? logger = null)
    {
        _outputDirectory = outputDirectory;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Write provider CSV files, split by provider type.
    /// </summary>
    /// <param name="providers">All synthetic providers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of generated file paths.</returns>
    public async Task<List<string>> WriteAsync(
        List<SyntheticProvider> providers,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);
        var filePaths = new List<string>();

        var individual = providers.Where(p => p.ProviderType == "Individual").ToList();
        var organizational = providers.Where(p => p.ProviderType == "Organization").ToList();

        // Write individual providers
        var indFilePath = Path.Combine(_outputDirectory, "MCC-providers-0001.csv");
        await WriteProviderCsvAsync(indFilePath, individual, cancellationToken);
        filePaths.Add(indFilePath);
        _logger.LogInformation("Wrote {Count:N0} individual providers to {File}", individual.Count, indFilePath);

        // Write organizational providers
        var orgFilePath = Path.Combine(_outputDirectory, "MCC-providers-0002.csv");
        await WriteProviderCsvAsync(orgFilePath, organizational, cancellationToken);
        filePaths.Add(orgFilePath);
        _logger.LogInformation("Wrote {Count:N0} organizational providers to {File}", organizational.Count, orgFilePath);

        return filePaths;
    }

    private static async Task WriteProviderCsvAsync(
        string filePath,
        List<SyntheticProvider> providers,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(providers.Count * 256);
        sb.AppendLine(ProviderCsvHeader);

        foreach (var p in providers)
        {
            sb.Append(EscapeCsv(p.Npi)); sb.Append(',');
            sb.Append(EscapeCsv(p.TaxId)); sb.Append(',');
            sb.Append(EscapeCsv(p.ProviderType)); sb.Append(',');
            sb.Append(EscapeCsv(p.FirstName)); sb.Append(',');
            sb.Append(EscapeCsv(p.LastName)); sb.Append(',');
            sb.Append(EscapeCsv(p.OrganizationName ?? "")); sb.Append(',');
            sb.Append(EscapeCsv(p.TaxonomyCode)); sb.Append(',');
            sb.Append(EscapeCsv(p.SpecialtyDescription)); sb.Append(',');
            sb.Append(EscapeCsv(p.NetworkStatus)); sb.Append(',');
            sb.Append(EscapeCsv(p.CredentialingStatus)); sb.Append(',');
            sb.Append(EscapeCsv(p.Address)); sb.Append(',');
            sb.Append(EscapeCsv(p.City)); sb.Append(',');
            sb.Append(EscapeCsv(p.State)); sb.Append(',');
            sb.Append(EscapeCsv(p.ZipCode)); sb.Append(',');
            sb.Append(EscapeCsv(p.Phone ?? "")); sb.Append(',');
            sb.Append(p.EffectiveDate.ToString("yyyy-MM-dd")); sb.Append(',');
            sb.Append(p.TermDate?.ToString("yyyy-MM-dd") ?? ""); sb.Append(',');
            sb.Append(EscapeCsv(p.ContractType)); sb.Append(',');
            sb.Append(EscapeCsv(p.FeeScheduleId ?? ""));
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), cancellationToken);
    }

    /// <summary>
    /// Escape a value for CSV output. Wraps in quotes if the value contains
    /// commas, quotes, or newlines.
    /// </summary>
    internal static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
