using System.Text.Json.Serialization;

namespace CloudHealthOffice.BankReconciliation.Bai2;

/// <summary>Whether a BAI2 type code moves money into or out of the account.</summary>
public enum Bai2Direction
{
    /// <summary>Status (balance) codes 001–099, loan and custom codes: no money movement.</summary>
    Status,

    /// <summary>Credit codes 100–399: money in.</summary>
    Credit,

    /// <summary>Debit codes 400–699: money out.</summary>
    Debit,
}

/// <summary>A BAI2 funds type and its availability detail.</summary>
public sealed class Bai2FundsAvailability
{
    /// <summary>0 immediate, 1 one-day, 2 two-or-more-day, S distributed, V value-dated, D distributed by days, Z unknown (or blank).</summary>
    public string FundsType { get; init; } = "Z";

    /// <summary>S: immediate / one-day / two-or-more-day amounts, in minor units.</summary>
    public long? ImmediateAmount { get; init; }
    public long? OneDayAmount { get; init; }
    public long? TwoOrMoreDayAmount { get; init; }

    /// <summary>V: value date (YYMMDD as parsed) and time.</summary>
    public DateOnly? ValueDate { get; init; }
    public string? ValueTime { get; init; }

    /// <summary>D: (days, amount) pairs.</summary>
    public List<Bai2Distribution> Distributions { get; init; } = [];
}

public sealed record Bai2Distribution(int Days, long Amount);

/// <summary>An 03 summary or status amount (type code, amount, item count, funds type).</summary>
public sealed class Bai2Summary
{
    public string TypeCode { get; init; } = string.Empty;

    /// <summary>In minor units (cents for USD). Status amounts may be signed. Null when the field was blank.</summary>
    public long? Amount { get; init; }

    public int? ItemCount { get; init; }

    public Bai2FundsAvailability Availability { get; init; } = new();
}

/// <summary>A 16 transaction detail record (with its 88 continuations).</summary>
public sealed class Bai2Transaction
{
    /// <summary>1-based physical line of the 16 record.</summary>
    public int LineNumber { get; init; }

    /// <summary>0-based position of the transaction within its account.</summary>
    public int Index { get; init; }

    public string TypeCode { get; init; } = string.Empty;

    public Bai2Direction Direction { get; init; }

    /// <summary>In minor units (cents for USD). Never negative: the type code carries the direction.</summary>
    public long Amount { get; init; }

    public Bai2FundsAvailability Availability { get; init; } = new();

    public string? BankReference { get; init; }

    public string? CustomerReference { get; init; }

    /// <summary>Free text, 88 continuations joined. May carry payer names: never log it.</summary>
    public string? Text { get; init; }
}

public sealed class Bai2Account
{
    /// <summary>The account number as the bank sent it. Never log or return it: use <see cref="MaskedAccountNumber"/>.</summary>
    [JsonIgnore]
    public string AccountNumber { get; init; } = string.Empty;

    /// <summary>Last four characters only, e.g. <c>****6789</c>.</summary>
    public string MaskedAccountNumber => AccountMask.Mask(AccountNumber);

    public string Currency { get; init; } = "USD";

    public List<Bai2Summary> Summaries { get; init; } = [];

    public List<Bai2Transaction> Transactions { get; init; } = [];

    /// <summary>49 account control total, minor units.</summary>
    public long ControlTotal { get; init; }

    /// <summary>49 number of records (03 through 49, continuations included).</summary>
    public int RecordCount { get; init; }
}

public sealed class Bai2Group
{
    public string UltimateReceiverId { get; init; } = string.Empty;
    public string OriginatorId { get; init; } = string.Empty;

    /// <summary>1 update, 2 deletion, 3 correction, 4 test only.</summary>
    public string GroupStatus { get; init; } = "1";

    public DateOnly AsOfDate { get; init; }
    public string? AsOfTime { get; init; }
    public string Currency { get; init; } = "USD";

    /// <summary>1 interim previous-day, 2 final previous-day, 3 interim same-day, 4 final same-day.</summary>
    public string? AsOfDateModifier { get; init; }

    public List<Bai2Account> Accounts { get; init; } = [];

    public long ControlTotal { get; init; }
    public int RecordCount { get; init; }
}

/// <summary>A parsed BAI2 file. Every control total and record count has been checked.</summary>
public sealed class Bai2File
{
    public string SenderId { get; init; } = string.Empty;
    public string ReceiverId { get; init; } = string.Empty;
    public DateOnly CreationDate { get; init; }
    public string CreationTime { get; init; } = string.Empty;

    /// <summary>01 file identification number: unique per sender, receiver and creation date.</summary>
    public string FileId { get; init; } = string.Empty;

    public string Version { get; init; } = "2";

    public List<Bai2Group> Groups { get; init; } = [];

    public long ControlTotal { get; init; }
    public int RecordCount { get; init; }

    /// <summary>The bank's identity of the file: sender, receiver, creation date and file id.</summary>
    public string BankFileKey => $"{SenderId}|{ReceiverId}|{CreationDate:yyyyMMdd}|{FileId}";

    public IEnumerable<Bai2Transaction> AllTransactions => Groups.SelectMany(g => g.Accounts).SelectMany(a => a.Transactions);
}

/// <summary>The file is not valid BAI2: it is rejected whole, and nothing in it is reconciled.</summary>
public sealed class Bai2FormatException : FormatException
{
    public Bai2FormatException(IReadOnlyList<string> errors)
        : base(string.Join("; ", errors)) => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Account numbers are shown by their last four characters only.</summary>
public static class AccountMask
{
    public static string Mask(string? accountNumber)
    {
        var value = (accountNumber ?? string.Empty).Trim();
        return value.Length <= 4 ? new string('*', value.Length) : "****" + value[^4..];
    }
}
