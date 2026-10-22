namespace DemoPersonaSeeder;

// The demo cast as plain data. Payloads.cs turns these into service requests;
// DemoScript.cs turns them (plus what the services returned) into the demo script.

public sealed record DemoPlan(
    string Key,
    Guid Id,
    string PlanId,
    string Name,
    string LineOfBusiness,
    string PlanType,
    string InNetworkId);

public sealed record DemoNetwork(string Id, string Name, string LineOfBusiness);

public sealed record DemoAddress(string Street, string City, string State, string Zip);

public sealed record DemoProvider(
    string Key,
    string Npi,
    bool IsOrganization,
    string? FirstName,
    string? LastName,
    string? OrganizationName,
    string? Credentials,
    string Specialty,
    string TaxonomyCode,
    string TaxId,
    DemoAddress Address,
    string Phone,
    IReadOnlyList<string> PlanKeys,
    bool IsPcp)
{
    public string DisplayName => IsOrganization ? OrganizationName! : $"{FirstName} {LastName}, {Credentials}";
}

public sealed record DemoContract(
    string ContractNumber,
    string ProviderKey,
    string LineOfBusiness,
    string PaymentMethodology,
    DateTime EffectiveDate,
    DateTime TerminationDate);

public sealed record DemoSponsor(
    string GroupNumber,
    string EmployerName,
    string TaxId,
    DemoAddress Address,
    string ContactName,
    string ContactPhone,
    decimal PremiumPerMember,
    int BillingDay,
    int GracePeriodDays);

public sealed record DemoIdentifier(string Type, string Value);

public sealed record DemoOtherInsurance(string PayerName, string PolicyNumber, string GroupNumber);

public sealed record DemoMember(
    string MemberId,
    string FirstName,
    string LastName,
    DateTime DateOfBirth,
    string Gender,
    string GroupNumber,
    string PlanKey,
    bool IsSubscriber,
    string? SubscriberMemberId,
    string RelationshipCode,
    DemoAddress Address,
    string Phone,
    IReadOnlyList<DemoIdentifier> Identifiers,
    string? PcpProviderKey,
    DemoOtherInsurance? OtherInsurance,
    decimal MonthlyPremium,
    decimal EmployerContribution)
{
    public string FullName => $"{FirstName} {LastName}";
}

public sealed record DemoService(string ProcedureCode, string Description, decimal Units);

public sealed record DemoAuthorization(
    string Number,
    string MemberId,
    string RequestingProviderKey,
    string? ServicingProviderKey,
    string? FacilityProviderKey,
    string AuthorizationType,
    string LevelOfService,
    string ServiceTypeCode,
    DateTime From,
    DateTime? To,
    IReadOnlyList<string> DiagnosisCodes,
    IReadOnlyList<DemoService> Services,
    string? Decision,
    decimal? ApprovedUnits);

public sealed record DemoClaimLine(string ProcedureCode, string Description, decimal Units, decimal Charge, string? RevenueCode = null);

public sealed record DemoClaim(
    string ClaimNumber,
    string PatientAccountNumber,
    string MemberId,
    string PlanKey,
    string BillingProviderKey,
    string RenderingProviderKey,
    int ClaimType,
    string PlaceOfService,
    DateTime ServiceDateFrom,
    DateTime ServiceDateTo,
    IReadOnlyList<string> DiagnosisCodes,
    IReadOnlyList<DemoClaimLine> Lines,
    string? PriorAuthorizationNumber,
    string Intent);

public sealed record DemoAppeal(
    string AppealNumber,
    string ClaimNumber,
    string AppealType,
    string Reason,
    bool IsUrgent);

/// <summary>A demo story: who it's for, and the steps a presenter follows.</summary>
public sealed record DemoStory(string Title, string Role, IReadOnlyList<string> Steps);

public sealed record DemoDataset(
    DateTime AsOf,
    IReadOnlyList<DemoNetwork> Networks,
    IReadOnlyList<DemoPlan> Plans,
    IReadOnlyList<DemoProvider> Providers,
    IReadOnlyList<DemoContract> Contracts,
    IReadOnlyList<DemoSponsor> Sponsors,
    IReadOnlyList<DemoMember> Members,
    IReadOnlyList<DemoAuthorization> Authorizations,
    IReadOnlyList<DemoClaim> Claims,
    IReadOnlyList<DemoAppeal> Appeals,
    IReadOnlyList<string> BilledGroups,
    IReadOnlyList<DemoStory> Stories)
{
    public DemoPlan Plan(string key) => Plans.Single(p => p.Key == key);
    public DemoProvider Provider(string key) => Providers.Single(p => p.Key == key);
    public DemoMember Member(string memberId) => Members.Single(m => m.MemberId == memberId);
    public DemoClaim Claim(string claimNumber) => Claims.Single(c => c.ClaimNumber == claimNumber);
}
