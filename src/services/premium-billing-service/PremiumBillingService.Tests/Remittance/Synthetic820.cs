using System.Globalization;
using System.Text;

namespace PremiumBillingService.Tests.Remittance;

/// <summary>Builds synthetic X12 005010X218 820 files. No real payer, member or bank data.</summary>
internal sealed class Synthetic820
{
    public const string Isa =
        "ISA*00*          *00*          *ZZ*SYNTHPAYER     *ZZ*CHOPLAN        *260305*1200*^*00501*000000001*0*T*:~";

    private readonly List<string> _transactions = new();

    public sealed class Transaction
    {
        public string HandlingCode { get; init; } = "C";
        public decimal Amount { get; init; }
        public string Trace { get; init; } = "TRACE0001";
        public string Originator { get; init; } = "1512345678";
        public string PaymentDate { get; init; } = "20260305";
        public List<string> Body { get; } = new();

        public Transaction Organization(string number = "1") { Body.Add($"ENT*{number}*2L*FI*999000111"); return this; }

        public Transaction Individual(string number, string employeeId, string memberId, string last, string first)
        {
            Body.Add($"ENT*{number}*2J*EI*{employeeId}");
            Body.Add($"NM1*IL*1*{last}*{first}****N*{memberId}");
            return this;
        }

        public Transaction Rmr(string? reference, decimal paid, decimal? billed = null, string qualifier = "IK")
        {
            Body.Add($"RMR*{qualifier}*{reference}**{Money(paid)}{(billed.HasValue ? "*" + Money(billed.Value) : string.Empty)}");
            return this;
        }

        public Transaction Coverage(string from, string to) { Body.Add($"DTM*582****RD8*{from}-{to}"); return this; }

        public Transaction Adx(decimal amount, string reason) { Body.Add($"ADX*{Money(amount)}*{reason}"); return this; }
    }

    public Synthetic820 Add(Transaction t, int controlNumber = 0)
    {
        var st = (controlNumber == 0 ? _transactions.Count + 1 : controlNumber).ToString("0000", CultureInfo.InvariantCulture);
        var segments = new List<string>
        {
            $"ST*820*{st}*005010X218",
            $"BPR*{t.HandlingCode}*{Money(t.Amount)}*C*ACH*CCP*01*011000015*DA*0000000001*{t.Originator}**01*021000021*DA*0000000002*{t.PaymentDate}",
            $"TRN*1*{t.Trace}*{t.Originator}",
            "REF*38*SYNTH-GROUP",
            "DTM*582****RD8*20260301-20260331",
            "N1*PE*SYNTHETIC HEALTH PLAN*FI*000000001",
            "N1*PR*SYNTHETIC EMPLOYER INC*FI*000000002",
        };
        segments.AddRange(t.Body);
        segments.Add($"SE*{segments.Count + 1}*{st}");
        _transactions.Add(string.Join("~\n", segments));
        return this;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Isa).Append('\n');
        sb.Append("GS*RA*SYNTHPAYER*CHOPLAN*20260305*1200*1*X*005010X218~\n");
        foreach (var t in _transactions)
            sb.Append(t).Append("~\n");
        sb.Append($"GE*{_transactions.Count}*1~\n");
        sb.Append("IEA*1*000000001~\n");
        return sb.ToString();
    }

    public static string Single(Transaction t) => new Synthetic820().Add(t).ToString();

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
