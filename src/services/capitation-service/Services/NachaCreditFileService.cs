using System.Text;
using CapitationService.Models;

namespace CapitationService.Services;

/// <summary>
/// Generates NACHA (National Automated Clearing House Association) formatted ACH files
/// for batch capitation CREDIT disbursements to providers. Unlike the premium-billing
/// NachaFileService which generates DEBIT entries (collecting from sponsors), this service
/// generates CREDIT entries (paying out to providers).
///
/// Key differences from debit-side NACHA:
/// - Transaction code 22 (checking credit) instead of 27 (checking debit)
/// - Transaction code 32 (savings credit) instead of 37 (savings debit)
/// - SEC code CCD (Corporate Credit or Debit) instead of PPD
/// - Service class code 220 (credits only) instead of 225 (debits only)
/// - Company entry description "CAPITATION" instead of "PREMIUM"
/// </summary>
public interface INachaCreditFileService
{
    /// <summary>
    /// Generate a NACHA credit file for a batch of capitation disbursements
    /// </summary>
    NachaCreditFileResult GenerateNachaCreditFile(List<NachaCreditEntryDetail> entries, NachaCreditFileOptions options);
}

public class NachaCreditFileService : INachaCreditFileService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<NachaCreditFileService> _logger;

    public NachaCreditFileService(IConfiguration configuration, ILogger<NachaCreditFileService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public NachaCreditFileResult GenerateNachaCreditFile(List<NachaCreditEntryDetail> entries, NachaCreditFileOptions options)
    {
        if (entries.Count == 0)
            throw new InvalidOperationException("No entries to include in NACHA credit file");

        var sb = new StringBuilder();
        var fileCreationDate = DateTime.UtcNow;
        var fileIdModifier = options.FileIdModifier ?? "A";

        // File Header Record (Type 1)
        sb.AppendLine(BuildFileHeader(options, fileCreationDate, fileIdModifier));

        // Batch Header Record (Type 5)
        var batchNumber = 1;
        sb.AppendLine(BuildBatchHeader(options, batchNumber, fileCreationDate));

        // Entry Detail Records (Type 6)
        int entrySequence = 0;
        decimal totalCreditAmount = 0;
        int entryHash = 0;

        foreach (var entry in entries)
        {
            entrySequence++;
            var traceNumber = $"{options.OriginatingDfi:00000000}{entrySequence:0000000}";
            entry.TraceNumber = traceNumber;

            sb.AppendLine(BuildEntryDetail(entry, entrySequence, options));

            totalCreditAmount += entry.Amount;

            // Add first 8 digits of routing number to hash
            if (entry.RoutingNumber.Length >= 8 &&
                int.TryParse(entry.RoutingNumber[..8], out var routingHash))
            {
                entryHash += routingHash;
            }
        }

        // Batch Control Record (Type 8)
        sb.AppendLine(BuildBatchControl(
            options, batchNumber, entries.Count, entryHash, totalCreditAmount));

        // File Control Record (Type 9)
        sb.AppendLine(BuildFileControl(
            batchCount: 1,
            blockCount: CalculateBlockCount(entries.Count),
            entryCount: entries.Count,
            entryHash: entryHash,
            totalCreditAmount: totalCreditAmount));

        // Pad to block boundary (10 records per block)
        var lineCount = 4 + entries.Count; // header + batch header + entries + batch control + file control
        var blockPadding = (10 - (lineCount % 10)) % 10;
        for (int i = 0; i < blockPadding; i++)
        {
            sb.AppendLine(new string('9', 94));
        }

        var fileName = $"ACH-CREDIT-{options.CompanyId}-{fileCreationDate:yyyyMMdd-HHmmss}.ach";
        var fileContent = sb.ToString();

        _logger.LogInformation(
            "Generated NACHA credit file {FileName}: {EntryCount} entries, ${TotalAmount:N2} total",
            fileName, entries.Count, totalCreditAmount);

        return new NachaCreditFileResult
        {
            FileReference = $"NACHA-CR-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}",
            FileName = fileName,
            FileContent = fileContent,
            EntryCount = entries.Count,
            TotalAmount = totalCreditAmount,
            GeneratedAt = fileCreationDate
        };
    }

    /// <summary>
    /// File Header Record — Record Type 1
    /// </summary>
    private static string BuildFileHeader(NachaCreditFileOptions options, DateTime creationDate, string fileIdModifier)
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
    /// Service class code 220 = credits only (provider payments)
    /// SEC code CCD = Corporate Credit or Debit
    /// </summary>
    private static string BuildBatchHeader(NachaCreditFileOptions options, int batchNumber, DateTime effectiveDate)
    {
        return string.Concat(
            "5",                                                          // Record Type Code
            "220",                                                        // Service Class Code (220 = credits only)
            FormatField(options.CompanyName, 16),                         // Company Name
            FormatField(options.CompanyDiscretionaryData ?? "", 20),       // Company Discretionary Data
            FormatField(options.CompanyId, 10),                           // Company Identification
            "CCD",                                                        // Standard Entry Class (CCD = Corporate Credit or Debit)
            FormatField(options.CompanyEntryDescription ?? "CAPITATION", 10), // Company Entry Description
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
    /// Transaction code 22 = checking CREDIT, 32 = savings CREDIT
    /// (vs 27/37 for debits in premium-billing)
    /// </summary>
    private static string BuildEntryDetail(NachaCreditEntryDetail entry, int sequence, NachaCreditFileOptions options)
    {
        // 22 = Checking Credit, 32 = Savings Credit
        var transactionCode = entry.AccountType == BankAccountType.Checking ? "22" : "32";

        return string.Concat(
            "6",                                                          // Record Type Code
            transactionCode,                                              // Transaction Code (credit)
            FormatField(entry.RoutingNumber[..8], 8),                     // Receiving DFI ID (first 8 of routing)
            entry.RoutingNumber.Length >= 9 ? entry.RoutingNumber[8].ToString() : "0", // Check Digit
            FormatField(entry.AccountNumber, 17),                         // DFI Account Number
            FormatAmount(entry.Amount, 10),                               // Amount (in cents)
            FormatField(entry.IndividualId ?? entry.ProviderNpi, 15),     // Individual Identification Number
            FormatField(entry.IndividualName, 22),                        // Individual Name
            FormatField("", 2),                                           // Discretionary Data
            "0",                                                          // Addenda Record Indicator
            FormatField(options.OriginatingDfi.ToString(), 8),            // Trace Number (ODFI routing)
            sequence.ToString("0000000")                                  // Trace Number (sequence)
        );
    }

    /// <summary>
    /// Batch Control Record — Record Type 8
    /// Service class code 220 = credits only
    /// Credit total goes in position 32-43 (Total Credit Amount)
    /// </summary>
    private static string BuildBatchControl(
        NachaCreditFileOptions options, int batchNumber, int entryCount, int entryHash, decimal totalCreditAmount)
    {
        return string.Concat(
            "8",                                                          // Record Type Code
            "220",                                                        // Service Class Code (220 = credits only)
            entryCount.ToString("000000"),                                // Entry/Addenda Count
            (entryHash % 10000000000).ToString("0000000000"),             // Entry Hash
            FormatAmount(0, 12),                                          // Total Debit Amount in Batch
            FormatAmount(totalCreditAmount, 12),                          // Total Credit Amount in Batch
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
        int batchCount, int blockCount, int entryCount, int entryHash, decimal totalCreditAmount)
    {
        return string.Concat(
            "9",                                                          // Record Type Code
            batchCount.ToString("000000"),                                // Batch Count
            blockCount.ToString("000000"),                                // Block Count
            entryCount.ToString("00000000"),                              // Entry/Addenda Count
            (entryHash % 10000000000).ToString("0000000000"),             // Entry Hash
            FormatAmount(0, 12),                                          // Total Debit Amount in File
            FormatAmount(totalCreditAmount, 12),                          // Total Credit Amount in File
            new string(' ', 39)                                           // Reserved
        );
    }

    private static int CalculateBlockCount(int entryCount)
    {
        var totalRecords = 4 + entryCount;
        return (totalRecords + 9) / 10;
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
/// Options for generating a NACHA credit file for capitation disbursements
/// </summary>
public class NachaCreditFileOptions
{
    /// <summary>Originating bank routing number</summary>
    public string ImmediateDestination { get; set; } = string.Empty;

    /// <summary>Company tax ID or FEIN (10 chars)</summary>
    public string ImmediateOrigin { get; set; } = string.Empty;

    /// <summary>Originating bank name</summary>
    public string ImmediateDestinationName { get; set; } = string.Empty;

    /// <summary>Originating company name</summary>
    public string ImmediateOriginName { get; set; } = string.Empty;

    /// <summary>Company name (16 chars)</summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>Company identification (typically 1 + EIN)</summary>
    public string CompanyId { get; set; } = string.Empty;

    /// <summary>Originating DFI routing number (8 digits)</summary>
    public long OriginatingDfi { get; set; }

    /// <summary>File ID modifier (A-Z, 0-9) for multiple files per day</summary>
    public string? FileIdModifier { get; set; }

    /// <summary>Description shown on receiver's bank statement (default: CAPITATION)</summary>
    public string? CompanyEntryDescription { get; set; }

    /// <summary>Optional discretionary data</summary>
    public string? CompanyDiscretionaryData { get; set; }

    /// <summary>Optional reference code</summary>
    public string? ReferenceCode { get; set; }

    /// <summary>Effective entry date (defaults to T+2 business days)</summary>
    public DateTime? EffectiveEntryDate { get; set; }
}

/// <summary>
/// Bank account type for NACHA transaction code selection
/// </summary>
public enum BankAccountType
{
    Checking,
    Savings
}

/// <summary>
/// A single ACH credit entry in a NACHA batch (provider payment)
/// </summary>
public class NachaCreditEntryDetail
{
    public string RoutingNumber { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public BankAccountType AccountType { get; set; } = BankAccountType.Checking;
    public decimal Amount { get; set; }
    public string ProviderNpi { get; set; } = string.Empty;
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
