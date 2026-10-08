namespace CloudHealthOffice.Infrastructure.Edi;

/// <summary>
/// The bank and originating-company details an 835 BPR/TRN carries. Shared by
/// payment-service (claim 835s) and capitation-service (capitation 835s) so
/// both emit the same element positions.
/// </summary>
public sealed class Era835BankDetails
{
    /// <summary>Payer's bank routing number (BPR07).</summary>
    public string? PayerRoutingNumber { get; init; }
    /// <summary>Payer's bank account number (BPR09).</summary>
    public string? PayerAccountNumber { get; init; }
    /// <summary>
    /// Originating company identifier (BPR10 on ACH, and TRN03 always):
    /// exactly 10 characters, typically "1" + the payer's TIN. Configured
    /// (Era:OriginatingCompanyId); never synthesised.
    /// </summary>
    public string? OriginatingCompanyId { get; init; }
    /// <summary>Originating company supplemental code (BPR11, situational, 9 characters).</summary>
    public string? OriginatingCompanySupplementalCode { get; init; }
    /// <summary>Payee's bank routing number (BPR13).</summary>
    public string? PayeeRoutingNumber { get; init; }
    /// <summary>Payee's bank account number (BPR15).</summary>
    public string? PayeeAccountNumber { get; init; }
}

/// <summary>
/// Builds the 835 BPR (Financial Information) and TRN (Reassociation Trace
/// Number) segments (005010X221A1).
///
/// BPR element positions:
///   BPR01 transaction handling code      BPR09 sender account number
///   BPR02 total actual provider payment  BPR10 originating company identifier
///   BPR03 credit/debit flag (C)          BPR11 originating company supplemental code
///   BPR04 payment method (ACH/CHK/NON)   BPR12 receiver DFI ID qualifier (01)
///   BPR05 payment format (CCP)           BPR13 receiver DFI (routing) number
///   BPR06 sender DFI ID qualifier (01)   BPR14 receiver account qualifier (DA)
///   BPR07 sender DFI (routing) number    BPR15 receiver account number
///   BPR08 sender account qualifier (DA)  BPR16 check issue / EFT effective date
///
/// For CHK and NON, BPR05-BPR15 are empty and BPR16 carries the date. A
/// zero-amount 835 moves no money: it is always NON with BPR01 = H
/// (notification only), whatever payment method was asked for, and needs no
/// bank details.
///
/// TRN03 (required for every payment method) is the originating company
/// identifier, identical to BPR10 on an ACH BPR. It is never synthesised,
/// because a made-up value breaks reassociation of the payment and the ERA.
/// </summary>
public static class Era835FinancialSegmentBuilder
{
    /// <summary>
    /// The BPR04 payment method code for a payment method. ACH is emitted as
    /// ACH only when the payer's bank routing number is configured; otherwise
    /// the 835 is remittance-only (NON). "CHK" and "Check" (any case) are
    /// checks.
    /// </summary>
    public static string ResolvePaymentMethod(string? paymentMethod, Era835BankDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (string.Equals(paymentMethod, "CHK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(paymentMethod, "Check", StringComparison.OrdinalIgnoreCase))
            return "CHK";
        if (string.Equals(paymentMethod, "ACH", StringComparison.OrdinalIgnoreCase) && details.PayerRoutingNumber is not null)
            return "ACH";
        return "NON";
    }

    /// <summary>
    /// The BPR04 code actually emitted for an 835 paying <paramref name="totalAmount"/>:
    /// NON when nothing is paid, otherwise <see cref="ResolvePaymentMethod"/>.
    /// </summary>
    public static string ResolvePaymentMethod(decimal totalAmount, string? paymentMethod, Era835BankDetails details)
        => totalAmount == 0m ? "NON" : ResolvePaymentMethod(paymentMethod, details);

    /// <summary>
    /// The configuration problems that keep BPR/TRN from being built for
    /// <paramref name="paymentMethod"/>. Every 835 needs a 10-character
    /// originating company identifier (TRN03; BPR10 on ACH). An ACH BPR also
    /// needs both banks' routing and account numbers. Empty when both
    /// segments can be built.
    /// </summary>
    public static IReadOnlyList<string> ConfigurationProblems(string? paymentMethod, Era835BankDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var problems = new List<string>();
        var ach = ResolvePaymentMethod(paymentMethod, details) == "ACH";

        if (string.IsNullOrWhiteSpace(details.OriginatingCompanyId))
            problems.Add("originating company identifier (TRN03/BPR10, Era:OriginatingCompanyId) is missing");
        else if (details.OriginatingCompanyId.Length != 10 || HasDelimiter(details.OriginatingCompanyId))
            problems.Add("originating company identifier (TRN03/BPR10, Era:OriginatingCompanyId) must be exactly 10 characters, typically '1' + the payer's TIN");

        if (!ach)
            return problems;

        if (string.IsNullOrWhiteSpace(details.PayerRoutingNumber))
            problems.Add("payer routing number (BPR07, Era:PayerRoutingNumber) is missing");
        if (string.IsNullOrWhiteSpace(details.PayerAccountNumber))
            problems.Add("payer account number (BPR09, Era:PayerAccountNumber) is missing");
        if (!string.IsNullOrEmpty(details.OriginatingCompanySupplementalCode)
            && (details.OriginatingCompanySupplementalCode.Length != 9 || HasDelimiter(details.OriginatingCompanySupplementalCode)))
            problems.Add("originating company supplemental code (BPR11, Era:OriginatingCompanySupplementalCode) must be exactly 9 characters");
        if (string.IsNullOrWhiteSpace(details.PayeeRoutingNumber))
            problems.Add("payee routing number (BPR13, Era:PayeeRoutingNumber) is missing");
        if (string.IsNullOrWhiteSpace(details.PayeeAccountNumber))
            problems.Add("payee account number (BPR15, Era:PayeeAccountNumber) is missing");
        return problems;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when BPR/TRN cannot be
    /// built for <paramref name="paymentMethod"/> from <paramref name="details"/>.
    /// </summary>
    public static void EnsureCanBeBuilt(string? paymentMethod, Era835BankDetails details)
    {
        var problems = ConfigurationProblems(paymentMethod, details);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Cannot build the 835 BPR/TRN segments: " + string.Join("; ", problems));
    }

    /// <summary>The BPR segment, terminator included.</summary>
    public static string BuildBpr(decimal totalAmount, string? paymentMethod, DateTime paymentDate, Era835BankDetails details)
    {
        var method = ResolvePaymentMethod(totalAmount, paymentMethod, details);
        EnsureCanBeBuilt(method, details);

        // BPR01: C = payment accompanies remittance; H = notification only
        // (BPR02 = 0, BPR04 = NON).
        var handlingCode = totalAmount > 0 ? "C" : "H";

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
            e[5] = "CCP";
            e[6] = "01";
            e[7] = details.PayerRoutingNumber!;
            e[8] = "DA";
            e[9] = details.PayerAccountNumber!;
            e[10] = details.OriginatingCompanyId!;
            e[11] = details.OriginatingCompanySupplementalCode ?? string.Empty;
            e[12] = "01";
            e[13] = details.PayeeRoutingNumber!;
            e[14] = "DA";
            e[15] = details.PayeeAccountNumber!;
        }

        e[16] = paymentDate.ToString("yyyyMMdd");
        return string.Join("*", e) + "~";
    }

    /// <summary>
    /// The TRN segment, terminator included. TRN03 is the configured
    /// originating company identifier, identical to BPR10.
    /// </summary>
    public static string BuildTrn(string checkOrEftNumber, Era835BankDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (string.IsNullOrWhiteSpace(details.OriginatingCompanyId))
            throw new InvalidOperationException(
                "Cannot build the 835 TRN segment: originating company identifier (TRN03, Era:OriginatingCompanyId) is missing");
        return $"TRN*1*{checkOrEftNumber}*{details.OriginatingCompanyId}~";
    }

    private static bool HasDelimiter(string value) =>
        value.IndexOfAny(new[] { '*', '~', ':', '^' }) >= 0;
}
