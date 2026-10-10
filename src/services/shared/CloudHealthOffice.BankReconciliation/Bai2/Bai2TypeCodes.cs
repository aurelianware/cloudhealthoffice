namespace CloudHealthOffice.BankReconciliation.Bai2;

/// <summary>
/// BAI2 type codes used by reconciliation. Banks extend the standard list
/// (900–999 custom codes, and different choices among the standard ones), so
/// every list here is a default that configuration can replace.
/// </summary>
public static class Bai2TypeCodes
{
    public static Bai2Direction DirectionOf(string typeCode) =>
        int.TryParse(typeCode, out var n)
            ? n switch
            {
                >= 100 and <= 399 => Bai2Direction.Credit,
                >= 400 and <= 699 => Bai2Direction.Debit,
                _ => Bai2Direction.Status,
            }
            : Bai2Direction.Status;

    /// <summary>
    /// Credits that are premium cash: lockbox deposits (115 Lockbox Deposit,
    /// 116 Item in Lockbox Deposit), ACH credits (142 ACH Credit Received,
    /// 165 Preauthorized ACH Credit, 166 ACH Settlement, 169 Miscellaneous ACH
    /// Credit), wires (195 Incoming Money Transfer, 208 Incoming Internal
    /// Money Transfer) and deposits (301 Commercial Deposit).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPremiumCashCredits = ["115", "116", "142", "165", "166", "169", "195", "208", "301"];

    /// <summary>Lockbox deposit totals: matched to the lockbox batches of the day.</summary>
    public static readonly IReadOnlyList<string> DefaultLockboxDeposits = ["115"];

    /// <summary>
    /// Debits that settle an ACH file CHO originated (credits to payees): 451
    /// ACH Debit Received, 455 Preauthorized ACH Debit, 466 ACH Settlement,
    /// 469 Miscellaneous ACH Debit.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAchSettlementDebits = ["451", "455", "466", "469"];

    /// <summary>Credits that settle an ACH debit file CHO originated (165, 166, 169).</summary>
    public static readonly IReadOnlyList<string> DefaultAchSettlementCredits = ["165", "166", "169"];

    /// <summary>
    /// ACH returns: 257 Individual ACH Return Item, 258 ACH Reversal Credit,
    /// 266 ACH Return Item or Adjustment Settlement (money back for a returned
    /// credit), and 555 Deposited Item Returned, 564 Individual ACH Return
    /// Item debit (a returned debit). Always flagged for a person; never applied.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAchReturns = ["257", "258", "266", "555", "564"];
}
