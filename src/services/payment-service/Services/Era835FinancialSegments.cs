namespace PaymentService.Services;

/// <summary>
/// Builds the 835 BPR (Financial Information) and TRN (Reassociation Trace
/// Number) segments for both <see cref="EraGeneratorService"/> and
/// <see cref="BatchEraGeneratorService"/>, so the element positions are
/// defined once.
///
/// BPR element positions (005010X221A1):
///   BPR01 transaction handling code      BPR09 sender account number
///   BPR02 total actual provider payment  BPR10 originating company identifier
///   BPR03 credit/debit flag (C)          BPR11 originating company supplemental code
///   BPR04 payment method (ACH/CHK/NON)   BPR12 receiver DFI ID qualifier (01)
///   BPR05 payment format (CCP)           BPR13 receiver DFI (routing) number
///   BPR06 sender DFI ID qualifier (01)   BPR14 receiver account qualifier (DA)
///   BPR07 sender DFI (routing) number    BPR15 receiver account number
///   BPR08 sender account qualifier (DA)  BPR16 check issue / EFT effective date
///
/// For CHK and NON, BPR05-BPR15 are empty and BPR16 carries the date.
/// </summary>
public static class Era835FinancialSegments
{
    /// <summary>Default TRN03 when neither an originating company id nor a payer id is configured.</summary>
    public const string DefaultTraceOriginatorId = "1999999999";

    /// <summary>
    /// The BPR04 payment method code for a payment method. ACH is emitted as
    /// ACH only when the payer's bank routing number is configured; otherwise
    /// the 835 is remittance-only (NON), as before.
    /// </summary>
    public static string ResolveBprPaymentMethod(string? paymentMethod, TradingPartnerInfo tp) => paymentMethod switch
    {
        "CHK" => "CHK",
        "ACH" when tp.PayerRoutingNumber is not null => "ACH",
        _ => "NON"
    };

    /// <summary>
    /// The ACH BPR configuration problems for <paramref name="tp"/>: an ACH BPR
    /// needs both banks' routing and account numbers and a 10-character
    /// originating company identifier (BPR10, typically "1" + the payer's TIN).
    /// Empty when an ACH BPR can be built.
    /// </summary>
    public static IReadOnlyList<string> AchConfigurationProblems(TradingPartnerInfo tp)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(tp.PayerRoutingNumber))
            problems.Add("payer routing number (BPR07, Era:PayerRoutingNumber) is missing");
        if (string.IsNullOrWhiteSpace(tp.PayerAccountNumber))
            problems.Add("payer account number (BPR09, Era:PayerAccountNumber) is missing");
        if (string.IsNullOrWhiteSpace(tp.OriginatingCompanyId))
            problems.Add("originating company identifier (BPR10, Era:OriginatingCompanyId) is missing");
        else if (tp.OriginatingCompanyId.Length != 10 || HasDelimiter(tp.OriginatingCompanyId))
            problems.Add("originating company identifier (BPR10, Era:OriginatingCompanyId) must be exactly 10 characters, typically '1' + the payer's TIN");
        if (!string.IsNullOrEmpty(tp.OriginatingCompanySupplementalCode)
            && (tp.OriginatingCompanySupplementalCode.Length != 9 || HasDelimiter(tp.OriginatingCompanySupplementalCode)))
            problems.Add("originating company supplemental code (BPR11, Era:OriginatingCompanySupplementalCode) must be exactly 9 characters");
        if (string.IsNullOrWhiteSpace(tp.PayeeRoutingNumber))
            problems.Add("payee routing number (BPR13, Era:PayeeRoutingNumber) is missing");
        if (string.IsNullOrWhiteSpace(tp.PayeeAccountNumber))
            problems.Add("payee account number (BPR15, Era:PayeeAccountNumber) is missing");
        return problems;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="paymentMethod"/>
    /// resolves to an ACH BPR that <paramref name="tp"/> cannot fill. Run services call
    /// this before reserving or paying any claim, so a misconfigured payer fails
    /// the run up front instead of after payments are issued.
    /// </summary>
    public static void EnsureBprCanBeBuilt(string? paymentMethod, TradingPartnerInfo tp)
    {
        if (ResolveBprPaymentMethod(paymentMethod, tp) != "ACH")
            return;

        var problems = AchConfigurationProblems(tp);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Cannot build the 835 ACH BPR segment: " + string.Join("; ", problems));
    }

    /// <summary>The BPR segment, terminator included.</summary>
    public static string BuildBpr(decimal totalAmount, string? paymentMethod, DateTime paymentDate, TradingPartnerInfo tp)
    {
        // BPR01: C = payment accompanies remittance, I = remittance only (zero-pay ERA)
        var handlingCode = totalAmount > 0 ? "C" : "I";
        var method = ResolveBprPaymentMethod(paymentMethod, tp);

        var e = new string[17];
        e[0] = "BPR";
        e[1] = handlingCode;
        e[2] = totalAmount.ToString("F2");
        e[3] = "C";
        e[4] = method;
        for (var i = 5; i <= 15; i++)
            e[i] = string.Empty;

        if (method == "ACH")
        {
            EnsureBprCanBeBuilt(paymentMethod, tp);
            e[5] = "CCP";
            e[6] = "01";
            e[7] = tp.PayerRoutingNumber!;
            e[8] = "DA";
            e[9] = tp.PayerAccountNumber!;
            e[10] = tp.OriginatingCompanyId!;
            e[11] = tp.OriginatingCompanySupplementalCode ?? string.Empty;
            e[12] = "01";
            e[13] = tp.PayeeRoutingNumber!;
            e[14] = "DA";
            e[15] = tp.PayeeAccountNumber!;
        }

        e[16] = paymentDate.ToString("yyyyMMdd");
        return string.Join("*", e) + "~";
    }

    /// <summary>
    /// The TRN segment, terminator included. TRN03 is the originating company
    /// identifier, identical to BPR10 when one is configured; otherwise the
    /// payer id (previous behaviour).
    /// </summary>
    public static string BuildTrn(string checkOrEftNumber, string? payerId, TradingPartnerInfo tp)
    {
        var originator = !string.IsNullOrWhiteSpace(tp.OriginatingCompanyId)
            ? tp.OriginatingCompanyId
            : payerId ?? DefaultTraceOriginatorId;
        return $"TRN*1*{checkOrEftNumber}*{originator}~";
    }

    private static bool HasDelimiter(string value) =>
        value.IndexOfAny(new[] { '*', '~', ':', '^' }) >= 0;
}
