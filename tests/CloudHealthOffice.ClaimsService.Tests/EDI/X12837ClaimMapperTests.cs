using ClaimsService.EDI.Inbound;
using ClaimsService.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.EDI;

public class X12837ClaimMapperTests
{
    private const string FmmisHlSample =
        "ISA*00*          *00*          *ZZ*FLMCO00001     *ZZ*FMMIS          *260725*0518*^*00501*000000001*0*P*:~GS*HC*FLMCO00001*FMMIS*20260725*0518*1*X*005010X222A1~ST*837*0001*005010X222A1~BHT*0019*18*E2E-FL-SAMPLE-0001*20260725*0518*CH~NM1*41*2*E2E FL SPECIALIST GROUP*****46*FLMCO00001~PER*IC*FMMIS ENCOUNTER SUBMISSION*TE*0000000000~NM1*40*2*FLORIDA MEDICAID FMMIS*****46*FMMIS~HL*1**20*1~NM1*85*2*E2E FL SPECIALIST GROUP*****XX*1234567890~N3*ADDRESS ON FILE~N4*CITY*FL*00000~REF*1D*FL-MCD-00001~HL*2*1*22*0~SBR*P*18*****MC~NM1*IL*1*MEMBER*TEST****MI*MBR19-SAMPLE~NM1*PR*2*FLORIDA MEDICAID*****PI*FMMIS~CLM*E2E-FL-SAMPLE-0001*200.00***11:B:1*Y*A*Y*Y~DTP*472*RD8*20260725-20260725~HI*ABK:J06.9~LX*1~SV1*HC:99213*150.00*UN*1*11**1~DTP*472*RD8*20260725-20260725~SE*21*0001~GE*1*1~IEA*1*000000001~";

    [Fact]
    public void Map_SubscriberIsPatient_UsesSubscriberMemberId()
    {
        var claim = X12837Parser.Parse(FmmisHlSample)[0];
        var adapterClaim = X12837ClaimMapper.Map(claim, "tenant-1");

        Assert.Equal("MBR19-SAMPLE", adapterClaim.MemberId);
        Assert.Equal("MBR19-SAMPLE", adapterClaim.SubscriberId);
        Assert.Null(adapterClaim.PatientFirstName);
    }

    [Fact]
    public void Map_DependentIsPatient_UsesPatientMemberId_NotSubscribers()
    {
        // Regression guard for the exact bug the scoping doc flagged:
        // a dependent's claim must resolve against the dependent's own
        // MemberId, not silently fall back to the subscriber's.
        var claim = X12837Parser.Parse(FmmisHlSample)[0] with
        {
            Subscriber = new() { MemberId = "SUBSCRIBER-ID", FirstName = "Jane", LastName = "Doe", DateOfBirth = "19800101" },
            Patient = new()
            {
                MemberId = "DEPENDENT-ID",
                FirstName = "Jimmy",
                LastName = "Doe",
                DateOfBirth = "20150101",
                RelationshipCode = "19"
            }
        };

        var adapterClaim = X12837ClaimMapper.Map(claim, "tenant-1");

        Assert.Equal("DEPENDENT-ID", adapterClaim.MemberId);
        Assert.Equal("SUBSCRIBER-ID", adapterClaim.SubscriberId);
        Assert.Equal("Jimmy", adapterClaim.PatientFirstName);
        Assert.Equal("19", adapterClaim.PatientRelationship);
    }

    [Fact]
    public void Map_DependentWithNoOwnId_FallsBackToSubscriberId_NotBlank()
    {
        // Documented, accepted limitation: some 837s don't carry the
        // dependent's own id at all. Falling back to the subscriber's id
        // (rather than leaving it blank, which would fail validation
        // outright) lets the claim reach adjudication, where it will very
        // likely fail member resolution against the wrong person — a
        // real, informative failure, not a silent success.
        var claim = X12837Parser.Parse(FmmisHlSample)[0] with
        {
            Patient = new() { FirstName = "Jimmy", LastName = "Doe", DateOfBirth = "20150101", RelationshipCode = "19", MemberId = null }
        };

        var adapterClaim = X12837ClaimMapper.Map(claim, "tenant-1");

        Assert.Equal(claim.Subscriber.MemberId, adapterClaim.MemberId);
    }

    [Fact]
    public void Map_ClaimTypeAndCoreFields()
    {
        var claim = X12837Parser.Parse(FmmisHlSample)[0];
        var adapterClaim = X12837ClaimMapper.Map(claim, "tenant-1");

        Assert.Equal("tenant-1", adapterClaim.TenantId);
        Assert.Equal("E2E-FL-SAMPLE-0001", adapterClaim.ClaimNumber);
        Assert.Equal(ClaimType.Professional, adapterClaim.ClaimType);
        Assert.Equal("1234567890", adapterClaim.BillingProviderNPI);
        Assert.Equal(200.00m, adapterClaim.TotalChargeAmount);
        Assert.Equal("0001", adapterClaim.EDI837ControlNumber);

        // Not required at submission time (ClaimSubmissionService.Validate
        // doesn't check them) — left for BenefitCalculationStage to
        // resolve during adjudication, not guessed at here.
        Assert.Null(adapterClaim.BenefitPlanId);
        Assert.Null(adapterClaim.CoverageId);
    }

    [Fact]
    public void Map_ServiceLines_AndClaimDateRange_DerivedFromLines()
    {
        var claim = X12837Parser.Parse(FmmisHlSample)[0];
        var adapterClaim = X12837ClaimMapper.Map(claim, "tenant-1");

        var line = Assert.Single(adapterClaim.ClaimLines);
        Assert.Equal("99213", line.ProcedureCode);
        Assert.Equal(150.00m, line.ChargeAmount);
        Assert.Equal([1], line.DiagnosisPointers);

        Assert.Equal(new DateTime(2026, 7, 25), adapterClaim.ServiceDateFrom);
        Assert.Equal(new DateTime(2026, 7, 25), adapterClaim.ServiceDateTo);
    }

    [Fact]
    public void Map_DiagnosisCodes()
    {
        var claim = X12837Parser.Parse(FmmisHlSample)[0];
        var adapterClaim = X12837ClaimMapper.Map(claim, "tenant-1");

        var dx = Assert.Single(adapterClaim.DiagnosisCodes);
        Assert.Equal("J06.9", dx.Code);
        Assert.Equal("ABK", dx.CodeQualifier);
        Assert.Equal(1, dx.PointerNumber);
    }
}
