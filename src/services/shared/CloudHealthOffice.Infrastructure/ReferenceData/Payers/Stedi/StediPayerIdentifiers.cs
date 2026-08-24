namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;

/// <summary>
/// Well-known external-identifier system/type strings written by the Stedi
/// directory mapper. These are values stored on
/// <see cref="PayerExternalIdentifier"/>, not property names on the canonical
/// payer model.
/// </summary>
internal static class StediPayerIdentifiers
{
    public const string System = "stedi";
    public const string IdType = "id";
    public const string TradingPartnerServiceIdType = "tradingPartnerServiceId";
    public const string PrimaryPayerIdType = "primaryPayerId";
}
