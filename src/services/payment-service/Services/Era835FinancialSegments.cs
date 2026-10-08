using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// Builds the 835 BPR (Financial Information) and TRN (Reassociation Trace
/// Number) segments for both <see cref="EraGeneratorService"/> and
/// <see cref="BatchEraGeneratorService"/>, so the element positions are
/// defined once, and checks the 835 balances before it is emitted.
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
/// A zero-pay ERA (BPR02 = 0) moves no money: BPR01 = H (notification only)
/// and BPR04 = NON, whatever the run's payment method.
///
/// TRN03 (required for every payment method) is the originating company
/// identifier, "1" + the payer's TIN, identical to BPR10 on an ACH BPR. It
/// is always configured (Era:OriginatingCompanyId); it is never synthesised,
/// because a made-up value breaks reassociation of the payment and the ERA.
/// </summary>
public static class Era835FinancialSegments
{
    /// <summary>
    /// The BPR04 payment method code for a payment method. ACH is emitted as
    /// ACH only when the payer's bank routing number is configured; otherwise
    /// the 835 is remittance-only (NON), as before. "CHK" and "Check" (any
    /// case) are checks.
    /// </summary>
    public static string ResolveBprPaymentMethod(string? paymentMethod, TradingPartnerInfo tp) =>
        ResolveConfiguredMethod(paymentMethod, tp);

    /// <summary>
    /// The BPR04 code for an ERA of <paramref name="totalAmount"/>: NON when
    /// nothing is paid (BPR02 = 0), otherwise the run's method.
    /// </summary>
    public static string ResolveBprPaymentMethod(decimal totalAmount, string? paymentMethod, TradingPartnerInfo tp) =>
        totalAmount == 0m ? "NON" : ResolveConfiguredMethod(paymentMethod, tp);

    private static string ResolveConfiguredMethod(string? paymentMethod, TradingPartnerInfo tp)
    {
        if (string.Equals(paymentMethod, "CHK", StringComparison.OrdinalIgnoreCase)
            || string.Equals(paymentMethod, "Check", StringComparison.OrdinalIgnoreCase))
            return "CHK";
        if (string.Equals(paymentMethod, "ACH", StringComparison.OrdinalIgnoreCase) && tp.PayerRoutingNumber is not null)
            return "ACH";
        return "NON";
    }

    /// <summary>
    /// The configuration problems that keep BPR/TRN from being built for
    /// <paramref name="paymentMethod"/>. Every 835 needs a 10-character
    /// originating company identifier (TRN03; BPR10 on ACH). An ACH BPR also
    /// needs both banks' routing and account numbers. Empty when both
    /// segments can be built.
    /// </summary>
    public static IReadOnlyList<string> ConfigurationProblems(string? paymentMethod, TradingPartnerInfo tp)
    {
        var problems = new List<string>();
        var ach = ResolveBprPaymentMethod(paymentMethod, tp) == "ACH";

        if (string.IsNullOrWhiteSpace(tp.OriginatingCompanyId))
            problems.Add("originating company identifier (TRN03/BPR10, Era:OriginatingCompanyId) is missing");
        else if (tp.OriginatingCompanyId.Length != 10 || HasDelimiter(tp.OriginatingCompanyId))
            problems.Add("originating company identifier (TRN03/BPR10, Era:OriginatingCompanyId) must be exactly 10 characters, typically '1' + the payer's TIN");

        if (!ach)
            return problems;

        if (string.IsNullOrWhiteSpace(tp.PayerRoutingNumber))
            problems.Add("payer routing number (BPR07, Era:PayerRoutingNumber) is missing");
        if (string.IsNullOrWhiteSpace(tp.PayerAccountNumber))
            problems.Add("payer account number (BPR09, Era:PayerAccountNumber) is missing");
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
    /// Throws <see cref="InvalidOperationException"/> when BPR/TRN cannot be
    /// built for <paramref name="paymentMethod"/> from <paramref name="tp"/>.
    /// Run services call this before reserving or paying any claim, so a
    /// misconfigured payer fails the run up front instead of after payments
    /// are issued.
    /// </summary>
    public static void EnsureBprCanBeBuilt(string? paymentMethod, TradingPartnerInfo tp)
    {
        var problems = ConfigurationProblems(paymentMethod, tp);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Cannot build the 835 BPR/TRN segments: " + string.Join("; ", problems));
    }

    /// <summary>The BPR segment, terminator included.</summary>
    public static string BuildBpr(decimal totalAmount, string? paymentMethod, DateTime paymentDate, TradingPartnerInfo tp)
    {
        // A zero-pay ERA moves no money: NON, whatever the run's method.
        var method = ResolveBprPaymentMethod(totalAmount, paymentMethod, tp);
        EnsureBprCanBeBuilt(method, tp);

        // BPR01: C = payment accompanies remittance; H = notification only
        // (BPR02 = 0, BPR04 = NON); I = remittance information only (a
        // negative reversal total, unchanged here).
        var handlingCode = totalAmount > 0 ? "C" : totalAmount == 0m ? "H" : "I";

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
    /// The TRN segment, terminator included. TRN03 is the configured
    /// originating company identifier, identical to BPR10.
    /// </summary>
    public static string BuildTrn(string checkOrEftNumber, TradingPartnerInfo tp)
    {
        if (string.IsNullOrWhiteSpace(tp.OriginatingCompanyId))
            throw new InvalidOperationException(
                "Cannot build the 835 TRN segment: originating company identifier (TRN03, Era:OriginatingCompanyId) is missing");
        return $"TRN*1*{checkOrEftNumber}*{tp.OriginatingCompanyId}~";
    }

    /// <summary>
    /// The balancing problem of one claim's service lines, or null: when a
    /// claim carries service lines, the sum of their payments (SVC03) must
    /// equal the claim payment (CLP04).
    /// </summary>
    public static string? ServiceLineBalanceProblem(ClaimPayment cp)
    {
        if (cp.ServiceLines.Count == 0)
            return null;
        var lines = cp.ServiceLines.Sum(sl => sl.PaymentAmount);
        return lines == cp.PaymentAmount
            ? null
            : $"claim {cp.ClaimId}: service line payments (SVC03) total {lines:F2} but the claim payment (CLP04) is {cp.PaymentAmount:F2}";
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless the 835 balances:
    /// BPR02 = sum of CLP04 - sum of PLB amounts, and for every claim with
    /// service lines, sum of SVC03 = CLP04.
    /// </summary>
    public static void EnsureBalanced(
        decimal bprAmount, IEnumerable<ClaimPayment> claimPayments, IEnumerable<ProviderAdjustment> providerAdjustments)
    {
        var claims = claimPayments.ToList();
        var problems = claims.Select(ServiceLineBalanceProblem).Where(p => p != null).ToList();

        var expected = claims.Sum(cp => cp.PaymentAmount) - providerAdjustments.Sum(a => a.Amount);
        if (expected != bprAmount)
            problems.Add($"BPR02 is {bprAmount:F2} but the claim payments (CLP04) less provider adjustments (PLB) total {expected:F2}");

        if (problems.Count > 0)
            throw new InvalidOperationException("Cannot generate an unbalanced 835: " + string.Join("; ", problems));
    }

    private static bool HasDelimiter(string value) =>
        value.IndexOfAny(new[] { '*', '~', ':', '^' }) >= 0;
}
