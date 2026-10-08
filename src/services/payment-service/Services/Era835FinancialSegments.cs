using CloudHealthOffice.Infrastructure.Edi;
using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// The 835 BPR (Financial Information) and TRN (Reassociation Trace Number)
/// segments for both <see cref="EraGeneratorService"/> and
/// <see cref="BatchEraGeneratorService"/>, and the check that an 835 balances
/// before it is emitted. The segments themselves are built by the shared
/// <see cref="Era835FinancialSegmentBuilder"/> (CloudHealthOffice.Infrastructure),
/// which capitation-service uses too, so the element positions are defined
/// once. See there for the BPR element positions.
///
/// TRN03 (required for every payment method) is the originating company
/// identifier, "1" + the payer's TIN, identical to BPR10 on an ACH BPR. It
/// is always configured (Era:OriginatingCompanyId); it is never synthesised.
/// </summary>
public static class Era835FinancialSegments
{
    /// <summary>
    /// The BPR04 payment method code for a payment method; see
    /// <see cref="Era835FinancialSegmentBuilder.ResolvePaymentMethod(string?, Era835BankDetails)"/>.
    /// </summary>
    public static string ResolveBprPaymentMethod(string? paymentMethod, TradingPartnerInfo tp)
        => Era835FinancialSegmentBuilder.ResolvePaymentMethod(paymentMethod, BankDetails(tp));

    /// <summary>
    /// The BPR04 code for an ERA of <paramref name="totalAmount"/>: NON when
    /// nothing is paid (BPR02 = 0), otherwise the run's method.
    /// </summary>
    public static string ResolveBprPaymentMethod(decimal totalAmount, string? paymentMethod, TradingPartnerInfo tp)
        => Era835FinancialSegmentBuilder.ResolvePaymentMethod(totalAmount, paymentMethod, BankDetails(tp));

    /// <summary>
    /// The configuration problems that keep BPR/TRN from being built for
    /// <paramref name="paymentMethod"/>. Empty when both segments can be built.
    /// </summary>
    public static IReadOnlyList<string> ConfigurationProblems(string? paymentMethod, TradingPartnerInfo tp)
        => Era835FinancialSegmentBuilder.ConfigurationProblems(paymentMethod, BankDetails(tp));

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when BPR/TRN cannot be
    /// built for <paramref name="paymentMethod"/> from <paramref name="tp"/>.
    /// Run services call this before reserving or paying any claim, so a
    /// misconfigured payer fails the run up front instead of after payments
    /// are issued.
    /// </summary>
    public static void EnsureBprCanBeBuilt(string? paymentMethod, TradingPartnerInfo tp)
        => Era835FinancialSegmentBuilder.EnsureCanBeBuilt(paymentMethod, BankDetails(tp));

    /// <summary>
    /// The BPR segment, terminator included. A zero-amount 835 (e.g. denials
    /// only) is NON with BPR01 = H.
    /// </summary>
    public static string BuildBpr(decimal totalAmount, string? paymentMethod, DateTime paymentDate, TradingPartnerInfo tp)
        => Era835FinancialSegmentBuilder.BuildBpr(totalAmount, paymentMethod, paymentDate, BankDetails(tp));

    /// <summary>
    /// The TRN segment, terminator included. TRN03 is the configured
    /// originating company identifier, identical to BPR10.
    /// </summary>
    public static string BuildTrn(string checkOrEftNumber, TradingPartnerInfo tp)
        => Era835FinancialSegmentBuilder.BuildTrn(checkOrEftNumber, BankDetails(tp));

    /// <summary>
    /// BPR02 and the PLB adjustments of an 835 whose claims and PLBs net to
    /// <paramref name="netAmount"/> (sum CLP04 - sum PLB). BPR02 is never
    /// negative: a net-negative remittance (e.g. a reversal run's
    /// recoupment) is BPR02 = 0, with a forward-balance PLB (FB, amount =
    /// the negative net, reference = the 835's trace number) carrying what
    /// the provider owes, so sum(CLP04) - sum(PLB) = BPR02 = 0.
    /// <paramref name="forwardBalance"/> is that negative amount (0 when none).
    /// </summary>
    public static (decimal BprAmount, List<ProviderAdjustment> ProviderAdjustments, decimal ForwardBalance) WithForwardBalance(
        decimal netAmount, IEnumerable<ProviderAdjustment> providerAdjustments, string traceNumber, DateTime fiscalPeriodEnd)
    {
        var plbs = providerAdjustments.ToList();
        if (netAmount >= 0m)
            return (netAmount, plbs, 0m);

        plbs.Add(new ProviderAdjustment
        {
            AdjustmentIdentifier = Era835FinancialSegmentBuilder.ForwardBalanceCode,
            ReferenceIdentification = traceNumber,
            Amount = netAmount,
            FiscalPeriodEnd = fiscalPeriodEnd,
            Description = "Negative balance carried forward (provider receivable)",
        });
        return (0m, plbs, netAmount);
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
    /// The adjustment balancing problems of one claim, payment or reversal
    /// (CLP02 = 22, every amount negated) alike. Once its lines carry CAS:
    /// every line must satisfy SVC02 - sum(line CAS) = SVC03 (a line without
    /// CAS must be paid in full), and the claim CLP03 - sum(CAS, claim and
    /// lines) = CLP04. Empty when it balances, or when no line carries CAS
    /// (claim-level CAS, and payments and reversals recorded before lines
    /// carried CAS, are not checked here).
    /// </summary>
    public static IReadOnlyList<string> AdjustmentBalanceProblems(ClaimPayment cp)
    {
        var problems = new List<string>();
        if (!cp.ServiceLines.Any(l => l.Adjustments.Count > 0))
            return problems;

        foreach (var line in cp.ServiceLines)
        {
            var cas = line.Adjustments.Sum(a => a.Amount);
            if (line.ChargeAmount - cas != line.PaymentAmount)
                problems.Add(
                    $"claim {cp.ClaimId} line {line.LineNumber}: charge (SVC02) {line.ChargeAmount:F2} less adjustments (CAS) {cas:F2} " +
                    $"is {line.ChargeAmount - cas:F2} but the line payment (SVC03) is {line.PaymentAmount:F2}");
        }

        var allCas = cp.ClaimAdjustments.Sum(a => a.Amount) + cp.ServiceLines.Sum(l => l.Adjustments.Sum(a => a.Amount));
        if (cp.ChargeAmount - allCas != cp.PaymentAmount)
            problems.Add(
                $"claim {cp.ClaimId}: charge (CLP03) {cp.ChargeAmount:F2} less adjustments (CAS, claim and lines) {allCas:F2} " +
                $"is {cp.ChargeAmount - allCas:F2} but the claim payment (CLP04) is {cp.PaymentAmount:F2}");
        return problems;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless the 835 balances:
    /// BPR02 = sum of CLP04 - sum of PLB amounts; for every claim with
    /// service lines, sum of SVC03 = CLP04; and, once lines carry CAS,
    /// SVC02 - sum(line CAS) = SVC03 and CLP03 - sum(CAS) = CLP04
    /// (<see cref="AdjustmentBalanceProblems"/>).
    /// </summary>
    public static void EnsureBalanced(
        decimal bprAmount, IEnumerable<ClaimPayment> claimPayments, IEnumerable<ProviderAdjustment> providerAdjustments)
    {
        var claims = claimPayments.ToList();
        var problems = claims.Select(ServiceLineBalanceProblem).Where(p => p != null).Select(p => p!).ToList();
        problems.AddRange(claims.SelectMany(AdjustmentBalanceProblems));

        var expected = claims.Sum(cp => cp.PaymentAmount) - providerAdjustments.Sum(a => a.Amount);
        if (expected != bprAmount)
            problems.Add($"BPR02 is {bprAmount:F2} but the claim payments (CLP04) less provider adjustments (PLB) total {expected:F2}");

        if (problems.Count > 0)
            throw new InvalidOperationException("Cannot generate an unbalanced 835: " + string.Join("; ", problems));
    }

    private static Era835BankDetails BankDetails(TradingPartnerInfo tp)
    {
        ArgumentNullException.ThrowIfNull(tp);
        return new Era835BankDetails
        {
            PayerRoutingNumber = tp.PayerRoutingNumber,
            PayerAccountNumber = tp.PayerAccountNumber,
            OriginatingCompanyId = tp.OriginatingCompanyId,
            OriginatingCompanySupplementalCode = tp.OriginatingCompanySupplementalCode,
            PayeeRoutingNumber = tp.PayeeRoutingNumber,
            PayeeAccountNumber = tp.PayeeAccountNumber,
        };
    }
}
