namespace PaymentService.Models;

/// <summary>
/// A payment the run pays by check although the run's method is ACH, because
/// its payee has no approved EFT account. Recorded at execution (the payment
/// and its 835 BPR04 are CHK) or when the EFT file is generated (the approved
/// account was gone by then: its 835 already said ACH and it needs a person).
/// </summary>
public class CheckFallbackPayment
{
    public string PaymentId { get; set; } = string.Empty;
    public string? PayeeNpi { get; set; }
    public string? PayeeName { get; set; }

    /// <summary>The check number, also the 835's TRN02.</summary>
    public string CheckNumber { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>"Execution" or "EftFile": when the fallback was decided.</summary>
    public string DecidedAt { get; set; } = "Execution";

    /// <summary>True when the payment's 835 already went out as ACH: someone must tell the provider.</summary>
    public bool NeedsAttention { get; set; }
}

/// <summary>One receivable recovery a payment run made (see <see cref="Payment.ReceivableOffsets"/>).</summary>
public class PaymentRunReceivableRecovery
{
    public string ReceivableId { get; set; } = string.Empty;
    public string PaymentId { get; set; } = string.Empty;
    public string? PayeeNpi { get; set; }
    public decimal Amount { get; set; }
    public string AdjustmentCode { get; set; } = string.Empty;
    public string? Reference { get; set; }
}

/// <summary>
/// What the run's NACHA CCD+ credit file holds, read from the file itself, and
/// pinned at first generation: regenerating the file for the run must
/// reproduce <see cref="Sha256"/> byte for byte, or it is refused. No routing
/// number, account number or TIN is kept, only their last four digits.
/// </summary>
public class PaymentRunEftFile
{
    public string FileReference { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long ByteSize { get; set; }

    /// <summary>Entry detail records (type 6): one per payee (TIN + account) per 835 trace number.</summary>
    public int EntryCount { get; set; }

    /// <summary>Addenda records (type 7, one per entry).</summary>
    public int AddendaCount { get; set; }

    public int BatchCount { get; set; }
    public int BlockCount { get; set; }

    /// <summary>Rightmost 10 digits of the sum of the receiving DFI ids.</summary>
    public string EntryHash { get; set; } = string.Empty;

    public decimal TotalCreditAmount { get; set; }
    public decimal TotalDebitAmount { get; set; }

    public DateTime FileCreatedAt { get; set; }
    public DateTime EffectiveEntryDate { get; set; }

    /// <summary>
    /// File header file ID modifier (A-Z, 0-9), allocated on first generation so no
    /// two files of the tenant created the same day share one (duplicate-file
    /// detection), and reused on every rebuild. Null on files pinned before allocation
    /// existed: those rebuild with <c>Nacha:FileIdModifier</c> (default A).
    /// </summary>
    public string? FileIdModifier { get; set; }

    /// <summary>0 for the first file; n for the n-th re-date (file reference and name end in -R{n}).</summary>
    public int Revision { get; set; }

    /// <summary>Set on a file in <see cref="PaymentRun.EftFileHistory"/>: when, by whom and why it was replaced, and by which file.</summary>
    public DateTime? SupersededAt { get; set; }
    public string? SupersededBy { get; set; }
    public string? SupersededReason { get; set; }
    public string? SupersededByFileReference { get; set; }

    public DateTime FirstGeneratedAt { get; set; }
    public string? FirstGeneratedBy { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public string? LastVerifiedBy { get; set; }
    public int GenerationCount { get; set; }

    public List<PaymentRunEftEntry> Entries { get; set; } = new();

    /// <summary>Payments not in the file: paid by check (no approved EFT account).</summary>
    public List<CheckFallbackPayment> CheckFallbacks { get; set; } = new();

    /// <summary>Payments not in the file because offsets brought them to zero (nothing to credit).</summary>
    public List<string> ZeroAmountPaymentIds { get; set; } = new();
}

/// <summary>One NACHA credit (entry detail + addenda), without full numbers.</summary>
public class PaymentRunEftEntry
{
    /// <summary>The ACH trace number (ODFI + sequence), positions 80-94 of the entry.</summary>
    public string AchTraceNumber { get; set; } = string.Empty;

    /// <summary>The reassociation trace (835 TRN02) carried in the addenda.</summary>
    public string ReassociationTrace { get; set; } = string.Empty;

    public decimal Amount { get; set; }
    public List<string> PayeeNpis { get; set; } = new();
    public string? ReceiverName { get; set; }
    public string? TaxIdLast4 { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
    public string TransactionCode { get; set; } = string.Empty;
    public List<string> PaymentIds { get; set; } = new();
    public int ClaimCount { get; set; }
}
