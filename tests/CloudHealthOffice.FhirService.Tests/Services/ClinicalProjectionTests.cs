using FhirService.Services.Clinical;
using FhirService.Services.PayerToPayer.Ingestion;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// What changes between the stored row and the resource on the wire, and — just
/// as load-bearing — what does not.
/// </summary>
public class ClinicalResourceProjectorTests
{
    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });
    private static readonly ClinicalResourceProjector Projector = new();

    [Fact]
    public void TheServedSubjectIsChosMember_NotWhateverThePayloadClaims()
    {
        // The central rule: imported subject is data, never authorization
        // authority. A package whose Observation names another member is filed
        // under the member the exchange resolved, and served that way too.
        var stored = Stored(new Observation
        {
            Id = "OBS-1",
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "8867-4"),
            Subject = new ResourceReference("Patient/somebody-elses-member"),
        });

        var projected = (Observation)Projector.Project(stored)!;

        projected.Subject.Reference.Should().Be("Patient/pat-001");
    }

    [Fact]
    public void TheServedIdIsChosLogicalId_NotTheSourcePayers()
    {
        var stored = Stored(Condition("CND-1"));

        Projector.Project(stored)!.Id.Should().Be(stored.ClinicalId);
        stored.ClinicalId.Should().NotBe("CND-1");
    }

    [Fact]
    public void ClinicalContentIsPreservedExactlyAsThePayerSentIt()
    {
        var stored = Stored(new Observation
        {
            Id = "OBS-1",
            Status = ObservationStatus.Amended,
            Code = new CodeableConcept("http://loinc.org", "8867-4", "Heart rate"),
            Value = new Quantity(72, "/min"),
            Subject = new ResourceReference("Patient/remote-1"),
        });

        var projected = (Observation)Projector.Project(stored)!;

        projected.Status.Should().Be(ObservationStatus.Amended);
        projected.Code.Coding[0].Code.Should().Be("8867-4");
        ((Quantity)projected.Value).Value.Should().Be(72);
    }

    [Fact]
    public void ProvenanceNamesTheOriginAndTheSourceIdentity()
    {
        var stored = Stored(Condition("CND-1"));

        var meta = Projector.Project(stored)!.Meta;

        meta.Source.Should().Be("urn:cho:clinical:imported:PRIOR-PLAN:CND-1");
        meta.VersionId.Should().Be(stored.ContentHash[..12]);
        meta.LastUpdated.Should().NotBeNull();
    }

    [Fact]
    public void ProvenanceComponentsAreEscaped_SoAPayerIdCannotForgeADifferentOrigin()
    {
        // A payer id containing the URN separator must not be able to make the
        // source read as some other payer's.
        var stored = Stored(Condition("CND-1")) with { SourcePayerId = "evil:cho-native" };

        Projector.Project(stored)!.Meta.Source
            .Should().Be("urn:cho:clinical:imported:evil%3Acho-native:CND-1");
    }

    [Fact]
    public void NativeDataIsDistinguishableFromImportedData()
    {
        var stored = Stored(Condition("CND-1")) with
        {
            Origin = ClinicalResourceOrigin.ChoNative,
            SourcePayerId = null,
            SourceResourceId = null,
        };

        Projector.Project(stored)!.Meta.Source.Should().Be(ClinicalResourceProjector.NativeSource);
    }

    [Fact]
    public void NoProfileIsStamped_BecauseNoneIsValidated()
    {
        Projector.Project(Stored(Condition("CND-1")))!.Meta.Profile.Should().BeEmpty();
    }

    [Fact]
    public void ARowWhoseStoredPayloadDisagreesWithItsTypeIsNotServed()
    {
        // The row is indexed and authorized as an Observation. A payload that is
        // something else cannot be served under that identity — the caller gets
        // the same "not found" every other miss gets.
        var stored = Stored(Condition("CND-1")) with { ResourceType = "Observation" };

        Projector.Project(stored).Should().BeNull();
    }

    [Fact]
    public void AnUnreadableStoredPayloadIsNotServedAsAHalfResource()
    {
        var stored = Stored(Condition("CND-1")) with { ResourceJson = "{ not json" };

        Projector.Project(stored).Should().BeNull();
    }

    // ── Reference handling ────────────────────────────────────────────────────

    [Fact]
    public void ALocalReferenceToAServedClinicalResourceBecomesAResolvableFhirReference()
    {
        var targetKey = new string('b', 64);
        var stored = Stored(new Observation
        {
            Id = "OBS-1",
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "8867-4"),
            Subject = new ResourceReference("Patient/remote-1"),
            HasMember = [new ResourceReference($"{PayerToPayerReferenceNormalizer.ImportedPrefix}/{targetKey}")],
        });

        var projected = (Observation)Projector.Project(
            stored, new Dictionary<string, string> { [targetKey] = "Observation" })!;

        projected.HasMember[0].Reference.Should().Be($"Observation/{targetKey}");
    }

    [Fact]
    public void ALocalReferenceToATypeChoDoesNotServeIsLeftAsTheOpaqueLocalIdentity()
    {
        // Dressing it up as `Organization/{id}` would produce a reference that
        // looks resolvable and 404s. CHO says what it can honour and no more.
        var targetKey = new string('c', 64);
        var local = $"{PayerToPayerReferenceNormalizer.ImportedPrefix}/{targetKey}";
        var stored = Stored(new Observation
        {
            Id = "OBS-1",
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "8867-4"),
            Subject = new ResourceReference("Patient/remote-1"),
            Performer = [new ResourceReference(local)],
        });

        var projected = (Observation)Projector.Project(
            stored, new Dictionary<string, string> { [targetKey] = "Organization" })!;

        projected.Performer[0].Reference.Should().Be(local);
    }

    [Fact]
    public void AnUnresolvableLocalReferenceIsLeftAlone_NotInvented()
    {
        var local = $"{PayerToPayerReferenceNormalizer.ImportedPrefix}/{new string('d', 64)}";
        var stored = Stored(new Observation
        {
            Id = "OBS-1",
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "8867-4"),
            Subject = new ResourceReference("Patient/remote-1"),
            Performer = [new ResourceReference(local)],
        });

        var projected = (Observation)Projector.Project(stored, new Dictionary<string, string>())!;

        projected.Performer[0].Reference.Should().Be(local);
    }

    [Fact]
    public void LocalReferenceIdsAreCollectedForBatchResolution()
    {
        var first = new string('e', 64);
        var second = new string('f', 64);
        var json = $$"""
            {"resourceType":"Observation","id":"x",
             "performer":[{"reference":"PayerToPayerImport/{{first}}"}],
             "hasMember":[{"reference":"PayerToPayerImport/{{second}}"},
                          {"reference":"Patient/pat-001"}]}
            """;

        ClinicalResourceProjector.LocalReferenceIds(json)
            .Should().BeEquivalentTo(new[] { first, second });
    }

    [Fact]
    public void AResourceWithNoLocalReferencesNeedsNoResolution()
        => ClinicalResourceProjector.LocalReferenceIds(
                Serializer.SerializeToString(Condition("CND-1")))
            .Should().BeEmpty();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Condition Condition(string id) => new()
    {
        Id = id,
        Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
        Subject = new ResourceReference("Patient/remote-1"),
    };

    private static StoredClinicalResource Stored(Resource resource)
    {
        var json = Serializer.SerializeToString(resource);
        return new StoredClinicalResource
        {
            TenantId = "t1",
            MemberId = "pat-001",
            ResourceType = resource.TypeName,
            ClinicalId = ClinicalResourceIdentity.ForImported(
                PayerToPayerImportPolicy.ImportKey("t1", "pat-001", "PRIOR-PLAN", resource.TypeName, resource.Id!)),
            ResourceJson = json,
            SourcePayerId = "PRIOR-PLAN",
            SourceResourceId = resource.Id,
            ExchangeId = "exchange-1",
            ContentHash = PayerToPayerImportPolicy.ContentHash(json),
            LastUpdatedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
        };
    }
}

/// <summary>
/// The gate an imported clinical resource passes before it becomes durable,
/// readable PHI on CHO's own FHIR surface.
/// </summary>
public class ClinicalPayloadValidatorTests
{
    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    [Fact]
    public void AWellFormedClinicalResourceIsAccepted()
        => Validate(new Condition
        {
            Id = "CND-1",
            Code = new CodeableConcept("http://hl7.org/fhir/sid/icd-10-cm", "E11.9"),
        }).Should().Be(ClinicalPayloadRejection.None);

    [Fact]
    public void AResourceWithNoSourceIdIsRefused()
    {
        // Without a source id there is no identity tuple to key on, so the
        // resource has nowhere deterministic to live.
        Validate(new Condition { Code = new CodeableConcept("s", "c") })
            .Should().Be(ClinicalPayloadRejection.MissingSourceId);
    }

    [Fact]
    public void ASourceIdLongerThanFhirAllowsIsRefused()
        => Validate(new Condition { Id = new string('x', 65) })
            .Should().Be(ClinicalPayloadRejection.OversizedSourceId);

    [Fact]
    public void AResourceTypeChoDoesNotServeClinicallyIsRefused()
    {
        // The validator is the last line, not the first: classification has
        // already routed non-clinical types elsewhere. It still refuses, because
        // a caller passing one here would be a bug worth failing on.
        Validate(new Patient { Id = "P-1" }).Should().Be(ClinicalPayloadRejection.UnsupportedType);
    }

    [Fact]
    public void AnOversizedResourceIsRefused()
    {
        // The clinical store is for clinical facts. Bulk content belongs behind
        // DocumentReference, which has its own attachment handling.
        var validator = new ClinicalPayloadValidator(new ClinicalPayloadLimits { MaxResourceBytes = 256 });
        var big = new Condition
        {
            Id = "CND-1",
            Note = [new Annotation { Text = new Markdown(new string('n', 4096)) }],
        };

        validator.Validate(big, Serializer.SerializeToString(big))
            .Should().Be(ClinicalPayloadRejection.Oversized);
    }

    [Fact]
    public void ADeeplyNestedPayloadIsRefusedWithoutBeingMaterialized()
    {
        var validator = new ClinicalPayloadValidator(new ClinicalPayloadLimits { MaxDepth = 4 });

        // 200 nested arrays: readable as JSON, but not something a clinical
        // resource looks like, and exactly the shape that makes a consumer's
        // parser do exponential work.
        var nested = new string('[', 200) + new string(']', 200);
        var json = $"{{\"resourceType\":\"Condition\",\"id\":\"CND-1\",\"x\":{nested}}}";

        validator.Validate(new Condition { Id = "CND-1" }, json)
            .Should().Be(ClinicalPayloadRejection.TooDeeplyNested);
    }

    [Fact]
    public void TheDefaultLimitsAcceptARealisticClinicalResource()
    {
        var observation = new Observation
        {
            Id = "OBS-1",
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "85354-9", "Blood pressure panel"),
            Component =
            [
                new Observation.ComponentComponent
                {
                    Code = new CodeableConcept("http://loinc.org", "8480-6"),
                    Value = new Quantity(120, "mm[Hg]"),
                },
                new Observation.ComponentComponent
                {
                    Code = new CodeableConcept("http://loinc.org", "8462-4"),
                    Value = new Quantity(80, "mm[Hg]"),
                },
            ],
        };

        Validate(observation).Should().Be(ClinicalPayloadRejection.None);
    }

    private static ClinicalPayloadRejection Validate(Resource resource)
        => new ClinicalPayloadValidator().Validate(resource, Serializer.SerializeToString(resource));
}

/// <summary>The shape of a CHO clinical logical id, and what is refused as one.</summary>
public class ClinicalResourceIdentityTests
{
    [Fact]
    public void AnImportedIdIsTheDeterministicImportIdentity()
    {
        var key = PayerToPayerImportPolicy.ImportKey("t1", "pat-001", "PRIOR", "Observation", "OBS-1");

        ClinicalResourceIdentity.ForImported(key).Should().Be(key);
        ClinicalResourceIdentity.IsWellFormed(ClinicalResourceIdentity.ForImported(key)).Should().BeTrue();
    }

    [Fact]
    public void AnImportedIdLeaksNoMemberOrPayerDetail()
    {
        var id = ClinicalResourceIdentity.ForImported(
            PayerToPayerImportPolicy.ImportKey("demo-tenant", "pat-001", "PRIOR-PLAN", "Observation", "OBS-1"));

        id.Should().NotContain("pat-001").And.NotContain("demo-tenant").And.NotContain("PRIOR-PLAN");
    }

    [Fact]
    public void AnImportedIdIsALegalFhirResourceId()
    {
        // FHIR: [A-Za-z0-9\-\.]{1,64}
        var id = ClinicalResourceIdentity.ForImported(
            PayerToPayerImportPolicy.ImportKey("t1", "pat-001", "PRIOR", "Observation", "OBS-1"));

        id.Should().MatchRegex(@"^[A-Za-z0-9\-\.]{1,64}$");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OBS-1")]
    [InlineData("../../etc/passwd")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // upper-case hex
    public void AnythingChoCouldNotHaveIssuedIsNotWellFormed(string? id)
        => ClinicalResourceIdentity.IsWellFormed(id).Should().BeFalse();
}
