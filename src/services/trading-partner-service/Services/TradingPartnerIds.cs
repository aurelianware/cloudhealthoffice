using System.Text;

namespace CloudHealthOffice.TradingPartnerService.Services;

/// <summary>
/// Record ids for trading partners.
/// <para>
/// The old id <c>{partnerId}-{tenant}-{environment}</c> was ambiguous: tenant
/// <c>b</c> with partner <c>x-a</c> and tenant <c>a-b</c> with partner
/// <c>x</c> (same environment) both produced <c>x-a-b-prod</c>, so one
/// tenant's create failed on the other's record. A new id encodes each part in
/// base64url (alphabet <c>A-Z a-z 0-9 - _</c>) and joins them with <c>.</c>,
/// which base64url never contains, so distinct (tenant, partner, environment)
/// triples always get distinct ids. The id is still deterministic, so the
/// database's own id uniqueness refuses a second create of the same triple.
/// </para>
/// <para>
/// Records are looked up by their (tenantId, tradingPartnerId, environment)
/// fields, never by recomputing an id, so records saved with the old id keep
/// working. Mongo also has a unique index on those three fields.
/// </para>
/// </summary>
public static class TradingPartnerIds
{
    public const string Prefix = "tp.";

    public static string For(string tenantId, string tradingPartnerId, string environment)
        => Prefix + Encode(tenantId) + "." + Encode(tradingPartnerId) + "." + Encode(environment);

    private static string Encode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A trading partner with this (tenant, partner id, environment) already exists.</summary>
public sealed class DuplicateTradingPartnerException : Exception
{
    public DuplicateTradingPartnerException(string tradingPartnerId, string environment, Exception? inner = null)
        : base($"Trading partner {tradingPartnerId} ({environment}) already exists.", inner)
    {
    }
}
