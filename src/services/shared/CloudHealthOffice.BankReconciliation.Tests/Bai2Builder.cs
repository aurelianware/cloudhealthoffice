// Synthetic BAI2 files for tests. Linked into premium-billing-service and
// payment-service test projects too. Account numbers, ids and names are made up.
namespace CloudHealthOffice.BankReconciliation.Testing;

/// <summary>
/// Builds a BAI2 file with correct control totals and record counts, which a
/// test can then break on purpose (<see cref="AccountTotalDelta"/> etc.).
/// </summary>
public sealed class Bai2Builder
{
    public sealed record Tx(
        string TypeCode,
        long Amount,
        string FundsType = "0",
        string BankReference = "",
        string CustomerReference = "",
        string? Text = null,
        string[]? TextContinuations = null,
        string AvailabilityFields = "");

    public sealed class Account
    {
        public string Number { get; init; } = "0000123456789";
        public string Currency { get; init; } = "USD";
        public List<(string TypeCode, long Amount)> Summaries { get; init; } = [("010", 100000), ("015", 250000)];
        public List<Tx> Transactions { get; init; } = [];
    }

    public sealed class Group
    {
        public string AsOfDate { get; init; } = "261009";
        public string Status { get; init; } = "1";
        public List<Account> Accounts { get; init; } = [];
    }

    public string SenderId { get; init; } = "SYNBANK01";
    public string ReceiverId { get; init; } = "CHOTENANT";
    public string CreationDate { get; init; } = "261010";
    public string FileId { get; init; } = "1";
    public string Version { get; init; } = "2";
    public List<Group> Groups { get; init; } = [];

    public long AccountTotalDelta { get; init; }
    public long GroupTotalDelta { get; init; }
    public long FileTotalDelta { get; init; }
    public int AccountRecordDelta { get; init; }
    public int FileRecordDelta { get; init; }

    /// <summary>One group, one account, these transactions.</summary>
    public static Bai2Builder Single(params Tx[] transactions) => new()
    {
        Groups = [new Group { Accounts = [new Account { Transactions = [.. transactions] }] }],
    };

    public string Build()
    {
        var lines = new List<string> { $"01,{SenderId},{ReceiverId},{CreationDate},0600,{FileId},,,{Version}/" };
        long fileTotal = 0;
        foreach (var g in Groups)
        {
            var groupLines = new List<string> { $"02,{ReceiverId},{SenderId},{g.Status},{g.AsOfDate},2400,USD,2/" };
            long groupTotal = 0;
            foreach (var a in g.Accounts)
            {
                var accountLines = new List<string>
                {
                    $"03,{a.Number},{a.Currency}," + string.Join(",", a.Summaries.Select(s => $"{s.TypeCode},{s.Amount},,")) + "/",
                };
                long accountTotal = a.Summaries.Sum(s => s.Amount);
                foreach (var t in a.Transactions)
                {
                    var availability = t.AvailabilityFields.Length > 0 ? "," + t.AvailabilityFields : string.Empty;
                    var head = $"16,{t.TypeCode},{t.Amount},{t.FundsType}{availability},{t.BankReference},{t.CustomerReference}";
                    accountLines.Add(t.Text is null && t.TextContinuations is null ? head + "/" : $"{head},{t.Text}");
                    foreach (var c in t.TextContinuations ?? [])
                        accountLines.Add($"88,{c}");
                    accountTotal += t.Amount;
                }
                accountTotal += AccountTotalDelta;
                accountLines.Add($"49,{accountTotal},{accountLines.Count + 1 + AccountRecordDelta}/");
                groupLines.AddRange(accountLines);
                groupTotal += accountTotal;
            }
            groupTotal += GroupTotalDelta;
            groupLines.Add($"98,{groupTotal},{g.Accounts.Count},{groupLines.Count + 1}/");
            lines.AddRange(groupLines);
            fileTotal += groupTotal;
        }
        fileTotal += FileTotalDelta;
        lines.Add($"99,{fileTotal},{Groups.Count},{lines.Count + 1 + FileRecordDelta}/");
        return string.Join("\n", lines) + "\n";
    }

    public byte[] Bytes() => System.Text.Encoding.ASCII.GetBytes(Build());
}
