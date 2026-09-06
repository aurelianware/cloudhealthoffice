using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The only data that ever leaves CHO for a third-party system comes from
/// <see cref="SyntheticInteropData"/>, and the external RI has to be able to accept
/// it. These tests hold both ends: the identifiers are synthetic, and the bundle
/// satisfies the PAS request-bundle constraints the payer RI enforces before it
/// will process a submission.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class SyntheticInteropDataTests
{
    private static readonly Bundle RequestBundle =
        SyntheticInteropData.PasRequestBundle(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Identifiers_name_the_synthetic_interoperability_identity_set()
    {
        SyntheticInteropData.MemberId.Should().Be("interop-member-001");
        SyntheticInteropData.PayerId.Should().Be("interop-payer-a");
        SyntheticInteropData.PriorAuthId.Should().Be("interop-pa-001");
    }

    [Fact]
    public void The_provider_npi_is_correctly_formed_but_is_a_test_value()
    {
        var npi = SyntheticInteropData.ProviderNpi;

        npi.Should().MatchRegex("^[0-9]{10}$", "an RI that format-checks the NPI must be able to accept it");
        IsValidNpiCheckDigit(npi).Should().BeTrue("the check digit must be valid so format validation passes");
        npi.Should().Be("1234567893", "the conventional test NPI, issued to no provider");
    }

    [Fact]
    public void The_request_bundle_meets_the_pas_request_bundle_constraints()
    {
        RequestBundle.Type.Should().Be(Bundle.BundleType.Collection);
        RequestBundle.Identifier.Should().NotBeNull("PAS requires Bundle.identifier");
        RequestBundle.Timestamp.Should().NotBeNull("PAS requires Bundle.timestamp");
        RequestBundle.Entry.Should().OnlyContain(entry => !string.IsNullOrWhiteSpace(entry.FullUrl),
            "every entry needs a fullUrl for the payer's reference resolution");

        var claim = RequestBundle.Entry.First().Resource.Should().BeOfType<Claim>().Subject;
        claim.Use.Should().Be(ClaimUseCode.Preauthorization);
        claim.Patient.Should().NotBeNull();
        claim.Insurer.Should().NotBeNull();
        claim.Provider.Should().NotBeNull();
        claim.Insurance.Should().NotBeEmpty();
        claim.Item.Should().NotBeEmpty();
        claim.Item.Should().OnlyContain(item => item.Category != null && item.Location != null,
            "the payer RI rejects an item without a category or a place of service");
    }

    [Fact]
    public void The_member_carries_the_MB_identifier_type_pas_requires()
    {
        var patient = RequestBundle.Entry
            .Select(entry => entry.Resource)
            .OfType<Patient>()
            .Single();

        patient.Identifier.Should().Contain(id =>
            id.Type.Coding.Any(coding =>
                coding.System == "http://terminology.hl7.org/CodeSystem/v2-0203" && coding.Code == "MB"));
    }

    [Fact]
    public void The_bundle_round_trips_through_the_fhir_serializer_cho_uses()
    {
        var json = new FhirJsonSerializer().SerializeToString(SyntheticInteropData.AsSubmitParameters(RequestBundle));

        var parsed = new FhirJsonParser().Parse<Parameters>(json);

        parsed.Parameter.Should().ContainSingle(p => p.Name == "resource");
        parsed.Parameter.Single().Resource.Should().BeOfType<Bundle>();
    }

    /// <summary>
    /// NPI check digit: Luhn over the number prefixed with the 80840 issuer prefix.
    /// </summary>
    private static bool IsValidNpiCheckDigit(string npi)
    {
        var digits = ("80840" + npi[..9]).Select(c => c - '0').Reverse().ToArray();
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var value = digits[i];
            if (i % 2 == 0)
            {
                value *= 2;
                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
        }

        return (10 - (sum % 10)) % 10 == npi[9] - '0';
    }
}
