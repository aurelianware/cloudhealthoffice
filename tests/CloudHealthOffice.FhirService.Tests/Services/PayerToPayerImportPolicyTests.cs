using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// The two rules that decide what an imported Payer-to-Payer resource becomes:
/// how it is classified, and how it is identified. Both are load-bearing for
/// member safety — a wrong classification would let another payer's data pose as
/// CHO-owned, and a wrong key would either duplicate a member's history or fuse
/// two payers' records together.
/// </summary>
public class PayerToPayerImportPolicyTests
{
    [Theory]
    // Member history CHO actually serves.
    [InlineData("ExplanationOfBenefit", ImportedResourceClass.MemberHistory)]
    [InlineData("Claim", ImportedResourceClass.MemberHistory)]
    [InlineData("ClaimResponse", ImportedResourceClass.MemberHistory)]
    [InlineData("Encounter", ImportedResourceClass.MemberHistory)]
    [InlineData("DocumentReference", ImportedResourceClass.MemberHistory)]
    // Administrative context — stored, but never authoritative.
    [InlineData("Patient", ImportedResourceClass.AdministrativeReference)]
    [InlineData("Coverage", ImportedResourceClass.AdministrativeReference)]
    [InlineData("Organization", ImportedResourceClass.AdministrativeReference)]
    [InlineData("Practitioner", ImportedResourceClass.AdministrativeReference)]
    [InlineData("Provenance", ImportedResourceClass.AdministrativeReference)]
    // Types CHO's FHIR surface does not serve. Claiming to ingest these would be
    // a false claim, so they are named and counted instead.
    [InlineData("Condition", ImportedResourceClass.Unsupported)]
    [InlineData("Observation", ImportedResourceClass.Unsupported)]
    [InlineData("Procedure", ImportedResourceClass.Unsupported)]
    [InlineData("MedicationRequest", ImportedResourceClass.Unsupported)]
    [InlineData("AllergyIntolerance", ImportedResourceClass.Unsupported)]
    [InlineData("", ImportedResourceClass.Unsupported)]
    public void Classify_PutsEachResourceTypeInItsDocumentedBucket(string type, ImportedResourceClass expected)
        => PayerToPayerImportPolicy.Classify(type).Should().Be(expected);

    [Fact]
    public void SupportedTypes_AreServedByAChoFhirController()
    {
        // Guard against the inventory drifting into a wish list: every type CHO
        // claims to ingest must be a type its FHIR surface serves.
        PayerToPayerImportPolicy.SupportedMemberHistoryTypes.Should().BeEquivalentTo(
            new[] { "Claim", "ClaimResponse", "DocumentReference", "Encounter", "ExplanationOfBenefit" });
    }

    [Fact]
    public void ImportKey_IsStableForTheSameResourceFromTheSamePayer()
    {
        var first = PayerToPayerImportPolicy.ImportKey("t1", "pat-001", "PRIOR", "Claim", "C-1");
        var second = PayerToPayerImportPolicy.ImportKey("t1", "pat-001", "PRIOR", "Claim", "C-1");

        second.Should().Be(first, "a replay must land on the same row instead of duplicating history");
    }

    [Theory]
    // Any component of the identity tuple changing makes it a different record.
    [InlineData("t2", "pat-001", "PRIOR", "Claim", "C-1")]   // another tenant
    [InlineData("t1", "pat-002", "PRIOR", "Claim", "C-1")]   // another member
    [InlineData("t1", "pat-001", "OTHER", "Claim", "C-1")]   // another payer
    [InlineData("t1", "pat-001", "PRIOR", "Encounter", "C-1")] // another resource type
    [InlineData("t1", "pat-001", "PRIOR", "Claim", "C-2")]   // another source id
    public void ImportKey_DiffersWheneverTheIdentityTupleDiffers(
        string tenant, string member, string payer, string type, string sourceId)
    {
        var baseline = PayerToPayerImportPolicy.ImportKey("t1", "pat-001", "PRIOR", "Claim", "C-1");

        PayerToPayerImportPolicy.ImportKey(tenant, member, payer, type, sourceId).Should().NotBe(baseline);
    }

    [Fact]
    public void ImportKey_CannotCollideByConcatenation()
    {
        // Without a separator that cannot occur in an identifier, ("a","bc") and
        // ("ab","c") would hash to the same key and merge two members' records.
        var left = PayerToPayerImportPolicy.ImportKey("t", "a", "bc", "Claim", "C-1");
        var right = PayerToPayerImportPolicy.ImportKey("t", "ab", "c", "Claim", "C-1");

        left.Should().NotBe(right);
    }

    [Fact]
    public void ImportKey_CarriesNoReadableMemberDetail()
    {
        var key = PayerToPayerImportPolicy.ImportKey("demo-tenant", "pat-001", "PRIOR-PLAN", "Claim", "C-1");

        key.Should().MatchRegex("^[0-9a-f]{64}$");
        key.Should().NotContain("pat-001").And.NotContain("demo-tenant");
    }

    [Fact]
    public void ContentHash_ChangesOnlyWhenTheResourceChanges()
    {
        var first = PayerToPayerImportPolicy.ContentHash("{\"resourceType\":\"Claim\",\"id\":\"C-1\"}");

        PayerToPayerImportPolicy.ContentHash("{\"resourceType\":\"Claim\",\"id\":\"C-1\"}").Should().Be(first);
        PayerToPayerImportPolicy.ContentHash("{\"resourceType\":\"Claim\",\"id\":\"C-2\"}").Should().NotBe(first);
    }
}

/// <summary>
/// Reference rewriting inside an imported package. The rule is narrow: only
/// references that resolve to another resource in the SAME package are rewritten,
/// and only to that resource's imported identity.
/// </summary>
public class PayerToPayerReferenceNormalizerTests
{
    private static string KeyFor(string type, string id) => $"key-{type}-{id}";

    private static Bundle Package(params Resource[] resources) => new()
    {
        Type = Bundle.BundleType.Collection,
        Entry = resources
            .Select(r => new Bundle.EntryComponent { FullUrl = $"{r.TypeName}/{r.Id}", Resource = r })
            .ToList(),
    };

    [Fact]
    public void RelativeAndAbsoluteReferences_BothResolveToTheImportedCopy()
    {
        var patient = new Patient { Id = "P-1" };
        var relative = new Encounter
        {
            Id = "E-1",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("Patient/P-1"),
        };
        var absolute = new Encounter
        {
            Id = "E-2",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("https://peer.example/fhir/r4/Patient/P-1"),
        };

        var outcome = PayerToPayerReferenceNormalizer.Normalize(
            Package(patient, relative, absolute), KeyFor);

        var expected = $"{PayerToPayerReferenceNormalizer.ImportedPrefix}/key-Patient-P-1";
        relative.Subject.Reference.Should().Be(expected);
        absolute.Subject.Reference.Should().Be(expected,
            "an absolute URL must not survive as a live pointer at the source payer");
        outcome.Rewritten.Should().Be(2);
    }

    [Fact]
    public void AVersionedReference_ResolvesToTheSameResource()
    {
        var patient = new Patient { Id = "P-1" };
        var encounter = new Encounter
        {
            Id = "E-1",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("Patient/P-1/_history/3"),
        };

        PayerToPayerReferenceNormalizer.Normalize(Package(patient, encounter), KeyFor);

        encounter.Subject.Reference.Should()
            .Be($"{PayerToPayerReferenceNormalizer.ImportedPrefix}/key-Patient-P-1");
    }

    [Fact]
    public void ReferencesThePackageDoesNotContain_AreLeftExactlyAsTheyArrived()
    {
        // CHO must not invent a link to a resource the payer never sent.
        var encounter = new Encounter
        {
            Id = "E-1",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("Patient/absent"),
            ServiceProvider = new ResourceReference("https://peer.example/fhir/r4/Organization/absent"),
        };

        var outcome = PayerToPayerReferenceNormalizer.Normalize(Package(encounter), KeyFor);

        encounter.Subject.Reference.Should().Be("Patient/absent");
        encounter.ServiceProvider.Reference.Should().Be("https://peer.example/fhir/r4/Organization/absent");
        outcome.Rewritten.Should().Be(0);
    }

    [Fact]
    public void ContainedReferences_AreNotRewritten()
    {
        // A "#..." reference is local to its own resource; rewriting it would
        // break the containment it describes.
        var encounter = new Encounter
        {
            Id = "E-1",
            Status = Encounter.EncounterStatus.Finished,
            ServiceProvider = new ResourceReference("#org-1"),
        };

        var outcome = PayerToPayerReferenceNormalizer.Normalize(Package(encounter), KeyFor);

        encounter.ServiceProvider.Reference.Should().Be("#org-1");
        outcome.Rewritten.Should().Be(0);
    }

    [Fact]
    public void NestedReferences_AreRewrittenToo()
    {
        // The walk is over the FHIR model, not a fixed property list, so a
        // reference nested inside a backbone element is covered as well.
        var encounter = new Encounter { Id = "E-1", Status = Encounter.EncounterStatus.Finished };
        var eob = new ExplanationOfBenefit
        {
            Id = "EOB-1",
            Status = ExplanationOfBenefit.ExplanationOfBenefitStatus.Active,
            Item =
            [
                new ExplanationOfBenefit.ItemComponent
                {
                    Sequence = 1,
                    Encounter = [new ResourceReference("Encounter/E-1")],
                },
            ],
        };

        PayerToPayerReferenceNormalizer.Normalize(Package(encounter, eob), KeyFor);

        eob.Item[0].Encounter[0].Reference.Should()
            .Be($"{PayerToPayerReferenceNormalizer.ImportedPrefix}/key-Encounter-E-1");
    }

    [Theory]
    [InlineData("urn:uuid:6f2b8a1e-0000-4000-8000-000000000000")]
    [InlineData("Patient")]
    [InlineData("")]
    [InlineData("https://peer.example/fhir/r4")]
    public void NonResourceReferences_AreNotTreatedAsResourceReferences(string reference)
        => PayerToPayerReferenceNormalizer.ToRelative(reference).Should().BeNull();

    [Fact]
    public void TheMapRecordsWhatWasRewritten()
    {
        var patient = new Patient { Id = "P-1" };
        var encounter = new Encounter
        {
            Id = "E-1",
            Status = Encounter.EncounterStatus.Finished,
            Subject = new ResourceReference("Patient/P-1"),
        };

        var outcome = PayerToPayerReferenceNormalizer.Normalize(Package(patient, encounter), KeyFor);

        outcome.Map.Should().ContainKey("Patient/P-1")
            .WhoseValue.Should().Be($"{PayerToPayerReferenceNormalizer.ImportedPrefix}/key-Patient-P-1");
    }
}
