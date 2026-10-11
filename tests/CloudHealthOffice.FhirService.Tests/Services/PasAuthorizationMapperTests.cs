using CloudHealthOffice.FhirService.Tests.TestSupport;
using FhirService.Services.Pas;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// D-03 / D-08: the PAS Bundle → authorization record mapping. Every value
/// from the request; a missing required value is an error, never a
/// placeholder; an undeterminable optional value is unknown, never a default.
/// </summary>
public class PasAuthorizationMapperTests
{
    private static readonly DateTime Received = new(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);

    private static PasMappingResult Map(Bundle bundle) => PasAuthorizationMapper.Map(bundle, PasBundles.ClaimOf(bundle), Received);

    [Fact]
    public void CompleteBundle_MapsEveryFieldFromTheRequest()
    {
        var result = Map(PasBundles.Complete());

        result.IsValid.Should().BeTrue();
        var r = result.Request!;
        r.ReceivedAt.Should().Be(Received);
        r.MemberId.Should().Be(PasBundles.MemberId);
        r.PatientReference.Should().Be("Patient/pat-001");
        r.PatientFirstName.Should().Be("Ana");
        r.PatientLastName.Should().Be("Rivera");
        r.PatientDateOfBirth.Should().Be(new DateTime(1984, 3, 7));
        r.LineOfBusiness.Should().Be("Medicaid");
        r.RequestingProviderNPI.Should().Be(PasBundles.RequestingNpi);
        r.RequestingProviderName.Should().Be("Jordan Lee");
        r.ServiceTypeCode.Should().Be("2");
        r.LevelOfService.Should().Be("E");
        r.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 11, 2));
        r.RequestedServiceDateTo.Should().Be(new DateTime(2026, 11, 4));
        var line = r.RequestedServices.Should().ContainSingle().Subject;
        line.ProcedureCode.Should().Be("27447");
        line.ProductOrServiceSystem.Should().Be(PasBundles.CptSystem);
        r.DiagnosisCodes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PasDiagnosisCode { Code = "M17.11", CodeQualifier = "BK" });
        result.ApprovedFrom.Should().Be(new DateTime(2026, 11, 2));
        result.ApprovedTo.Should().Be(new DateTime(2026, 11, 4));
    }

    // ── Missing required data → error, nothing invented ─────────────────

    [Fact]
    public void MissingPatient_IsAnError()
    {
        var result = Map(PasBundles.Complete().Without<Patient>());

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Expression == "Claim.patient");
    }

    [Fact]
    public void MissingCoverage_IsAnError()
    {
        var result = Map(PasBundles.Complete().Without<Coverage>());

        result.Errors.Should().Contain(e => e.Expression == "Claim.insurance.coverage");
    }

    [Fact]
    public void MissingNpi_IsAnError()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Practitioner>().Single().Identifier.Clear();

        var result = Map(bundle);

        result.Errors.Should().ContainSingle(e => e.Expression == "Claim.provider");
    }

    [Fact]
    public void NonNpiIdentifier_IsNotTakenAsTheNpi()
    {
        var bundle = PasBundles.Complete();
        var practitioner = bundle.Entry.Select(e => e.Resource).OfType<Practitioner>().Single();
        practitioner.Identifier = [new Identifier("http://example.org/staff-id", "1234567893")];

        Map(bundle).Errors.Should().Contain(e => e.Expression == "Claim.provider");
    }

    [Fact]
    public void NpiOnTheProviderReference_IsAccepted()
    {
        var bundle = PasBundles.Complete().Without<Practitioner>();
        PasBundles.ClaimOf(bundle).Provider.Identifier = new Identifier(PasBundles.NpiSystem, "1992753880");

        var result = Map(bundle);

        result.IsValid.Should().BeTrue();
        result.Request!.RequestingProviderNPI.Should().Be("1992753880");
    }

    [Fact]
    public void PartialBirthDate_IsAnError()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single().BirthDate = "1984";

        Map(bundle).Errors.Should().Contain(e => e.Expression == "Patient.birthDate");
    }

    [Fact]
    public void NoMemberIdentifierAnywhere_IsAnError()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single().Identifier.Clear();
        bundle.Entry.Select(e => e.Resource).OfType<Coverage>().Single().SubscriberId = null;

        Map(bundle).Errors.Should().Contain(e => e.Expression == "Patient.identifier");
    }

    [Fact]
    public void MemberIdFallsBackToCoverageSubscriberId()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single().Identifier.Clear();
        bundle.Entry.Select(e => e.Resource).OfType<Coverage>().Single().SubscriberId = "SUB-42";

        Map(bundle).Request!.MemberId.Should().Be("SUB-42");
    }

    [Fact]
    public void NoServiceDate_IsAnError()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Item[0].Serviced = null;

        Map(bundle).Errors.Should().Contain(e => e.Expression == "Claim.item.serviced");
    }

    [Fact]
    public void UncodedItem_IsAnError()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Item[0].ProductOrService = new CodeableConcept { Text = "knee replacement" };

        Map(bundle).Errors.Should().Contain(e => e.Expression.StartsWith("Claim.item[1]"));
    }

    // ── Unknown, never defaulted ─────────────────────────────────────

    [Theory]
    [InlineData(PasBundles.X12ClaimFilingSystem, "MC", "Medicaid")]
    [InlineData(PasBundles.X12ClaimFilingSystem, "MB", "Medicare")]
    [InlineData(PasBundles.X12ClaimFilingSystem, "CI", "Commercial")]
    [InlineData(PasBundles.X12ClaimFilingSystem, "CH", "TRICARE")]
    [InlineData(PasBundles.X12ClaimFilingSystem, "WC", "Unknown")]
    [InlineData("https://nahdo.org/sopt", "21", "Medicaid")]
    [InlineData("https://nahdo.org/sopt", "111", "Medicare")]
    [InlineData("https://nahdo.org/sopt", "311", "TRICARE")]
    [InlineData("https://nahdo.org/sopt", "512", "Commercial")]
    [InlineData("http://example.org/plan-type", "COMMERCIAL", "Unknown")]
    public void LineOfBusiness_FromCoverageType_OrUnknown(string system, string code, string expected)
    {
        var coverage = new Coverage { Type = new CodeableConcept(system, code) };
        PasAuthorizationMapper.LineOfBusiness(coverage).Should().Be(expected);
    }

    [Fact]
    public void NoCoverageType_IsUnknown_NotCommercial()
    {
        PasAuthorizationMapper.LineOfBusiness(new Coverage()).Should().Be("Unknown");
    }

    [Fact]
    public void NoX12ServiceType_IsNull_NeverTheClaimType()
    {
        var result = Map(PasBundles.Complete(serviceTypeCode: null));

        result.IsValid.Should().BeTrue();
        result.Request!.ServiceTypeCode.Should().BeNull();
    }

    // ── D-08: priority ───────────────────────────────────────────────

    [Theory]
    [InlineData("stat", "U")]
    [InlineData("urgent", "U")]
    [InlineData("expedited", "U")]
    [InlineData("U", "U")]
    [InlineData("03", "U")]
    [InlineData("normal", "E")]
    [InlineData("deferred", "E")]
    [InlineData("E", "E")]
    [InlineData("whenever", null)]
    [InlineData(null, null)]
    public void Priority_MapsToLevelOfService(string? priority, string? expected)
    {
        Map(PasBundles.Complete(priority: priority)).Request!.LevelOfService.Should().Be(expected);
    }

    // ── Dates and the approved period ────────────────────────────────

    [Fact]
    public void SingleServicedDate_ApprovesThatDay()
    {
        var result = Map(PasBundles.Complete(servicedStart: "2026-12-01", servicedEnd: null));

        result.Request!.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 12, 1));
        result.Request.RequestedServiceDateTo.Should().Be(new DateTime(2026, 12, 1));
        result.ApprovedFrom.Should().Be(new DateTime(2026, 12, 1));
        result.ApprovedTo.Should().Be(new DateTime(2026, 12, 1));
    }

    [Fact]
    public void OpenEndedPeriod_UsesTheDocumentedOneYearFallback_OnlyForTheEnd()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Item[0].Serviced = new Period { Start = "2026-12-01" };

        var result = Map(bundle);

        result.Request!.RequestedServiceDateTo.Should().BeNull();
        result.ApprovedFrom.Should().Be(new DateTime(2026, 12, 1));
        result.ApprovedTo.Should().Be(new DateTime(2026, 12, 1).Add(PasAuthorizationMapper.OpenEndedApproval));
    }

    [Fact]
    public void SeveralItems_SpanFromEarliestToLatest()
    {
        var bundle = PasBundles.Complete();
        var claim = PasBundles.ClaimOf(bundle);
        claim.Item.Add(new Claim.ItemComponent
        {
            Sequence = 2,
            ProductOrService = new CodeableConcept("https://bluebutton.cms.gov/resources/codesystem/hcpcs", "J1234"),
            Serviced = new Date("2026-10-30"),
        });

        var result = Map(bundle);

        result.Request!.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 10, 30));
        result.Request.RequestedServiceDateTo.Should().Be(new DateTime(2026, 11, 4));
        result.Request.RequestedServices.Select(s => s.ProductOrServiceSystem).Should().Equal(
            PasBundles.CptSystem, "https://bluebutton.cms.gov/resources/codesystem/hcpcs");
    }

    [Fact]
    public void BillablePeriod_IsTheFallbackForDates()
    {
        var bundle = PasBundles.Complete();
        var claim = PasBundles.ClaimOf(bundle);
        claim.Item[0].Serviced = null;
        claim.BillablePeriod = new Period { Start = "2026-09-10", End = "2026-09-20" };

        var result = Map(bundle);

        result.Request!.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 9, 10));
        result.ApprovedTo.Should().Be(new DateTime(2026, 9, 20));
    }

    [Fact]
    public void Resolve_FollowsFullUrlReferences()
    {
        var bundle = PasBundles.Complete();
        var patientEntry = bundle.Entry.Single(e => e.Resource is Patient);
        patientEntry.FullUrl = "http://example.org/fhir/Patient/pat-001";

        PasAuthorizationMapper.Resolve(bundle, new ResourceReference("Patient/pat-001"))
            .Should().BeOfType<Patient>();
        PasAuthorizationMapper.Resolve(bundle, new ResourceReference("Patient/someone-else"))
            .Should().BeNull();
    }
}
