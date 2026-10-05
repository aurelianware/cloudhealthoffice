using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace SponsorService.Models;

/// <summary>How premium billing debits the sponsor (matches premium-billing's EftMethod).</summary>
public enum SponsorDebitMethod
{
    Nacha = 0,
    StripeAch = 1
}

public enum SponsorBankAccountType
{
    Checking = 0,
    Savings = 1
}

/// <summary>Where a proposed bank-account change stands.</summary>
public enum SponsorBankAccountChangeStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3
}

/// <summary>
/// A sponsor's bank account and auto-debit enrollment: what premium billing
/// debits. Changed only through dual control (propose, then a different user
/// with payments:approve approves).
///
/// <para>
/// In storage, <see cref="RoutingNumber"/> and <see cref="AccountNumber"/>
/// hold encrypted values (<c>enc:v1:...</c>, see IFieldProtector); in
/// memory, after a read, they hold the plaintext. API responses carry them
/// only from the service-only full read; every other response is masked
/// (<see cref="SponsorBankAccountMasking.Mask"/>).
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class SponsorBankAccountDetails
{
    /// <summary>Whether the sponsor is enrolled in auto-debit. False: premium billing skips the sponsor (a normal state).</summary>
    public bool EftEnabled { get; set; }

    public SponsorDebitMethod? PreferredMethod { get; set; }

    /// <summary>9-digit ABA routing number. Encrypted at rest.</summary>
    [StringLength(200)]
    public string? RoutingNumber { get; set; }

    /// <summary>Bank account number. Encrypted at rest.</summary>
    [StringLength(400)]
    public string? AccountNumber { get; set; }

    public SponsorBankAccountType AccountType { get; set; } = SponsorBankAccountType.Checking;

    [StringLength(200)]
    public string? AccountHolderName { get; set; }

    /// <summary>Stripe customer id (Stripe ACH only).</summary>
    [StringLength(200)]
    public string? StripeCustomerId { get; set; }

    /// <summary>Stripe bank-account payment method id (Stripe ACH only).</summary>
    [StringLength(200)]
    public string? StripePaymentMethodId { get; set; }

    /// <summary>Last 4 digits of the routing number (display).</summary>
    public string? RoutingNumberLast4 { get; set; }

    /// <summary>Last 4 digits of the account number (display).</summary>
    public string? AccountNumberLast4 { get; set; }
}

/// <summary>
/// A proposed change to a sponsor's bank account. While Pending,
/// <see cref="Proposed"/> holds the full (encrypted at rest) numbers; once
/// decided they are dropped and only the masked view is kept. An approved
/// account's numbers live only on the record's active account.
/// </summary>
[BsonIgnoreExtraElements]
public class SponsorBankAccountChange
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    public string GroupNumber { get; set; } = string.Empty;

    public SponsorBankAccountDetails? Proposed { get; set; }

    /// <summary>
    /// The active account's change id when this change was proposed.
    /// Approval is refused (409) when the active account changed since.
    /// </summary>
    public string? BaseActiveChangeId { get; set; }

    /// <summary>True when the proposal reused the active account's numbers (only enrollment or Stripe ids changed).</summary>
    public bool NumbersCarriedOver { get; set; }

    /// <summary>Token subject that proposed the change.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }

    public SponsorBankAccountChangeStatus Status { get; set; } = SponsorBankAccountChangeStatus.Pending;

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? Reason { get; set; }

    /// <summary>The account this change replaced when it was approved (masked).</summary>
    public SponsorBankAccountDetails? PreviousAccount { get; set; }
}

/// <summary>
/// One document per sponsor per tenant (collection/container
/// <c>SponsorBankAccounts</c>): the active approved account, at most one
/// pending change, and the history. Every write replaces the whole document
/// conditionally on <see cref="Revision"/> (Mongo filtered replace, Cosmos
/// If-Match), so approving a change and switching the active account are one
/// atomic step.
/// </summary>
[BsonIgnoreExtraElements]
public class SponsorBankAccountRecord
{
    public string Id { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// The sponsor document this record belongs to. A record whose sponsor id
    /// no longer matches (sponsor deleted and re-created under the same group
    /// number) is not used for debits.
    /// </summary>
    public string SponsorId { get; set; } = string.Empty;

    /// <summary>The approved account premium billing debits. Null until a change is approved.</summary>
    public SponsorBankAccountDetails? Active { get; set; }

    public string? ActiveChangeId { get; set; }

    public string? ActiveApprovedBy { get; set; }

    public DateTime? ActiveApprovedAt { get; set; }

    /// <summary>Every change, oldest first. At most one is Pending.</summary>
    public List<SponsorBankAccountChange> Changes { get; set; } = new();

    /// <summary>Optimistic-concurrency counter, incremented by every save.</summary>
    public long Revision { get; set; }

    public DateTime UpdatedAt { get; set; }

    public SponsorBankAccountChange? GetPending()
        => Changes.FirstOrDefault(c => c.Status == SponsorBankAccountChangeStatus.Pending);

    public static string KeyFor(string tenantId, string groupNumber) => $"{tenantId}|{groupNumber}";
}

/// <summary>A bank-account change as the API shows it: numbers masked.</summary>
public class SponsorBankAccountChangeView
{
    public string Id { get; set; } = string.Empty;
    public string GroupNumber { get; set; } = string.Empty;
    public SponsorBankAccountChangeStatus Status { get; set; }
    public SponsorBankAccountDetails? Proposed { get; set; }
    public bool NumbersCarriedOver { get; set; }
    public SponsorBankAccountDetails? PreviousAccount { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Reason { get; set; }

    public static SponsorBankAccountChangeView From(SponsorBankAccountChange change) => new()
    {
        Id = change.Id,
        GroupNumber = change.GroupNumber,
        Status = change.Status,
        Proposed = SponsorBankAccountMasking.Mask(change.Proposed),
        NumbersCarriedOver = change.NumbersCarriedOver,
        PreviousAccount = SponsorBankAccountMasking.Mask(change.PreviousAccount),
        RequestedBy = change.RequestedBy,
        RequestedAt = change.RequestedAt,
        DecidedBy = change.DecidedBy,
        DecidedAt = change.DecidedAt,
        Reason = change.Reason,
    };
}

/// <summary>The masked read: the active approved account and whether a change awaits approval.</summary>
public class SponsorBankAccountView
{
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>The approved account, masked. Null when none is approved.</summary>
    public SponsorBankAccountDetails? Active { get; set; }

    public string? ActiveApprovedBy { get; set; }

    public DateTime? ActiveApprovedAt { get; set; }

    /// <summary>The pending change, masked, if any.</summary>
    public SponsorBankAccountChangeView? Pending { get; set; }
}

/// <summary>Body of a proposal: the complete account and enrollment as they should be after approval.</summary>
public class ProposeSponsorBankAccountRequest
{
    public bool EftEnabled { get; set; }

    public SponsorDebitMethod? PreferredMethod { get; set; }

    /// <summary>
    /// Leave both <see cref="RoutingNumber"/> and <see cref="AccountNumber"/>
    /// out to keep the active account's numbers (e.g. to change only the
    /// enrollment). Give both to change them.
    /// </summary>
    [StringLength(9)]
    public string? RoutingNumber { get; set; }

    [StringLength(17)]
    public string? AccountNumber { get; set; }

    public SponsorBankAccountType AccountType { get; set; } = SponsorBankAccountType.Checking;

    [StringLength(200)]
    public string? AccountHolderName { get; set; }

    [StringLength(200)]
    public string? StripeCustomerId { get; set; }

    [StringLength(200)]
    public string? StripePaymentMethodId { get; set; }
}

/// <summary>Body of an approve, reject or cancel.</summary>
public class SponsorBankAccountDecisionRequest
{
    [StringLength(500)]
    public string? Reason { get; set; }
}

/// <summary>Masking and validation of sponsor bank details.</summary>
public static class SponsorBankAccountMasking
{
    /// <summary>A copy without the full routing and account numbers (last 4 only).</summary>
    public static SponsorBankAccountDetails? Mask(SponsorBankAccountDetails? account)
    {
        if (account == null) return null;
        return new SponsorBankAccountDetails
        {
            EftEnabled = account.EftEnabled,
            PreferredMethod = account.PreferredMethod,
            AccountType = account.AccountType,
            AccountHolderName = account.AccountHolderName,
            StripeCustomerId = account.StripeCustomerId,
            StripePaymentMethodId = account.StripePaymentMethodId,
            RoutingNumberLast4 = account.RoutingNumberLast4,
            AccountNumberLast4 = account.AccountNumberLast4,
            // RoutingNumber and AccountNumber intentionally omitted
        };
    }

    public static string? Last4(string? value)
        => string.IsNullOrEmpty(value) ? null : value.Length >= 4 ? value[^4..] : value;

    /// <summary>"****1234" for a value, null for none.</summary>
    public static string? Masked(string? value)
        => string.IsNullOrEmpty(value) ? value : "****" + Last4(value);

    /// <summary>A 9-digit ABA routing number with a valid check digit.</summary>
    public static bool IsValidRoutingNumber(string? value)
    {
        if (value is not { Length: 9 } || !value.All(char.IsAsciiDigit)) return false;
        var d = value.Select(c => c - '0').ToArray();
        var sum = 3 * (d[0] + d[3] + d[6]) + 7 * (d[1] + d[4] + d[7]) + (d[2] + d[5] + d[8]);
        return sum % 10 == 0;
    }

    /// <summary>4 to 17 digits (the NACHA DFI account number field).</summary>
    public static bool IsValidAccountNumber(string? value)
        => value is { Length: >= 4 and <= 17 } && value.All(char.IsAsciiDigit);
}
