using System.Text.Json;
using FhirService.Mappers;
using FhirService.Models;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Models;

/// <summary>
/// A bundle entry declared as <see cref="FhirResource"/> is written with every
/// field of its actual resource type, as ASP.NET's formatter writes it.
/// </summary>
public class FhirBundleSerializationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly ChoMember Member = new()
    {
        MemberId = "m1", FirstName = "Ana", LastName = "Ruiz", Dob = "1980-01-02", Gender = "F",
    };

    private static JsonElement Resource(string json, int index) =>
        JsonDocument.Parse(json).RootElement.GetProperty("entry")[index].GetProperty("resource");

    [Fact]
    public void Bundle_entries_carry_their_full_resource()
    {
        var bundle = new FhirBundle
        {
            Type = "collection",
            Entry =
            [
                new FhirBundleEntry { Resource = PatientAccessMapper.MapMemberToPatient(Member) },
                new FhirBundleEntry { Resource = PatientAccessMapper.MapMemberToCoverage(Member) },
            ],
        };

        var json = JsonSerializer.Serialize(bundle, Web);

        var patient = Resource(json, 0);
        patient.GetProperty("resourceType").GetString().Should().Be("Patient");
        patient.GetProperty("birthDate").GetString().Should().Be("1980-01-02");
        patient.GetProperty("name")[0].GetProperty("family").GetString().Should().Be("Ruiz");
        var coverage = Resource(json, 1);
        coverage.GetProperty("resourceType").GetString().Should().Be("Coverage");
        coverage.TryGetProperty("beneficiary", out _).Should().BeTrue();
    }

    [Fact]
    public void Search_bundle_entries_carry_their_full_resource()
    {
        var bundle = ProviderDirectoryMapper.CreateSearchBundle("Patient", [PatientAccessMapper.MapMemberToPatient(Member)]);

        var json = JsonSerializer.Serialize(bundle, Web);

        Resource(json, 0).GetProperty("birthDate").GetString().Should().Be("1980-01-02");
    }

    [Fact]
    public void A_null_resource_is_written_as_null()
    {
        var json = JsonSerializer.Serialize(new FhirBundle { Type = "collection", Entry = [new FhirBundleEntry()] }, Web);

        Resource(json, 0).ValueKind.Should().Be(JsonValueKind.Null);
    }
}
