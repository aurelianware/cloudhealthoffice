using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PaymentService.Services;

/// <summary>
/// Builds a NACHA CCD+ credit file for a fee-for-service payment run: one
/// batch (service class 220, credits only, SEC CCD, company entry description
/// HCCLAIMPMT as the NACHA healthcare EFT rule requires), one entry detail
/// record (type 6) per payee, each followed by one addenda record (type 7,
/// type code 05) whose payment-related information is the X12 TRN reassociation
/// trace <c>TRN*1*{TRN02}*{TRN03}\</c>, identical to the TRN of the 835 that
/// remits the same payment, so the provider can reassociate the EFT with its 835.
///
/// <para>
/// Pure and deterministic: the output depends only on the header and the
/// entries passed in (no clock, no random ids), so the same run yields the same
/// bytes every time. Records are 94 characters, each followed by '\n', blocked
/// by 10 with '9'-filled records. Entry hash = rightmost 10 digits of the sum
/// of the 8-digit receiving DFI ids; totals are in cents.
/// </para>
/// </summary>
public static class FfsNachaCreditFileBuilder
{
    public const int RecordLength = 94;
    public const int BlockingFactor = 10;
    public const string StandardEntryClass = "CCD";
    public const string ServiceClassCreditsOnly = "220";
    public const string HealthcareEntryDescription = "HCCLAIMPMT";
    public const string AddendaTypeCode = "05";

    /// <summary>Largest amount one entry can carry: 10 digits of cents.</summary>
    public const decimal MaxEntryAmount = 99_999_999.99m;

    /// <summary>Most credits one batch can hold: entry + addenda count must fit 6 digits.</summary>
    public const int MaxEntries = 499_999;

    /// <summary>The batch and file control totals are 12 digits of cents.</summary>
    public const long MaxTotalCents = 999_999_999_999L;

    /// <summary>
    /// The ABA routing number check digit: 3·(d1+d4+d7) + 7·(d2+d5+d8) + (d3+d6+d9) ≡ 0 (mod 10).
    /// </summary>
    public static bool IsValidAbaRoutingNumber(string? routing)
    {
        if (!IsDigits(routing, 9))
            return false;
        var d = routing!.Select(c => c - '0').ToArray();
        var sum = 3 * (d[0] + d[3] + d[6]) + 7 * (d[1] + d[4] + d[7]) + (d[2] + d[5] + d[8]);
        return sum % 10 == 0;
    }

    public static FfsNachaBuiltFile Build(FfsNachaFileHeader header, IReadOnlyList<FfsNachaCreditEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(entries);
        header.Validate();
        if (entries.Count == 0)
            throw new InvalidOperationException("A NACHA credit file needs at least one entry.");
        // The batch control's entry/addenda count is 6 digits and each credit
        // is an entry plus an addenda record.
        if (entries.Count > MaxEntries)
            throw new InvalidOperationException(
                $"A NACHA batch holds at most {MaxEntries:N0} credits (entry + addenda count is 6 digits); this run has {entries.Count:N0}. Split the run.");
        foreach (var entry in entries)
            entry.Validate();
        var totalCents = entries.Sum(e => Cents(e.Amount));
        if (totalCents > MaxTotalCents)
            throw new InvalidOperationException(
                $"The NACHA credit total {totalCents / 100m:F2} does not fit the 12-digit control total (at most {MaxTotalCents / 100m:F2}). Split the run.");

        var records = new List<string>
        {
            FileHeader(header),
            BatchHeader(header),
        };

        long hash = 0;
        long creditCents = 0;
        var traces = new List<string>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            entry.Validate();
            var sequence = i + 1;
            var trace = header.OriginatingDfi + sequence.ToString("0000000", CultureInfo.InvariantCulture);
            traces.Add(trace);

            records.Add(EntryDetail(entry, trace));
            records.Add(Addenda(header, entry, sequence));

            hash += long.Parse(entry.RoutingNumber[..8], CultureInfo.InvariantCulture);
            creditCents += Cents(entry.Amount);
        }

        var entryAndAddendaCount = entries.Count * 2;
        var entryHash = (hash % 10_000_000_000L).ToString("0000000000", CultureInfo.InvariantCulture);

        records.Add(BatchControl(header, entryAndAddendaCount, entryHash, creditCents));

        var recordCountWithFileControl = records.Count + 1;
        var blockCount = (recordCountWithFileControl + BlockingFactor - 1) / BlockingFactor;
        records.Add(FileControl(blockCount, entryAndAddendaCount, entryHash, creditCents));

        while (records.Count % BlockingFactor != 0)
            records.Add(new string('9', RecordLength));

        foreach (var record in records)
        {
            if (record.Length != RecordLength)
                throw new InvalidOperationException($"NACHA record '{record[..1]}' is {record.Length} characters, not {RecordLength}.");
        }

        var content = string.Concat(records.Select(r => r + "\n"));
        var bytes = Encoding.ASCII.GetBytes(content);
        return new FfsNachaBuiltFile
        {
            Content = content,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ByteSize = bytes.LongLength,
            EntryCount = entries.Count,
            AddendaCount = entries.Count,
            BatchCount = 1,
            BlockCount = blockCount,
            EntryHash = entryHash,
            TotalCreditAmount = creditCents / 100m,
            AchTraceNumbers = traces,
        };
    }

    /// <summary>The addenda payment-related information: the 835 TRN with '\' as segment terminator.</summary>
    public static string ReassociationTrn(string trn02, string trn03, string? trn04 = null)
        => string.IsNullOrEmpty(trn04) ? $"TRN*1*{trn02}*{trn03}\\" : $"TRN*1*{trn02}*{trn03}*{trn04}\\";

    private static string FileHeader(FfsNachaFileHeader h) => string.Concat(
        "1",
        "01",
        " " + h.ImmediateDestination,                                  // b + 9-digit routing
        Alpha(h.ImmediateOrigin, 10, rightAlign: true),
        h.FileCreatedAt.ToString("yyMMdd", CultureInfo.InvariantCulture),
        h.FileCreatedAt.ToString("HHmm", CultureInfo.InvariantCulture),
        h.FileIdModifier,
        "094",
        "10",
        "1",
        Alpha(h.ImmediateDestinationName, 23),
        Alpha(h.ImmediateOriginName, 23),
        Alpha(h.ReferenceCode, 8));

    private static string BatchHeader(FfsNachaFileHeader h) => string.Concat(
        "5",
        ServiceClassCreditsOnly,
        Alpha(h.CompanyName, 16),
        Alpha(h.CompanyDiscretionaryData, 20),
        h.CompanyId,                                                        // validated: identical to the addenda TRN03
        StandardEntryClass,
        Alpha(HealthcareEntryDescription, 10),
        h.FileCreatedAt.ToString("yyMMdd", CultureInfo.InvariantCulture),   // company descriptive date
        h.EffectiveEntryDate.ToString("yyMMdd", CultureInfo.InvariantCulture),
        "   ",                                                              // settlement date: ACH operator
        "1",
        h.OriginatingDfi,
        "0000001");

    private static string EntryDetail(FfsNachaCreditEntry e, string trace) => string.Concat(
        "6",
        e.IsSavings ? "32" : "22",
        e.RoutingNumber[..8],
        e.RoutingNumber[8..9],
        Alpha(e.AccountNumber, 17),
        Cents(e.Amount).ToString("0000000000", CultureInfo.InvariantCulture),
        Alpha(e.IdentificationNumber, 15),
        Alpha(e.ReceiverName, 22),
        "  ",
        "1",                                                                // addenda record indicator
        trace);

    private static string Addenda(FfsNachaFileHeader h, FfsNachaCreditEntry e, int entrySequence) => string.Concat(
        "7",
        AddendaTypeCode,
        Alpha(ReassociationTrn(e.ReassociationTrace, h.CompanyId, e.ReassociationTrn04), 80, upper: false),
        "0001",
        entrySequence.ToString("0000000", CultureInfo.InvariantCulture));

    private static string BatchControl(FfsNachaFileHeader h, int entryAndAddendaCount, string entryHash, long creditCents) => string.Concat(
        "8",
        ServiceClassCreditsOnly,
        entryAndAddendaCount.ToString("000000", CultureInfo.InvariantCulture),
        entryHash,
        0L.ToString("000000000000", CultureInfo.InvariantCulture),
        creditCents.ToString("000000000000", CultureInfo.InvariantCulture),
        h.CompanyId,
        new string(' ', 19),
        new string(' ', 6),
        h.OriginatingDfi,
        "0000001");

    private static string FileControl(int blockCount, int entryAndAddendaCount, string entryHash, long creditCents) => string.Concat(
        "9",
        1.ToString("000000", CultureInfo.InvariantCulture),
        blockCount.ToString("000000", CultureInfo.InvariantCulture),
        entryAndAddendaCount.ToString("00000000", CultureInfo.InvariantCulture),
        entryHash,
        0L.ToString("000000000000", CultureInfo.InvariantCulture),
        creditCents.ToString("000000000000", CultureInfo.InvariantCulture),
        new string(' ', 39));

    internal static long Cents(decimal amount)
    {
        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents))
            throw new InvalidOperationException($"NACHA amounts are whole cents; {amount} is not.");
        return (long)cents;
    }

    /// <summary>
    /// An alphanumeric field: printable ASCII only (anything else becomes a
    /// space), upper case unless told otherwise, cut or padded to
    /// <paramref name="length"/>.
    /// </summary>
    internal static string Alpha(string? value, int length, bool rightAlign = false, bool upper = true)
    {
        var chars = (value ?? string.Empty).Select(c => c is >= ' ' and <= '~' ? c : ' ').ToArray();
        var text = new string(chars);
        if (upper) text = text.ToUpperInvariant();
        if (text.Length > length) text = text[..length];
        return rightAlign ? text.PadLeft(length) : text.PadRight(length);
    }

    internal static bool IsDigits(string? value, int length)
        => value != null && value.Length == length && value.All(char.IsAsciiDigit);
}

/// <summary>File- and batch-level values. All come from configuration or the run; none from a request.</summary>
public sealed class FfsNachaFileHeader
{
    /// <summary>The ODFI's 9-digit routing number (file header immediate destination).</summary>
    public string ImmediateDestination { get; init; } = string.Empty;

    /// <summary>The originator id the ODFI assigned (10 characters, often "1" + TIN).</summary>
    public string ImmediateOrigin { get; init; } = string.Empty;

    public string ImmediateDestinationName { get; init; } = string.Empty;
    public string ImmediateOriginName { get; init; } = string.Empty;

    /// <summary>Batch company name (16).</summary>
    public string CompanyName { get; init; } = string.Empty;

    /// <summary>
    /// Batch company identification (10). Must equal the 835 TRN03 (and BPR10):
    /// the provider reassociates on it, so it is also TRN03 in every addenda.
    /// </summary>
    public string CompanyId { get; init; } = string.Empty;

    /// <summary>The ODFI's first 8 routing digits (batch header/control, trace numbers).</summary>
    public string OriginatingDfi { get; init; } = string.Empty;

    public string? CompanyDiscretionaryData { get; init; }
    public string? ReferenceCode { get; init; }

    /// <summary>File ID modifier: A-Z or 0-9.</summary>
    public string FileIdModifier { get; init; } = "A";

    /// <summary>File creation date/time: from the run, never the clock, so the file is reproducible.</summary>
    public DateTime FileCreatedAt { get; init; }

    /// <summary>The payment date: the effective entry date.</summary>
    public DateTime EffectiveEntryDate { get; init; }

    public void Validate()
    {
        var problems = new List<string>();
        if (!FfsNachaCreditFileBuilder.IsDigits(ImmediateDestination, 9))
            problems.Add("immediate destination (Nacha:ImmediateDestination) must be the ODFI's 9-digit routing number");
        if (string.IsNullOrWhiteSpace(ImmediateOrigin) || ImmediateOrigin.Length > 10)
            problems.Add("immediate origin (Nacha:ImmediateOrigin) must be 1-10 characters");
        if (string.IsNullOrWhiteSpace(CompanyName))
            problems.Add("company name (Nacha:CompanyName) is missing");
        // Written as is into both the batch header/control and every addenda
        // TRN03, so it must already be in its final form: 10 upper-case
        // letters or digits (typically "1" + the payer's TIN), as in the 835.
        if (CompanyId is not { Length: 10 } || !CompanyId.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)))
            problems.Add("company identification must be exactly 10 upper-case letters or digits (the 835 TRN03, Era:OriginatingCompanyId)");
        if (!FfsNachaCreditFileBuilder.IsDigits(OriginatingDfi, 8))
            problems.Add("originating DFI (Nacha:OriginatingDfi) must be the ODFI's first 8 routing digits");
        if (FileIdModifier is not { Length: 1 } || !char.IsAsciiLetterUpper(FileIdModifier[0]) && !char.IsAsciiDigit(FileIdModifier[0]))
            problems.Add("file ID modifier must be one character A-Z or 0-9");
        if (problems.Count > 0)
            throw new InvalidOperationException("The NACHA file cannot be built: " + string.Join("; ", problems));
    }
}

/// <summary>One payee's credit. Holds full numbers: never logged or serialized.</summary>
public sealed class FfsNachaCreditEntry
{
    public string RoutingNumber { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public bool IsSavings { get; init; }
    public decimal Amount { get; init; }

    /// <summary>Entry identification number (15): the payee NPI.</summary>
    public string IdentificationNumber { get; init; } = string.Empty;

    /// <summary>Receiving company name (22).</summary>
    public string ReceiverName { get; init; } = string.Empty;

    /// <summary>The 835's TRN02 for this payment.</summary>
    public string ReassociationTrace { get; init; } = string.Empty;

    /// <summary>Optional TRN04 (originating company supplemental code), as in the 835.</summary>
    public string? ReassociationTrn04 { get; init; }

    public void Validate()
    {
        if (!FfsNachaCreditFileBuilder.IsDigits(RoutingNumber, 9))
            throw new InvalidOperationException("A NACHA entry needs a 9-digit receiving routing number.");
        if (!FfsNachaCreditFileBuilder.IsValidAbaRoutingNumber(RoutingNumber))
            throw new InvalidOperationException("A NACHA entry's receiving routing number fails the ABA check digit.");
        if (string.IsNullOrWhiteSpace(AccountNumber) || AccountNumber.Length > 17)
            throw new InvalidOperationException("A NACHA entry needs an account number of at most 17 characters.");
        if (Amount <= 0m || Amount > FfsNachaCreditFileBuilder.MaxEntryAmount)
            throw new InvalidOperationException($"A NACHA credit must be between 0.01 and {FfsNachaCreditFileBuilder.MaxEntryAmount:F2} (was {Amount:F2}).");
        if (string.IsNullOrWhiteSpace(ReassociationTrace) || ReassociationTrace.IndexOfAny(new[] { '*', '\\', '~' }) >= 0)
            throw new InvalidOperationException("A NACHA entry needs the 835 TRN02 trace, without X12 delimiters.");
    }
}

public sealed class FfsNachaBuiltFile
{
    /// <summary>The file. Holds full routing and account numbers.</summary>
    public string Content { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long ByteSize { get; init; }
    public int EntryCount { get; init; }
    public int AddendaCount { get; init; }
    public int BatchCount { get; init; }
    public int BlockCount { get; init; }
    public string EntryHash { get; init; } = string.Empty;
    public decimal TotalCreditAmount { get; init; }

    /// <summary>The ACH trace number of each entry, in entry order.</summary>
    public IReadOnlyList<string> AchTraceNumbers { get; init; } = Array.Empty<string>();
}
