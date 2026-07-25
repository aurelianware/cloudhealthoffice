using ClaimsService.Models;
using ClaimsService.Services.Resolution;
using EngineModels = CloudHealthOffice.ClaimsScrubEngine.Models;

namespace ClaimsService.Services.Adjudication.Mapping;

/// <summary>
/// Bridges the platform's <see cref="AdapterClaim"/> + resolved-member
/// context onto the <see cref="EngineModels.X12837Claim"/> shape that
/// <c>CloudHealthOffice.ClaimsScrubEngine</c> consumes (capability 5.4,
/// Decision 5 — mapping layer lives consumer-side).
///
/// <para>
/// The engine ships as a domain-agnostic class library so it can run
/// against state-Medicaid EDI pipelines that feed it 837 transactions
/// directly (Phase 2 customer onboarding). Keeping the mapper in
/// claims-service preserves that future use case.
/// </para>
///
/// <para>
/// <b>Mapping fidelity (Decision 10).</b> Only the fields the default
/// rule set actually inspects are populated faithfully — required X12
/// envelope fields the engine doesn't validate get sentinel values
/// (e.g. blank addresses, generated control numbers). The rule audit:
/// </para>
/// <list type="bullet">
///   <item><description>Subscriber.MemberId — DC001 (Error)</description></item>
///   <item><description>Subscriber.DateOfBirth — DC002 (Error). Sourced
///     from <see cref="ResolvedMember.DateOfBirth"/> because AdapterClaim
///     does not carry DOB; null DOB honestly fails DC002.</description></item>
///   <item><description>BillingProvider.Npi — DC003 + PV001 (Error)</description></item>
///   <item><description>ClaimHeader.DiagnosisCodes — DC004 + CV001 (Error)</description></item>
///   <item><description>ServiceLines — DC005 / DC006 / CV002-005 / DL001-004 / AL001-003 / MV001-002</description></item>
/// </list>
/// </summary>
public static class ClaimToX12837Mapper
{
    /// <summary>
    /// Build the engine input from the claim + resolved member. Member
    /// is optional — when null the subscriber DOB is empty and rule
    /// DC002 produces a structural Error.
    /// </summary>
    public static EngineModels.X12837Claim Map(
        AdapterClaim claim,
        ResolvedMember? subscriber)
    {
        return new EngineModels.X12837Claim
        {
            ClaimId = claim.Id,
            ClaimType = MapClaimType(claim.ClaimType),
            // X12 envelope fields are not inspected by default rules;
            // generate stable per-claim sentinels so the request shape
            // is well-formed.
            TransactionControlNumber = claim.EDI837ControlNumber ?? claim.Id,
            InterchangeControlNumber = claim.EDI837ControlNumber ?? claim.Id,
            TransactionDate = (claim.SubmittedDate == default
                ? DateTime.UtcNow
                : claim.SubmittedDate).ToString("yyyyMMdd"),
            Submitter = MapSubmitter("cloudhealthoffice", claim.TenantId),
            Receiver = MapReceiver("payer", claim.TenantId),
            BillingProvider = MapBillingProvider(claim),
            Subscriber = MapSubscriber(claim, subscriber),
            Patient = MapPatient(claim, subscriber),
            ClaimHeader = MapClaimHeader(claim),
            ServiceLines = claim.ClaimLines.Select(MapServiceLine).ToList(),
            TotalClaimedAmount = claim.TotalChargeAmount,
            ParsedAt = DateTime.UtcNow.ToString("o"),
        };
    }

    // The engine's enum is value-default ordered (Professional=0,
    // Institutional=1, Dental=2); the platform's enum is 1-based
    // (Professional=1, Institutional=2, Dental=3). Switch by name —
    // never raw-cast.
    internal static EngineModels.ClaimType MapClaimType(ClaimType type) => type switch
    {
        ClaimType.Professional => EngineModels.ClaimType.Professional,
        ClaimType.Institutional => EngineModels.ClaimType.Institutional,
        ClaimType.Dental => EngineModels.ClaimType.Dental,
        _ => EngineModels.ClaimType.Professional,
    };

    // ClaimSubmitter and ClaimReceiver are nominally distinct engine
    // types with identical shape. Default rules don't inspect either —
    // we populate well-formed sentinel records so the request builds.
    private static EngineModels.ClaimSubmitter MapSubmitter(string name, string tenantId)
        => new()
        {
            Name = name,
            IdentificationCode = string.IsNullOrEmpty(tenantId) ? "UNKNOWN" : tenantId,
            IdentificationQualifier = "ZZ",
        };

    private static EngineModels.ClaimReceiver MapReceiver(string name, string tenantId)
        => new()
        {
            Name = name,
            IdentificationCode = string.IsNullOrEmpty(tenantId) ? "UNKNOWN" : tenantId,
            IdentificationQualifier = "ZZ",
        };

    private static EngineModels.BillingProvider MapBillingProvider(AdapterClaim claim) => new()
    {
        Npi = claim.BillingProviderNPI ?? string.Empty,
        Name = claim.BillingProviderName ?? string.Empty,
        // EntityType "2" = non-person (organization); the only fact
        // the platform knows by default. Engine rules don't inspect
        // this field.
        EntityType = "2",
        TaxId = null,
        TaxIdQualifier = null,
        Address = SentinelAddress(),
        TaxonomyCode = null,
    };

    private static EngineModels.ProviderAddress SentinelAddress() => new()
    {
        // Default rules don't inspect provider address fields; AdapterClaim
        // doesn't carry a billing-provider address. Empty sentinels keep
        // the X12 record well-formed.
        Line1 = string.Empty,
        City = string.Empty,
        State = string.Empty,
        PostalCode = string.Empty,
    };

    private static EngineModels.ClaimSubscriber MapSubscriber(
        AdapterClaim claim, ResolvedMember? member)
    {
        // Prefer SubscriberId — it's the X12 837 subscriber identifier
        // (Loop 2010BA / NM109). Fall back to MemberId for tenants
        // whose adapter populates only MemberId.
        var memberId = !string.IsNullOrWhiteSpace(claim.SubscriberId)
            ? claim.SubscriberId!
            : claim.MemberId ?? string.Empty;

        return new EngineModels.ClaimSubscriber
        {
            MemberId = memberId,
            FirstName = claim.SubscriberFirstName ?? string.Empty,
            LastName = claim.SubscriberLastName ?? string.Empty,
            // Engine's DateOfBirth field is `string` (yyyyMMdd). Empty
            // when ResolvedMember is null or has no DOB — DC002 then
            // produces a structural Error.
            DateOfBirth = FormatDob(member?.DateOfBirth),
            Gender = member?.Gender,
        };
    }

    private static EngineModels.ClaimPatient? MapPatient(
        AdapterClaim claim, ResolvedMember? member)
    {
        if (string.IsNullOrWhiteSpace(claim.PatientFirstName)
            && string.IsNullOrWhiteSpace(claim.PatientLastName))
        {
            // Patient = subscriber; engine treats null Patient as "use
            // subscriber DOB" for DL004.
            return null;
        }

        return new EngineModels.ClaimPatient
        {
            MemberId = claim.MemberId,
            FirstName = claim.PatientFirstName ?? string.Empty,
            LastName = claim.PatientLastName ?? string.Empty,
            DateOfBirth = FormatDob(member?.DateOfBirth),
            // X12 default = "18" (self). PatientRelationship on
            // AdapterClaim already carries the X12 code when set.
            RelationshipCode = string.IsNullOrWhiteSpace(claim.PatientRelationship)
                ? "18"
                : claim.PatientRelationship!,
        };
    }

    private static EngineModels.ClaimHeader MapClaimHeader(AdapterClaim claim) => new()
    {
        PatientControlNumber = !string.IsNullOrWhiteSpace(claim.ClaimNumber)
            ? claim.ClaimNumber
            : claim.Id,
        TotalChargeAmount = claim.TotalChargeAmount,
        PlaceOfServiceCode = claim.PlaceOfServiceCode,
        FrequencyCode = claim.ClaimFrequencyCode,
        DiagnosisCodes = claim.DiagnosisCodes
            .Select(d => new EngineModels.DiagnosisCode
            {
                Code = d.Code,
                Qualifier = string.IsNullOrWhiteSpace(d.CodeQualifier) ? "ABK" : d.CodeQualifier,
                Pointer = d.PointerNumber == 0 ? null : d.PointerNumber,
            })
            .ToList(),
        PrincipalDiagnosisCode = claim.DiagnosisCodes
            .OrderBy(d => d.PointerNumber)
            .FirstOrDefault()?.Code,
        PriorAuthorizationNumber = claim.PriorAuthorizationNumber,
    };

    private static EngineModels.ServiceLine MapServiceLine(AdapterClaimLine line) => new()
    {
        LineNumber = line.LineNumber,
        ProcedureCode = line.ProcedureCode ?? string.Empty,
        // Default qualifier — CV002/CV003 use this to pick CPT vs HCPCS
        // validation. Modifiers field qualifier is left unspecified by
        // the platform today; the engine tolerates null.
        ProcedureCodeQualifier = "HC",
        Modifiers = line.Modifiers?.Where(m => !string.IsNullOrEmpty(m)).ToList(),
        ServiceDate = line.ServiceDateFrom == default
            ? string.Empty
            : line.ServiceDateFrom.ToString("yyyyMMdd"),
        ServiceDateEnd = line.ServiceDateTo == default
            ? null
            : line.ServiceDateTo.ToString("yyyyMMdd"),
        ChargeAmount = line.ChargeAmount,
        Units = line.Units,
        PlaceOfService = line.PlaceOfServiceCode,
        RevenueCode = line.RevenueCode,
        DiagnosisPointers = line.DiagnosisPointers?.Where(p => p > 0).ToList(),
    };

    private static string FormatDob(DateTime? dob)
        => dob is { } d ? d.ToString("yyyyMMdd") : string.Empty;
}
