using System.Text;
using PremiumBillingService.Models;

namespace PremiumBillingService.Services;

/// <summary>
/// Generates NACHA (National Automated Clearing House Association) formatted ACH files
/// for batch EFT premium drafts. These files are submitted to the originating bank
/// for processing through the ACH network.
/// </summary>
public interface INachaFileService
{
    /// <summary>
    /// Generate a NACHA file for a batch of EFT drafts
    /// </summary>
    NachaFileResult GenerateNachaFile(List<NachaEntryDetail> entries, NachaFileOptions options);
}

public class NachaFileService : INachaFileService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<NachaFileService> _logger;

    public NachaFileService(IConfiguration configuration, ILogger<NachaFileService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public NachaFileResult GenerateNachaFile(List<NachaEntryDetail> entries, NachaFileOptions options)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException("No entries to include in NACHA file");

        var sb = new StringBuilder();
        var fileCreationDate = DateTime.UtcNow;
        var fileIdModifier = options.FileIdModifier ?? "A";

        // File Header Record (Type 1)
        sb.AppendLine(BuildFileHeader(options, fileCreationDate, fileIdModifier));

        // Batch Header Record (Type 5)
        var batchNumber = 1;
        sb.AppendLine(BuildBatchHeader(options, batchNumber, fileCreationDate));

        // Entry Detail Records (Type 6) + optional Addenda (Type 7)
        int entrySequence = 0;
        decimal totalDebitAmount = 0;
        int entryHash = 0;

        foreach (var entry in entries)
        {
            entrySequence++;
            var traceNumber = $"{options.OriginatingDfi:00000000}{entrySequence:0000000}";
            entry.TraceNumber = traceNumber;

            sb.AppendLine(BuildEntryDetail(entry, entrySequence, options));

            totalDebitAmount += entry.Amount;

            // Add first 8 digits of routing number to hash
            if (entry.RoutingNumber.Length >= 8 &&
                int.TryParse(entry.RoutingNumber[..8], out var routingHash))
            {
                entryHash += routingHash;
            }
        }

        // Batch Control Record (Type 8)
        sb.AppendLine(BuildBatchControl(
            options, batchNumber, entries.Count, entryHash, totalDebitAmount));

        // File Control Record (Type 9)
        sb.AppendLine(BuildFileControl(
            batchCount: 1,
            blockCount: CalculateBlockCount(entries.Count),
            entryCount: entries.Count,
            entryHash: entryHash,
            totalDebitAmount: totalDebitAmount));

        // Pad to block boundary (10 records per block)
        var lineCount = 4 + entries.Count; // header + batch header + entries + batch control + file control
        var blockPadding = (10 - (lineCount % 10)) % 10;
        for (int i = 0; i < blockPadding; i++)
        {
            sb.AppendLine(new string('9', 94));
        }

        var fileName = $"ACH-{options.CompanyId}-{fileCreationDate:yyyyMMdd-HHmmss}.ach";
        var fileContent = sb.ToString();

        _logger.LogInformation(
            "Generated NACHA file {FileName}: {EntryCount} entries, ${TotalAmount:N2} total",
            fileName, entries.Count, totalDebitAmount);

        return new NachaFileResult
        {
            FileReference = $"NACHA-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}",
            FileName = fileName,
            FileContent = fileContent,
            EntryCount = entries.Count,
            TotalAmount = totalDebitAmount,
            GeneratedAt = fileCreationDate
        };
    }

    /// <summary>
    /// File Header Record — Record Type 1
    /// </summary>
    private string BuildFileHeader(NachaFileOptions options, DateTime creationDate, string fileIdModifier)
    {
        return string.Concat(
            "1",                                                          // Record Type Code
            "01",                                                         // Priority Code
            FormatField(options.ImmediateDestination, 10, ' ', true),     // Immediate Destination (routing)
            FormatField(options.ImmediateOrigin, 10, ' ', true),          // Immediate Origin (company tax ID)
            creationDate.ToString("yyMMdd"),                              // File Creation Date
            creationDate.ToString("HHmm"),                                // File Creation Time
            FormatField(fileIdModifier, 1),                               // File ID Modifier
            "094",                                                        // Record Size
            "10",                                                         // Blocking Factor
            "1",                                                          // Format Code
            FormatField(options.ImmediateDestinationName, 23),            // Immediate Destination Name
            FormatField(options.ImmediateOriginName, 23),                 // Immediate Origin Name
            FormatField(options.ReferenceCode ?? "", 8)                   // Reference Code
        );
    }

    /// <summary>
    /// Batch Header Record — Record Type 5
    /// </summary>
    private string BuildBatchHeader(NachaFileOptions options, int batchNumber, DateTime effectiveDate)
    {
        return string.Concat(
            "5",                                                          // Record Type Code
            "200",                                                        // Service Class Code (200=mixed, 220=credits, 225=debits)
            FormatField(options.CompanyName, 16),                         // Company Name
            FormatField(options.CompanyDiscretionaryData ?? "", 20),       // Company Discretionary Data
            FormatField(options.CompanyId, 10),                           // Company Identification
            "PPD",                                                        // Standard Entry Class (PPD=Prearranged Payment and Deposit)
            FormatField(options.CompanyEntryDescription ?? "PREMIUM", 10),// Company Entry Description
            effectiveDate.ToString("yyMMdd"),                             // Company Descriptive Date
            (options.EffectiveEntryDate ?? effectiveDate.AddBusinessDays(2)).ToString("yyMMdd"), // Effective Entry Date
            "   ",                                                        // Settlement Date (Julian, filled by ACH Operator)
            "1",                                                          // Originator Status Code
            FormatField(options.OriginatingDfi.ToString(), 8),            // Originating DFI Identification
            batchNumber.ToString("0000000")                               // Batch Number
        );
    }

    /// <summary>
    /// Entry Detail Record — Record Type 6
    /// </summary>
    private static string BuildEntryDetail(NachaEntryDetail entry, int sequence, NachaFileOptions options)
    {
        var transactionCode = entry.AccountType == BankAccountType.Checking ? "27" : "37";
        // 27 = Checking Debit, 37 = Savings Debit

        return string.Concat(
            "6",                                                          // Record Type Code
            transactionCode,                                              // Transaction Code
            FormatField(entry.RoutingNumber[..8], 8),                     // Receiving DFI ID (first 8 of routing)
            entry.RoutingNumber.Length >= 9 ? entry.RoutingNumber[8].ToString() : "0", // Check Digit
            FormatField(entry.AccountNumber, 17),                         // DFI Account Number
            FormatAmount(entry.Amount, 10),                               // Amount (in cents)
            FormatField(entry.IndividualId ?? entry.GroupNumber, 15),     // Individual Identification Number
            FormatField(entry.IndividualName, 22),                        // Individual Name
            FormatField("", 2),                                           // Discretionary Data
            "0",                                                          // Addenda Record Indicator
            FormatField(options.OriginatingDfi.ToString(), 8),            // Trace Number (ODFI routing)
            sequence.ToString("0000000")                                  // Trace Number (sequence)
        );
    }

    /// <summary>
    /// Batch Control Record — Record Type 8
    /// </summary>
    private static string BuildBatchControl(
        NachaFileOptions options, int batchNumber, int entryCount, int entryHash, decimal totalDebitAmount)
    {
        return string.Concat(
            "8",                                                          // Record Type Code
            "225",                                                        // Service Class Code (225 = debits only)
            entryCount.ToString("000000"),                                // Entry/Addenda Count
            (entryHash % 10000000000).ToString("0000000000"),             // Entry Hash
            FormatAmount(0, 12),                                          // Total Debit Amount in Batch (credits)
            FormatAmount(totalDebitAmount, 12),                           // Total Credit Amount in Batch (debits from receiver perspective)
            FormatField(options.CompanyId, 10),                           // Company Identification
            new string(' ', 19),                                          // Message Authentication Code
            new string(' ', 6),                                           // Reserved
            FormatField(options.OriginatingDfi.ToString(), 8),            // Originating DFI
            batchNumber.ToString("0000000")                               // Batch Number
        );
    }

    /// <summary>
    /// File Control Record — Record Type 9
    /// </summary>
    private static string BuildFileControl(
        int batchCount, int blockCount, int entryCount, int entryHash, decimal totalDebitAmount)
    {
        return string.Concat(
            "9",                                                          // Record Type Code
            batchCount.ToString("000000"),                                // Batch Count
            blockCount.ToString("000000"),                                // Block Count
            entryCount.ToString("00000000"),                              // Entry/Addenda Count
            (entryHash % 10000000000).ToString("0000000000"),             // Entry Hash
            FormatAmount(0, 12),                                          // Total Debit Amount in File
            FormatAmount(totalDebitAmount, 12),                           // Total Credit Amount in File
            new string(' ', 39)                                           // Reserved
        );
    }

    private static int CalculateBlockCount(int entryCount)
    {
        var totalRecords = 4 + entryCount; // file header + batch header + entries + batch control + file control
        return (totalRecords + 9) / 10; // round up to nearest block of 10
    }

    private static string FormatField(string value, int length, char padChar = ' ', bool rightAlign = false)
    {
        if (value.Length > length)
            return value[..length];
        return rightAlign ? value.PadLeft(length, padChar) : value.PadRight(length, padChar);
    }

    private static string FormatAmount(decimal amount, int length)
    {
        var cents = (long)(Math.Abs(amount) * 100);
        return cents.ToString().PadLeft(length, '0');
    }
}

/// <summary>
/// Options for generating a NACHA file
/// </summary>
public class NachaFileOptions
{
    /// <summary>
    /// Originating bank routing number (the health plan's bank)
    /// </summary>
    public string ImmediateDestination { get; set; } = string.Empty;

    /// <summary>
    /// Company tax ID or FEIN (10 chars, typically 1 + 9-digit EIN)
    /// </summary>
    public string ImmediateOrigin { get; set; } = string.Empty;

    /// <summary>
    /// Originating bank name
    /// </summary>
    public string ImmediateDestinationName { get; set; } = string.Empty;

    /// <summary>
    /// Originating company name
    /// </summary>
    public string ImmediateOriginName { get; set; } = string.Empty;

    /// <summary>
    /// Company name (16 chars)
    /// </summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Company identification (typically 1 + EIN)
    /// </summary>
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>
    /// Originating Depository Financial Institution routing number (8 digits)
    /// </summary>
    public long OriginatingDfi { get; set; }

    /// <summary>
    /// Optional file ID modifier (A-Z, 0-9) for multiple files per day
    /// </summary>
    public string? FileIdModifier { get; set; }

    /// <summary>
    /// Description shown on receiver's bank statement
    /// </summary>
    public string? CompanyEntryDescription { get; set; }

    /// <summary>
    /// Optional discretionary data
    /// </summary>
    public string? CompanyDiscretionaryData { get; set; }

    /// <summary>
    /// Optional reference code
    /// </summary>
    public string? ReferenceCode { get; set; }

    /// <summary>
    /// Effective entry date (defaults to T+2 business days)
    /// </summary>
    public DateTime? EffectiveEntryDate { get; set; }
}

/// <summary>
/// A single ACH debit entry in a NACHA batch
/// </summary>
public class NachaEntryDetail
{
    public string RoutingNumber { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public BankAccountType AccountType { get; set; } = BankAccountType.Checking;
    public decimal Amount { get; set; }
    public string GroupNumber { get; set; } = string.Empty;
    public string? IndividualId { get; set; }
    public string IndividualName { get; set; } = string.Empty;
    public string? TraceNumber { get; set; }
}

/// <summary>
/// Extension to add business days to a DateTime
/// </summary>
public static class DateTimeBusinessDayExtensions
{
    public static DateTime AddBusinessDays(this DateTime date, int days)
    {
        var result = date;
        var addedDays = 0;
        while (addedDays < days)
        {
            result = result.AddDays(1);
            if (result.DayOfWeek != DayOfWeek.Saturday && result.DayOfWeek != DayOfWeek.Sunday)
                addedDays++;
        }
        return result;
    }
}
