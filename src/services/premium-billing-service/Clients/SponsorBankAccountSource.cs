using PremiumBillingService.Models;

namespace PremiumBillingService.Clients;

/// <summary>
/// Where premium billing gets a sponsor's bank details for auto-debit (EFT/ACH
/// drafts and NACHA debit files).
/// </summary>
public interface ISponsorBankAccountSource
{
    Task<SponsorBankAccountLookup> GetAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default);
}

public enum SponsorBankAccountLookupStatus
{
    /// <summary>The sponsor's bank details are known.</summary>
    Found,

    /// <summary>
    /// The system of record says the sponsor is not enrolled in auto-debit.
    /// A normal state: the sponsor pays another way.
    /// </summary>
    NotEnrolled,

    /// <summary>
    /// Nobody could say whether the sponsor is enrolled or what its account is.
    /// Never a normal state: the item needs attention.
    /// </summary>
    Unavailable
}

public sealed record SponsorBankAccountLookup(
    SponsorBankAccountLookupStatus Status, SponsorBankAccount? Account, string? Reason)
{
    public static SponsorBankAccountLookup Found(SponsorBankAccount account) =>
        account.EftEnabled
            ? new(SponsorBankAccountLookupStatus.Found, account, null)
            : new(SponsorBankAccountLookupStatus.NotEnrolled, account, null);

    public static SponsorBankAccountLookup NotEnrolled() => new(SponsorBankAccountLookupStatus.NotEnrolled, null, null);

    public static SponsorBankAccountLookup Unavailable(string reason) =>
        new(SponsorBankAccountLookupStatus.Unavailable, null, reason);
}

/// <summary>
/// The production source today. premium-billing-service used to call
/// <c>GET sponsor-service/api/v1/sponsors/{group}/bank-account</c>, which does
/// not exist: every lookup was a 404, read as "no bank account", and every
/// auto-debit was silently skipped or refused as "EFT not enabled". Sponsor
/// bank details have no system of record anywhere in CHO (sponsor-service's
/// BillingInfo holds only a free-text PaymentMethod and a billing account
/// number). Until one exists, every lookup answers <see cref="SponsorBankAccountLookupStatus.Unavailable"/>
/// so the draft is refused loudly as needing attention.
/// </summary>
public sealed class UnavailableSponsorBankAccountSource : ISponsorBankAccountSource
{
    public const string Reason =
        "Sponsor bank details are unavailable: no CHO service stores sponsor bank accounts " +
        "(sponsor-service has no bank-account endpoint), so auto-debit cannot be performed. Needs attention.";

    public Task<SponsorBankAccountLookup> GetAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default)
        => Task.FromResult(SponsorBankAccountLookup.Unavailable(Reason));
}
