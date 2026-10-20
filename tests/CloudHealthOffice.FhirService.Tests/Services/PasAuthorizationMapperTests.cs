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
    public void QuantityCoding_IsIndependentOfDisplay_AndParticipatesInRequestHash()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Item[0].Quantity = new Quantity
        {
            Value = 3, Unit = "units", Code = "mL", System = "http://unitsofmeasure.org"
        };
        var request = Map(bundle).Request!;
        var line = request.RequestedServices.Single();
        line.UnitType.Should().Be("units");
        line.UnitCode.Should().Be("mL");
        line.UnitSystem.Should().Be("http://unitsofmeasure.org");
        var original = HttpPasAuthorizationRecorder.HashOf(request);
        line.UnitCode = "L";
        HttpPasAuthorizationRecorder.HashOf(request).Should().NotBe(original);
        line.UnitCode = "mL";
        line.UnitSystem = "https://example.test/custom-units";
        HttpPasAuthorizationRecorder.HashOf(request).Should().NotBe(original);
    }

    [Fact]
    public void DisplayOnlyQuantity_DoesNotInventCodingOrChangeLegacyNullWireShape()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Item[0].Quantity = new Quantity { Value = 1, Unit = "units" };
        var request = Map(bundle).Request!;
        var line = request.RequestedServices.Single();
        line.UnitCode.Should().BeNull();
        line.UnitSystem.Should().BeNull();
        var wire = System.Text.Json.JsonSerializer.Serialize(line, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        wire.Should().NotContain("unitCode").And.NotContain("unitSystem");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlongQuantityCoding_IsRejectedRatherThanTruncated(bool code)
    {
        var bundle = PasBundles.Complete();
        var quantity = PasBundles.ClaimOf(bundle).Item[0].Quantity;
        if (code) quantity.Code = new string('x', 257); else quantity.System = new string('x', 257);
        var result = Map(bundle);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Expression.EndsWith(".quantity"));
    }

    [Fact]
    public void NonOrdinalSequences_ArePreserved_AndDuplicateSequencesAreRejected()
    {
        var bundle = PasBundles.Complete();
        var claim = PasBundles.ClaimOf(bundle);
        claim.Item[0].Sequence = 7;
        Map(bundle).Request!.RequestedServices.Single().LineNumber.Should().Be(7);
        claim.Item.Add(new Claim.ItemComponent
        {
            Sequence = 7, ProductOrService = claim.Item[0].ProductOrService.DeepCopy() as CodeableConcept,
        });
        var invalid = Map(bundle);
        invalid.IsValid.Should().BeFalse();
        invalid.Errors.Should().Contain(e => e.Expression == "Claim.item.sequence");
    }

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
    public void MemberIdFallsBackToCoverageSubscriberId_WhenThePatientIsTheSubscriber()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single().Identifier.Clear();
        var coverage = bundle.Entry.Select(e => e.Resource).OfType<Coverage>().Single();
        coverage.SubscriberId = "SUB-42";
        coverage.Relationship = new CodeableConcept("http://terminology.hl7.org/CodeSystem/subscriber-relationship", "self");

        Map(bundle).Request!.MemberId.Should().Be("SUB-42");
    }

    [Fact]
    public void MemberIdFallsBackToCoverageSubscriberId_WhenSubscriberIsTheBeneficiary()
    {
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single().Identifier.Clear();
        var coverage = bundle.Entry.Select(e => e.Resource).OfType<Coverage>().Single();
        coverage.SubscriberId = "SUB-42";
        coverage.Subscriber = new ResourceReference($"Patient/{PasBundles.PatientId}");

        Map(bundle).Request!.MemberId.Should().Be("SUB-42");
    }

    [Theory]
    [InlineData("child", null)]
    [InlineData(null, "Patient/policy-holder")]
    [InlineData(null, null)]
    public void SubscriberId_IsNotTheMemberId_OfADependentOrAnUnstatedRelationship(string? relationship, string? subscriber)
    {
        // The subscriber id names the policy holder; for a dependent it is
        // someone else's id, so it cannot stand in for the patient's.
        var bundle = PasBundles.Complete();
        bundle.Entry.Select(e => e.Resource).OfType<Patient>().Single().Identifier.Clear();
        var coverage = bundle.Entry.Select(e => e.Resource).OfType<Coverage>().Single();
        coverage.SubscriberId = "SUB-OF-THE-POLICY-HOLDER";
        coverage.Relationship = relationship is null
            ? null
            : new CodeableConcept("http://terminology.hl7.org/CodeSystem/subscriber-relationship", relationship);
        coverage.Subscriber = subscriber is null ? null : new ResourceReference(subscriber);

        var result = Map(bundle);

        result.Errors.Should().Contain(e => e.Expression == "Patient.identifier");
    }

    // ── Diagnoses: ICD-10 only ──────────────────────────────────────

    [Fact]
    public void Diagnosis_PrefersTheIcd10CmCoding_OverAnEarlierOtherCoding()
    {
        var bundle = PasBundles.Complete();
        var concept = new CodeableConcept("http://snomed.info/sct", "1234567890123", "Primary osteoarthritis of knee");
        concept.Coding.Add(new Coding("http://hl7.org/fhir/sid/icd-10-cm", "M17.11"));
        PasBundles.ClaimOf(bundle).Diagnosis[0].Diagnosis = concept;

        var result = Map(bundle);

        result.IsValid.Should().BeTrue("a SNOMED coding first is not a reason to refuse the request");
        result.Request!.DiagnosisCodes.Should().ContainSingle().Which.Code.Should().Be("M17.11");
    }

    [Fact]
    public void Diagnosis_WithNoIcd10Coding_IsSkipped_NotAnError()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Diagnosis[0].Diagnosis = new CodeableConcept("http://snomed.info/sct", "1234567890123");

        var result = Map(bundle);

        result.IsValid.Should().BeTrue();
        result.Request!.DiagnosisCodes.Should().BeEmpty();
    }

    [Fact]
    public void Diagnosis_AcceptsIcd10_WhenThereIsNoIcd10Cm()
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Diagnosis[0].Diagnosis = new CodeableConcept("http://hl7.org/fhir/sid/icd-10", "M17.1");

        Map(bundle).Request!.DiagnosisCodes.Should().ContainSingle().Which.Code.Should().Be("M17.1");
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
    [InlineData("urn:oid:2.16.840.1.113883.3.221.5", "21", "Medicaid")]
    [InlineData("urn:oid:2.16.840.1.113883.3.221.5", "512", "Commercial")]
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

    private const string ProcessPriority = "http://terminology.hl7.org/CodeSystem/processpriority";
    private const string X12LevelOfService = "https://codesystem.x12.org/005010/1338";
    private const string ActPriority = "http://terminology.hl7.org/CodeSystem/v3-ActPriority";
    private const string ActPriorityOid = "urn:oid:2.16.840.1.113883.5.7";
    private const string RequestPriority = "http://hl7.org/fhir/request-priority";

    // Owner decision 2026-10-11 (§7.1): a recognised urgent value is expedited
    // (U) whatever system it arrives under; a nonstandard system/code pair is
    // logged. Only a genuinely unrecognised value defaults to standard (E),
    // and that is logged too.
    [Theory]
    // standard pairs: no warning
    [InlineData(ProcessPriority, "stat", "U", false)]
    [InlineData(ProcessPriority, "normal", "E", false)]
    [InlineData(ProcessPriority, "deferred", "E", false)]
    [InlineData(X12LevelOfService, "U", "U", false)]
    [InlineData(X12LevelOfService, "03", "U", false)]
    [InlineData(X12LevelOfService, "E", "E", false)]
    // recognised urgent values in a nonstandard pair: expedited, with a warning
    [InlineData(ProcessPriority, "urgent", "U", true)]
    [InlineData(ProcessPriority, "asap", "U", true)]
    [InlineData(ProcessPriority, "U", "U", true)]
    [InlineData(ProcessPriority, "expedited", "U", true)]
    [InlineData(X12LevelOfService, "stat", "U", true)]
    [InlineData("http://example.org/priority", "stat", "U", true)]
    [InlineData("http://example.org/priority", "03", "U", true)]
    [InlineData(null, "urgent", "U", true)]
    // HL7 v3-ActPriority: S stat, A ASAP, UR urgent, EM emergency, CS/CR
    // callback; R routine, EL elective
    [InlineData(ActPriority, "S", "U", false)]
    [InlineData(ActPriority, "A", "U", false)]
    [InlineData(ActPriority, "UR", "U", false)]
    [InlineData(ActPriority, "EM", "U", false)]
    [InlineData(ActPriority, "CS", "U", false)]
    [InlineData(ActPriority, "CR", "U", false)]
    [InlineData(ActPriority, "R", "E", false)]
    [InlineData(ActPriority, "EL", "E", false)]
    // ActPriority by its OID is the same system
    [InlineData(ActPriorityOid, "EM", "U", false)]
    [InlineData(ActPriorityOid, "S", "U", false)]
    [InlineData(ActPriorityOid, "R", "E", false)]
    // UR and EM say urgent / emergency in any system and any case (warned
    // outside ActPriority); S, A, CS and CR stay bound to ActPriority
    [InlineData("http://vendor.example/priority", "EM", "U", true)]
    [InlineData("http://vendor.example/priority", "UR", "U", true)]
    [InlineData(ActPriority, "em", "U", true)]
    [InlineData(ActPriorityOid, "ur", "U", true)]
    [InlineData(null, "Em", "U", true)]
    [InlineData("http://vendor.example/priority", "S", "E", true)]
    [InlineData("http://vendor.example/priority", "CR", "E", true)]
    // FHIR request-priority: routine is explicit standard; the others are urgent
    [InlineData(RequestPriority, "routine", "E", false)]
    [InlineData(RequestPriority, "urgent", "U", false)]
    [InlineData(RequestPriority, "asap", "U", false)]
    [InlineData(RequestPriority, "stat", "U", false)]
    // emergency words: urgent in any system, warned outside a standard pair
    [InlineData("http://example.org/priority", "emergency", "U", true)]
    [InlineData("http://example.org/priority", "emergent", "U", true)]
    [InlineData("http://example.org/priority", "immediate", "U", true)]
    [InlineData(ProcessPriority, "emergency", "U", true)]
    [InlineData(null, "Immediate", "U", true)]
    // unrecognised: standard, with a warning
    [InlineData(ProcessPriority, "whenever", "E", true)]
    [InlineData("http://example.org/priority", "P1", "E", true)]
    public void Priority_MapsToLevelOfService(string? system, string code, string expected, bool warned)
    {
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Priority = new CodeableConcept { Coding = [new Coding(system, code)] };

        var result = Map(bundle);

        result.Request!.LevelOfService.Should().Be(expected);
        if (warned)
            result.Warnings.Should().ContainSingle(w => w.Expression == "Claim.priority"
                                                        && w.System == system && w.Code == code);
        else
            result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void NoPriority_IsNoLevelOfService()
    {
        var result = Map(PasBundles.Complete(priority: null));

        result.Request!.LevelOfService.Should().BeNull("an absent priority is not an unrecognised one");
        result.Warnings.Should().BeEmpty();
    }

    // Urgency is never under-classified: the MOST urgent signal across the
    // PAS extension (Claim and items) and Claim.priority wins, so a standard
    // extension does not outrank a stat priority.
    [Theory]
    [InlineData("U", "normal", "U")]
    [InlineData("E", "stat", "U")]
    [InlineData("03", null, "U")]
    [InlineData("E", "normal", "E")]
    [InlineData("E", null, "E")]
    public void MostUrgentSignal_AcrossExtensionAndPriority_Wins(string extension, string? priority, string expected)
    {
        var bundle = PasBundles.Complete(priority: priority);
        PasBundles.ClaimOf(bundle).Extension.Add(new Extension(
            PasAuthorizationMapper.LevelOfServiceExtensionUrl, new CodeableConcept(X12LevelOfService, extension)));

        Map(bundle).Request!.LevelOfService.Should().Be(expected);
    }

    [Fact]
    public void PasLevelOfServiceExtension_OnTheItem_IsRead()
    {
        var bundle = PasBundles.Complete(priority: "normal");
        PasBundles.ClaimOf(bundle).Item[0].Extension.Add(new Extension(
            PasAuthorizationMapper.LevelOfServiceExtensionUrl, new CodeableConcept(X12LevelOfService, "U")));

        Map(bundle).Request!.LevelOfService.Should().Be("U");
    }

    [Fact]
    public void UrgentItemExtension_OutranksAStandardClaimExtension()
    {
        var bundle = PasBundles.Complete(priority: null);
        var claim = PasBundles.ClaimOf(bundle);
        claim.Extension.Add(new Extension(
            PasAuthorizationMapper.LevelOfServiceExtensionUrl, new CodeableConcept(X12LevelOfService, "E")));
        claim.Item[0].Extension.Add(new Extension(
            PasAuthorizationMapper.LevelOfServiceExtensionUrl, new CodeableConcept(X12LevelOfService, "U")));

        Map(bundle).Request!.LevelOfService.Should().Be("U");
    }

    [Theory]
    [InlineData("STAT")]
    [InlineData("urgent")]
    [InlineData(" Expedited ")]
    [InlineData("Emergency")]
    [InlineData("emergent")]
    [InlineData("IMMEDIATE")]
    public void UrgencyInPriorityText_IsExpedited_AndWarned(string text)
    {
        var bundle = PasBundles.Complete(priority: null);
        PasBundles.ClaimOf(bundle).Priority = new CodeableConcept { Text = text };

        var result = Map(bundle);

        result.Request!.LevelOfService.Should().Be("U");
        result.Warnings.Should().ContainSingle(w => w.Expression == "Claim.priority" && w.System == null);
    }

    [Fact]
    public void StandardWordInAnUnknownSystem_IsUnrecognised_StandardAndWarned()
    {
        // E only for an explicit standard value in a known system.
        var bundle = PasBundles.Complete();
        PasBundles.ClaimOf(bundle).Priority = new CodeableConcept("http://example.org/priority", "normal");

        var result = Map(bundle);

        result.Request!.LevelOfService.Should().Be("E");
        result.Warnings.Should().ContainSingle(w => w.Expression == "Claim.priority" && w.Code == "normal");
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
    public void MixedOpenAndClosedPeriods_ApprovalEndsAtTheLaterOfTheClosedEndAndTheFallback()
    {
        var bundle = PasBundles.Complete();
        var claim = PasBundles.ClaimOf(bundle);
        claim.Item[0].Serviced = new Period { Start = "2026-12-01" };
        claim.Item.Add(new Claim.ItemComponent
        {
            Sequence = 2,
            ProductOrService = new CodeableConcept(PasBundles.CptSystem, "97110"),
            Serviced = new Period { Start = "2026-11-01", End = "2028-01-31" },
        });

        var result = Map(bundle);

        result.Request!.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 11, 1));
        result.Request.RequestedServiceDateTo.Should().BeNull("one service is open-ended");
        result.ApprovedFrom.Should().Be(new DateTime(2026, 11, 1));
        result.ApprovedTo.Should().Be(new DateTime(2028, 1, 31),
            "the fallback (2027-11-01) may not cut short a service the request bounded later");
    }

    [Fact]
    public void MixedOpenAndClosedPeriods_ClosedEndBeforeTheFallback_UsesTheFallback()
    {
        var bundle = PasBundles.Complete();
        var claim = PasBundles.ClaimOf(bundle);
        claim.Item[0].Serviced = new Period { Start = "2026-12-01" };
        claim.Item.Add(new Claim.ItemComponent
        {
            Sequence = 2,
            ProductOrService = new CodeableConcept(PasBundles.CptSystem, "97110"),
            Serviced = new Period { Start = "2026-11-01", End = "2026-11-30" },
        });

        Map(bundle).ApprovedTo.Should().Be(new DateTime(2026, 11, 1).Add(PasAuthorizationMapper.OpenEndedApproval));
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
