using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson.Serialization.Attributes;

namespace CloudHealthOffice.BankReconciliation;

public enum BankStatementStatus
{
    /// <summary>Parsed, every control total matched; its transactions are stored.</summary>
    Accepted,

    /// <summary>Not valid BAI2, a control total did not match, or the bank file id was already used by a different file. Nothing in it is reconciled.</summary>
    Rejected,
}

/// <summary>
/// One received BAI2 file, stored once and never changed (append-only). The
/// id is derived from the tenant and the file's SHA-256, so the same bytes are
/// one record; <see cref="BankFileKey"/> (sender, receiver, creation date,
/// file id) is unique among accepted files, so a different file reusing the
/// bank's file id is refused. The file text is held only as
/// FieldProtection ciphertext (it carries account numbers).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class BankStatementFile
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the bytes received.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Set on accepted files only, unique per tenant: <see cref="BankFileKeyBase"/>
    /// for the first file with that bank id, <c>{base}#2</c>, <c>#3</c>... for
    /// corrected files accepted after an approved supersession.
    /// </summary>
    public string? BankFileKey { get; set; }

    /// <summary>The bank's identity of the file: <c>sender|receiver|yyyyMMdd|fileId</c> (accepted files).</summary>
    public string? BankFileKeyBase { get; set; }

    public string? FileName { get; set; }
    public long ByteSize { get; set; }
    public BankStatementStatus Status { get; set; }
    public List<string> Errors { get; set; } = [];

    public string? SenderId { get; set; }
    public string? ReceiverId { get; set; }
    public DateTime? CreationDate { get; set; }
    public string? FileIdNumber { get; set; }
    public int GroupCount { get; set; }
    public int AccountCount { get; set; }
    public int TransactionCount { get; set; }

    /// <summary>99 file control total, minor units.</summary>
    public long ControlTotal { get; set; }

    /// <summary>Accounts reported, masked to their last four characters.</summary>
    public List<string> MaskedAccounts { get; set; } = [];

    /// <summary>The file text, FieldProtection ciphertext bound to tenant, this id and the field.</summary>
    public string ProtectedContent { get; set; } = string.Empty;

    /// <summary>The service that first received it (premium-billing-service, payment-service).</summary>
    public string ReceivedByService { get; set; } = string.Empty;

    public string? ReceivedBy { get; set; }
    public DateTime ReceivedAt { get; set; }

    /// <summary>A copy without <see cref="ProtectedContent"/>, for responses.</summary>
    public BankStatementFile WithoutContent()
    {
        var copy = (BankStatementFile)MemberwiseClone();
        copy.ProtectedContent = string.Empty;
        copy.Errors = [.. Errors];
        copy.MaskedAccounts = [.. MaskedAccounts];
        return copy;
    }

    public static string IdFor(string tenantId, string sha256) => "bst-" + ShortHash($"{tenantId}|{sha256}");

    internal static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
}

/// <summary>One 16 record of an accepted statement. Holds the masked account only.</summary>
[BsonIgnoreExtraElements]
public sealed class BankTransaction
{
    /// <summary><c>{statementId}-{group}-{account}-{index}</c>: stable, so storing and reconciling are idempotent.</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string StatementId { get; set; } = string.Empty;
    public int LineNumber { get; set; }

    /// <summary>02 group status: 1 update, 2 deletion, 3 correction, 4 test only.</summary>
    public string GroupStatus { get; set; } = "1";

    /// <summary>02 as-of date (the bank's posting date for the transaction).</summary>
    public DateTime AsOfDate { get; set; }

    /// <summary>02 as-of-date modifier: 1 interim previous-day, 2 final previous-day, 3 interim same-day, 4 final same-day.</summary>
    public string? AsOfDateModifier { get; set; }

    public string Currency { get; set; } = "USD";
    public string MaskedAccount { get; set; } = string.Empty;

    /// <summary>Last four characters of the account, for configuration filters.</summary>
    public string AccountSuffix { get; set; } = string.Empty;

    public string TypeCode { get; set; } = string.Empty;
    public Bai2.Bai2Direction Direction { get; set; }

    /// <summary>Minor units (cents for USD).</summary>
    public long AmountMinor { get; set; }

    /// <summary>The amount in currency units.</summary>
    public decimal Amount { get; set; }

    public string FundsType { get; set; } = "Z";

    /// <summary>Funds type V value date, when given.</summary>
    public DateTime? ValueDate { get; set; }

    public string? BankReference { get; set; }
    public string? CustomerReference { get; set; }

    /// <summary>16 text with its 88 continuations. May name payers: never logged.</summary>
    public string? Text { get; set; }
}

public enum SupersessionStatus
{
    /// <summary>One person asked; a second must approve.</summary>
    Requested,

    /// <summary>Approved by a second person: a corrected file with the same bank file id is accepted.</summary>
    Approved,
}

/// <summary>
/// An audited, two-person replacement of an accepted statement by a corrected
/// file that reuses the bank's file id. Refused once any line of the
/// statement was applied, matched or settled. On approval, the statement's
/// open items are dismissed.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class BankStatementSupersession
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
    public string StatementId { get; set; } = string.Empty;
    public string BankFileKeyBase { get; set; } = string.Empty;
    public SupersessionStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    /// <summary>The corrected file accepted in its place, once received.</summary>
    public string? ReplacedByStatementId { get; set; }

    public static string IdFor(string tenantId, string statementId) => $"{tenantId}|{statementId}";

    public BankStatementSupersession Copy() => (BankStatementSupersession)MemberwiseClone();
}
