using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Mapping;
using ClaimsService.Services.Resolution;
using EngineModels = CloudHealthOffice.ClaimsScrubEngine.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Mapping;

/// <summary>
/// Capability 5.4 — verifies that <see cref="ClaimToX12837Mapper"/>
/// faithfully populates the engine fields the default rule set inspects
/// (Decision 10). Sentinel fields (X12 envelope, addresses) are checked
/// only for well-formedness, not for fidelity.
/// </summary>
public class ClaimToX12837MapperTests
{
    private static readonly DateTime ServiceDate = new(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Maps_required_engine_fields_from_AdapterClaim()
    {
        var claim = NewProfessionalClaim();
        var member = new ResolvedMember
        {
            MemberId = "MEM-1",
            DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            Gender = "F",
        };

        var x12 = ClaimToX12837Mapper.Map(claim, member);

        Assert.Equal("ver-1", x12.ClaimId);
        Assert.Equal(EngineModels.ClaimType.Professional, x12.ClaimType);
        Assert.Equal("1234567890", x12.BillingProvider.Npi);
        Assert.Equal("MEM-1", x12.Subscriber.MemberId);
        Assert.Equal("19800615", x12.Subscriber.DateOfBirth);
        Assert.Equal(100m, x12.TotalClaimedAmount);
        Assert.Single(x12.ServiceLines);
        Assert.Equal("99213", x12.ServiceLines[0].ProcedureCode);
        Assert.Equal("20260415", x12.ServiceLines[0].ServiceDate);
    }

    [Fact]
    public void Empty_DOB_when_ResolvedMember_is_null()
    {
        var claim = NewProfessionalClaim();

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        // Engine rule DC002 inspects this field and produces an Error
        // when blank. The mapper's job is to surface the absence
        // honestly, not to fabricate a DOB.
        Assert.Equal(string.Empty, x12.Subscriber.DateOfBirth);
    }

    [Fact]
    public void Prefers_SubscriberId_over_MemberId_when_both_present()
    {
        var claim = NewProfessionalClaim();
        claim.MemberId = "MEM-1";
        claim.SubscriberId = "SUB-1";

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        // SubscriberId is the X12 837 subscriber identifier (Loop 2010BA);
        // MemberId is the platform-internal pointer.
        Assert.Equal("SUB-1", x12.Subscriber.MemberId);
    }

    [Fact]
    public void Falls_back_to_MemberId_when_SubscriberId_missing()
    {
        var claim = NewProfessionalClaim();
        claim.MemberId = "MEM-1";
        claim.SubscriberId = null;

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        Assert.Equal("MEM-1", x12.Subscriber.MemberId);
    }

    [Fact]
    public void Maps_Institutional_ClaimType()
    {
        var claim = NewProfessionalClaim();
        claim.ClaimType = ClaimType.Institutional;

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        // Critical: the platform's enum is 1-based and the engine's is
        // 0-based. Raw cast would silently shift values; mapper switches
        // by name.
        Assert.Equal(EngineModels.ClaimType.Institutional, x12.ClaimType);
    }

    [Fact]
    public void Maps_Dental_ClaimType()
    {
        var claim = NewProfessionalClaim();
        claim.ClaimType = ClaimType.Dental;

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        Assert.Equal(EngineModels.ClaimType.Dental, x12.ClaimType);
    }

    [Fact]
    public void Patient_is_null_when_subscriber_is_the_patient()
    {
        var claim = NewProfessionalClaim();
        claim.PatientFirstName = null;
        claim.PatientLastName = null;

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        Assert.Null(x12.Patient);
    }

    [Fact]
    public void Patient_populated_with_RelationshipCode_default_when_present_without_code()
    {
        var claim = NewProfessionalClaim();
        claim.PatientFirstName = "Alex";
        claim.PatientLastName = "Doe";
        claim.PatientRelationship = null;

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        Assert.NotNull(x12.Patient);
        Assert.Equal("Alex", x12.Patient!.FirstName);
        Assert.Equal("Doe", x12.Patient.LastName);
        // X12 default relationship "18" = self.
        Assert.Equal("18", x12.Patient.RelationshipCode);
    }

    [Fact]
    public void Diagnosis_codes_propagate_with_default_qualifier()
    {
        var claim = NewProfessionalClaim();
        claim.DiagnosisCodes = new List<AdapterDiagnosisCode>
        {
            new() { Code = "Z00.00", PointerNumber = 1 },
            new() { Code = "I10", CodeQualifier = "ABK", PointerNumber = 2 },
        };

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        Assert.NotNull(x12.ClaimHeader.DiagnosisCodes);
        Assert.Equal(2, x12.ClaimHeader.DiagnosisCodes!.Count);
        Assert.Equal("Z00.00", x12.ClaimHeader.DiagnosisCodes[0].Code);
        Assert.Equal("ABK", x12.ClaimHeader.DiagnosisCodes[0].Qualifier);
        // Principal diagnosis = lowest pointer number.
        Assert.Equal("Z00.00", x12.ClaimHeader.PrincipalDiagnosisCode);
    }

    [Fact]
    public void ServiceLine_modifiers_drop_empties()
    {
        var claim = NewProfessionalClaim();
        claim.ClaimLines[0].Modifiers = new List<string> { "25", "", "59" };

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        Assert.Equal(new[] { "25", "59" }, x12.ServiceLines[0].Modifiers);
    }

    [Fact]
    public void X12_envelope_fields_are_well_formed_sentinels()
    {
        var claim = NewProfessionalClaim();

        var x12 = ClaimToX12837Mapper.Map(claim, subscriber: null);

        // These fields aren't inspected by default rules but the engine
        // requires them non-null. Sentinel values keep the request well-formed.
        Assert.False(string.IsNullOrEmpty(x12.TransactionControlNumber));
        Assert.False(string.IsNullOrEmpty(x12.InterchangeControlNumber));
        Assert.False(string.IsNullOrEmpty(x12.TransactionDate));
        Assert.False(string.IsNullOrEmpty(x12.Submitter.Name));
        Assert.False(string.IsNullOrEmpty(x12.Receiver.Name));
        Assert.False(string.IsNullOrEmpty(x12.ParsedAt));
    }

    private static AdapterClaim NewProfessionalClaim() => new()
    {
        TenantId = "tenant-1",
        Id = "ver-1",
        ClaimNumber = "CLM-1",
        ClaimVersionId = "ver-1",
        VersionNumber = 1,
        VersionState = ClaimVersionState.Submitted,
        MemberId = "MEM-1",
        SubscriberFirstName = "Pat",
        SubscriberLastName = "Roe",
        BillingProviderNPI = "1234567890",
        BillingProviderName = "Acme Clinic",
        BenefitPlanId = "plan-1",
        LineOfBusiness = LineOfBusiness.Commercial,
        ClaimType = ClaimType.Professional,
        ClaimFrequencyCode = "1",
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 100m,
        ServiceDateFrom = ServiceDate,
        ServiceDateTo = ServiceDate,
        SubmittedDate = ServiceDate,
        DiagnosisCodes = new List<AdapterDiagnosisCode>
        {
            new() { Code = "Z00.00", PointerNumber = 1 },
        },
        ClaimLines = new List<AdapterClaimLine>
        {
            new()
            {
                LineNumber = 1,
                ProcedureCode = "99213",
                ChargeAmount = 100m,
                Units = 1,
                ServiceDateFrom = ServiceDate,
                ServiceDateTo = ServiceDate,
                DiagnosisPointers = new List<int> { 1 },
            },
        },
    };
}
