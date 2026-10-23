using System.Text.Json.Serialization;
using CloudHealthOffice.PlanConfiguration;

namespace TenantService.Hierarchy;

/// <summary>
/// A legal entity of the tenant (plan-configuration.md §3). One per tenant by
/// default, created automatically; a second one matters when an organization
/// runs an HMO and an insurance company with different filings, payer ids and
/// letterhead.
///
/// <para>
/// The legal-entity fields here are where those details belong. They are not
/// yet what the sending services read: NACHA origination, the mailing name and
/// address and the payer ids are still taken from tenant configuration until
/// step 2b moves each consumer over (plan-configuration.md §12.2). Setting
/// them on a carrier today records them; it does not change what goes out on a
/// file or a letter.
/// </para>
/// </summary>
public sealed class Carrier
{
    /// <summary><c>{tenantId}|{code}</c>, so a tenant cannot hold two carriers with one code.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The code within the tenant, upper case, e.g. <c>HMO</c>. The default carrier's is <c>DEFAULT</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>What staff call it.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The registered legal name, for filings and letters.</summary>
    [JsonPropertyName("legalName")]
    public string? LegalName { get; set; }

    /// <summary>Federal employer identification number, digits only.</summary>
    [JsonPropertyName("taxId")]
    public string? TaxId { get; set; }

    /// <summary>NAIC company code, where the entity has one.</summary>
    [JsonPropertyName("naicCode")]
    public string? NaicCode { get; set; }

    /// <summary>Payer ids this entity is known by, keyed by transaction (<c>837</c>, <c>270</c>, <c>835</c>).</summary>
    [JsonPropertyName("payerIds")]
    public Dictionary<string, string> PayerIds { get; set; } = new();

    /// <summary>The NACHA immediate origin (routing number or <c>1</c> plus a tax id) this entity originates under.</summary>
    [JsonPropertyName("nachaImmediateOrigin")]
    public string? NachaImmediateOrigin { get; set; }

    /// <summary>The NACHA company identification this entity's batches carry.</summary>
    [JsonPropertyName("nachaCompanyIdentification")]
    public string? NachaCompanyIdentification { get; set; }

    /// <summary>The name and address used on letters and ID cards.</summary>
    [JsonPropertyName("correspondenceName")]
    public string? CorrespondenceName { get; set; }

    [JsonPropertyName("correspondenceAddress")]
    public Models.Address? CorrespondenceAddress { get; set; }

    /// <summary>False retires the carrier: its programs stay readable, nothing new may be filed under it.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    /// <summary>True for the carrier created automatically with the tenant. It cannot be deactivated while it is the only one.</summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    /// <summary>Optimistic concurrency, as on <see cref="Models.Tenant"/>. Never serialized.</summary>
    [JsonIgnore]
    public long Revision { get; set; }

    public static string IdFor(string tenantId, string code) => $"{tenantId}|{code}";
}

/// <summary>
/// A line of business in a state, under a carrier: "TX STAR", "AZ Commercial
/// Large Group" (plan-configuration.md §3). The record that makes a line of
/// business a thing rather than a label, so that a rule set, a setting and a
/// filing can hang off it.
/// </summary>
public sealed class PlanProgram
{
    /// <summary><c>{tenantId}|{carrierCode}|{code}</c>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("carrierId")]
    public string CarrierId { get; set; } = string.Empty;

    /// <summary>The code within the carrier, upper case: <c>STAR</c>, <c>STARKIDS</c>, <c>LARGEGROUP</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>A canonical line-of-business type name (<see cref="LinesOfBusiness"/>).</summary>
    [JsonPropertyName("lineOfBusiness")]
    public string LineOfBusiness { get; set; } = string.Empty;

    /// <summary>The two-letter state the program operates in, upper case.</summary>
    [JsonPropertyName("stateCode")]
    public string StateCode { get; set; } = string.Empty;

    /// <summary>The state or federal contract the program runs under, where there is one.</summary>
    [JsonPropertyName("regulatoryContractNumber")]
    public string? RegulatoryContractNumber { get; set; }

    [JsonPropertyName("effectiveFrom")]
    public DateTime? EffectiveFrom { get; set; }

    [JsonPropertyName("effectiveTo")]
    public DateTime? EffectiveTo { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("createdBy")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; set; }

    [JsonIgnore]
    public long Revision { get; set; }

    public static string IdFor(string tenantId, string carrierCode, string code) => $"{tenantId}|{carrierCode}|{code}";

    /// <summary>The program as every other service reads it.</summary>
    public ProgramDescriptor ToDescriptor() => new()
    {
        Id = Id,
        Code = Code,
        Name = Name,
        CarrierId = CarrierId,
        LineOfBusiness = LineOfBusiness,
        StateCode = StateCode,
        Active = Active,
    };
}
