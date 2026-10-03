using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ProviderService.Models;

/// <summary>Where a proposed bank-account change stands.</summary>
public enum BankAccountChangeStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3
}

/// <summary>
/// A proposed change to a provider's bank account. Changing the account
/// redirects payments, so a change never applies when it is proposed: a
/// different user holding <c>payments:approve</c> must approve it first.
///
/// <para>
/// While <see cref="Status"/> is <see cref="BankAccountChangeStatus.Pending"/>,
/// <see cref="Proposed"/> holds the full account details, stored the same way
/// as the provider's active account. Once the change is decided (approved,
/// rejected or cancelled) the full numbers are dropped from the change and
/// only the masked view (last 4 digits) is kept; an approved account's full
/// numbers live only on the record's active account. API responses always
/// show the masked view.
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class PendingBankAccountChange
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Provider chain key (<see cref="Provider.ProviderId"/>).</summary>
    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("providerNpi")]
    public string? ProviderNpi { get; set; }

    /// <summary>The proposed account. Full numbers only while Pending.</summary>
    [JsonPropertyName("proposed")]
    public ProviderBankAccount? Proposed { get; set; }

    /// <summary>
    /// The active account's change id when this change was proposed
    /// (<see cref="ProviderBankAccountRecord.ActiveChangeId"/>). Approval is
    /// refused (409) when the active account has changed since.
    /// </summary>
    [JsonPropertyName("baseActiveChangeId")]
    public string? BaseActiveChangeId { get; set; }

    /// <summary>The token subject that proposed the change.</summary>
    [JsonPropertyName("requestedBy")]
    public string RequestedBy { get; set; } = string.Empty;

    [JsonPropertyName("requestedAt")]
    public DateTime RequestedAt { get; set; }

    /// <summary>The write that proposed it (bank-account PUT, provider create, provider update, ...).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("status")]
    public BankAccountChangeStatus Status { get; set; } = BankAccountChangeStatus.Pending;

    [JsonPropertyName("decidedBy")]
    public string? DecidedBy { get; set; }

    [JsonPropertyName("decidedAt")]
    public DateTime? DecidedAt { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>The account this change replaced when it was approved (masked).</summary>
    [JsonPropertyName("previousAccount")]
    public ProviderBankAccount? PreviousAccount { get; set; }
}

/// <summary>
/// One document per provider (per tenant) holding the provider's active bank
/// account, the pending change (at most one) and the history of decided
/// changes. Every write replaces the whole document conditionally on
/// <see cref="Revision"/>, so approving a change and switching the active
/// account are one atomic step.
///
/// <para>
/// Providers whose account was set before dual control have it on the
/// provider row (<see cref="Provider.BankAccount"/>). Until the first change is
/// proposed there is no record and that row account is the active one; the
/// first proposal seeds <see cref="Active"/> from it (<see cref="LegacyChangeId"/>).
/// </para>
/// </summary>
[BsonIgnoreExtraElements]
public class ProviderBankAccountRecord
{
    /// <summary><see cref="ActiveChangeId"/> of an account carried over from the provider row.</summary>
    public const string LegacyChangeId = "legacy-provider-row";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("providerNpi")]
    public string? ProviderNpi { get; set; }

    /// <summary>The approved account payments use. Null when none is approved.</summary>
    [JsonPropertyName("active")]
    public ProviderBankAccount? Active { get; set; }

    /// <summary>The change that made <see cref="Active"/> active, or <see cref="LegacyChangeId"/>.</summary>
    [JsonPropertyName("activeChangeId")]
    public string? ActiveChangeId { get; set; }

    [JsonPropertyName("activeApprovedBy")]
    public string? ActiveApprovedBy { get; set; }

    [JsonPropertyName("activeApprovedAt")]
    public DateTime? ActiveApprovedAt { get; set; }

    /// <summary>Every change, oldest first. At most one is Pending.</summary>
    [JsonPropertyName("changes")]
    public List<PendingBankAccountChange> Changes { get; set; } = new();

    /// <summary>Optimistic-concurrency counter, incremented by every save.</summary>
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore]
    [BsonIgnore]
    public PendingBankAccountChange? Pending
        => Changes.FirstOrDefault(c => c.Status == BankAccountChangeStatus.Pending);

    public static string KeyFor(string tenantId, string providerId) => $"{tenantId}|{providerId}";
}

/// <summary>A bank-account change as the API shows it: account numbers masked.</summary>
public class BankAccountChangeView
{
    public string Id { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderNpi { get; set; }
    public BankAccountChangeStatus Status { get; set; }
    public ProviderBankAccount? Proposed { get; set; }
    public ProviderBankAccount? PreviousAccount { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public string? Source { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? Reason { get; set; }

    public static BankAccountChangeView From(PendingBankAccountChange change) => new()
    {
        Id = change.Id,
        ProviderId = change.ProviderId,
        ProviderNpi = change.ProviderNpi,
        Status = change.Status,
        Proposed = BankAccountMasking.Mask(change.Proposed),
        PreviousAccount = BankAccountMasking.Mask(change.PreviousAccount),
        RequestedBy = change.RequestedBy,
        RequestedAt = change.RequestedAt,
        Source = change.Source,
        DecidedBy = change.DecidedBy,
        DecidedAt = change.DecidedAt,
        Reason = change.Reason,
    };
}

/// <summary>Body of a reject or cancel.</summary>
public class BankAccountChangeDecisionRequest
{
    public string? Reason { get; set; }
}

/// <summary>Masking and comparison of bank-account details.</summary>
public static class BankAccountMasking
{
    /// <summary>
    /// A copy without the full routing, account and tax numbers: what the
    /// masked bank-account read has always returned.
    /// </summary>
    public static ProviderBankAccount? Mask(ProviderBankAccount? account)
    {
        if (account == null) return null;
        return new ProviderBankAccount
        {
            EftEnabled = account.EftEnabled,
            PreferredDisbursementMethod = account.PreferredDisbursementMethod,
            AccountType = account.AccountType,
            AccountHolderName = account.AccountHolderName,
            StripeConnectedAccountId = account.StripeConnectedAccountId,
            RoutingNumberLast4 = account.RoutingNumberLast4 ?? Last4(account.RoutingNumber),
            AccountNumberLast4 = account.AccountNumberLast4 ?? Last4(account.AccountNumber),
            W9OnFile = account.W9OnFile,
            TaxIdType = account.TaxIdType,
            // RoutingNumber, AccountNumber, TaxId intentionally omitted
        };
    }

    /// <summary>Sets the last-4 display fields from the full numbers when they are given.</summary>
    public static void DeriveLast4(ProviderBankAccount account)
    {
        if (!string.IsNullOrEmpty(account.RoutingNumber))
            account.RoutingNumberLast4 = Last4(account.RoutingNumber);
        if (!string.IsNullOrEmpty(account.AccountNumber))
            account.AccountNumberLast4 = Last4(account.AccountNumber);
    }

    /// <summary>Whether two accounts carry the same payment details.</summary>
    public static bool SameAccount(ProviderBankAccount? a, ProviderBankAccount? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a.EftEnabled == b.EftEnabled
            && a.PreferredDisbursementMethod == b.PreferredDisbursementMethod
            && a.AccountType == b.AccountType
            && string.Equals(a.RoutingNumber ?? string.Empty, b.RoutingNumber ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.AccountNumber ?? string.Empty, b.AccountNumber ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.AccountHolderName ?? string.Empty, b.AccountHolderName ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.StripeConnectedAccountId ?? string.Empty, b.StripeConnectedAccountId ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(a.TaxId ?? string.Empty, b.TaxId ?? string.Empty, StringComparison.Ordinal)
            && a.TaxIdType == b.TaxIdType
            && a.W9OnFile == b.W9OnFile;
    }

    private static string? Last4(string? value)
        => string.IsNullOrEmpty(value) ? null : value.Length >= 4 ? value[^4..] : value;
}
