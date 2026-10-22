using AuthorizationService.Models;
using AuthorizationService.Services.Fhir;
using FluentAssertions;

namespace AuthorizationService.Tests.Services;

/// <summary>
/// CMS-0057-F's PA-data APIs cover medical prior authorization and exclude
/// drugs. These pin that boundary, because getting it wrong in either direction
/// is a compliance problem: too narrow withholds data the rule requires, too
/// wide discloses drug authorizations it excludes.
/// </summary>
public class PriorAuthorizationApiScopeTests
{
    private static Authorization Auth(
        string? serviceTypeCode = null,
        params (string Code, string? System)[] lines)
        => new()
        {
            TenantId = "tenant-1",
            MemberId = "M1",
            AuthorizationNumber = "PA-1",
            ServiceTypeCode = serviceTypeCode,
            RequestedServices = lines
                .Select(l => new RequestedService
                {
                    ProcedureCode = l.Code,
                    ProductOrServiceSystem = l.System,
                })
                .ToList(),
        };

    [Fact]
    public void AMedicalProcedure_IsInScope()
    {
        var authorization = Auth(serviceTypeCode: "2", lines: ("99213", "cpt"));

        PriorAuthorizationApiScope.IsInScope(authorization).Should().BeTrue();
        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeFalse();
    }

    [Fact]
    public void APharmacyServiceType_IsADrugAuthorization()
    {
        // 278 UM03 = 88 is pharmacy; the request is a drug request whatever its
        // lines say.
        var authorization = Auth(serviceTypeCode: "88");

        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeTrue();
        PriorAuthorizationApiScope.IsInScope(authorization).Should().BeFalse();
    }

    [Theory]
    [InlineData("ndc")]
    [InlineData("NDC")]
    [InlineData("http://hl7.org/fhir/sid/ndc")]
    [InlineData("rxnorm")]
    [InlineData("http://www.nlm.nih.gov/research/umls/rxnorm")]
    public void ALineInADrugCodeSystem_IsADrugAuthorization(string system)
    {
        var authorization = Auth(lines: ("12345-678-90", system));

        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeTrue();
    }

    [Theory]
    [InlineData("J1745", "hcpcs")]
    [InlineData("j1745", "hcpcs")]
    // No system hint: a bare J-code is still a drug, and excluding it is the
    // fail-closed reading.
    [InlineData("J9035", null)]
    public void AnHcpcsJCode_IsADrugAuthorization(string code, string? system)
    {
        var authorization = Auth(lines: (code, system));

        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeTrue();
    }

    [Theory]
    // Not five characters.
    [InlineData("J174")]
    [InlineData("J17455")]
    // Not all digits after the letter.
    [InlineData("J17A5")]
    // A different HCPCS letter entirely.
    [InlineData("A4253")]
    public void AnHcpcsCodeThatIsNotAJCode_IsNotADrugAuthorization(string code)
    {
        var authorization = Auth(lines: (code, "hcpcs"));

        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeFalse();
    }

    [Fact]
    public void AJShapedCodeInAnExplicitNonDrugSystem_IsTakenAtItsWord()
    {
        // CPT codes are numeric, so "J1745" under CPT is a mislabelled code
        // rather than a drug. The explicit system wins: this projector does not
        // second-guess a submitter that named its system.
        var authorization = Auth(lines: ("J1745", "cpt"));

        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeFalse();
    }

    [Fact]
    public void OneDrugLineAmongMedicalOnes_ExcludesTheWholeRecord()
    {
        // The regulation excludes drug data, not drug-only records: a record
        // mixing the two still discloses the drug.
        var authorization = Auth(
            serviceTypeCode: "2",
            lines: [("99213", "cpt"), ("J1745", "hcpcs")]);

        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeTrue();
    }

    [Fact]
    public void ARecordWithNoLinesAndNoPharmacyType_IsInScope()
    {
        PriorAuthorizationApiScope.IsInScope(Auth()).Should().BeTrue();
    }

    [Fact]
    public void ABlankProcedureCode_IsIgnoredRatherThanTreatedAsADrug()
    {
        var authorization = Auth(lines: [("   ", "ndc"), ("99213", "cpt")]);

        authorization.RequestedServices.Should().HaveCount(2);
        // The blank line names NDC, but there is no code to be a drug.
        PriorAuthorizationApiScope.IsDrugAuthorization(authorization).Should().BeFalse();
    }
}
