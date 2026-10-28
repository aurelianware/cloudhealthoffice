namespace FhirService.Services.PublicDirectory;

/// <summary>
/// Da Vinci PDex Plan-Net 1.2.0 canonicals the public Provider Directory
/// declares and emits (G-36). provider-service mirrors the ones its own
/// projection uses in <c>ChoProviderFhirUrls</c>.
/// </summary>
public static class PlanNetCanonicals
{
    public const string Version = "1.2.0";
    public const string Base = "http://hl7.org/fhir/us/davinci-pdex-plan-net/";
    public const string CapabilityStatement = Base + "CapabilityStatement/plan-net";

    private const string Sd = Base + "StructureDefinition/";
    private const string Cs = Base + "CodeSystem/";

    public const string EndpointProfile = Sd + "plannet-Endpoint";
    public const string HealthcareServiceProfile = Sd + "plannet-HealthcareService";
    public const string InsurancePlanProfile = Sd + "plannet-InsurancePlan";
    public const string LocationProfile = Sd + "plannet-Location";
    public const string NetworkProfile = Sd + "plannet-Network";
    public const string OrganizationProfile = Sd + "plannet-Organization";
    public const string OrganizationAffiliationProfile = Sd + "plannet-OrganizationAffiliation";
    public const string PractitionerProfile = Sd + "plannet-Practitioner";
    public const string PractitionerRoleProfile = Sd + "plannet-PractitionerRole";

    public const string NetworkReferenceExtension = Sd + "network-reference";
    public const string LocationReferenceExtension = Sd + "location-reference";
    public const string DeliveryMethodExtension = Sd + "delivery-method";
    public const string PractitionerPeriodExtension = Sd + "practitioner-period";
    public const string OrganizationPeriodExtension = "http://hl7.org/fhir/StructureDefinition/organization-period";

    public const string OrgTypeSystem = Cs + "OrgTypeCS";
    public const string InsuranceProductTypeSystem = Cs + "InsuranceProductTypeCS";
    public const string EndpointConnectionTypeSystem = Cs + "EndpointConnectionTypeCS";
    public const string EndpointPayloadTypeSystem = Cs + "EndpointPayloadTypeCS";
}
