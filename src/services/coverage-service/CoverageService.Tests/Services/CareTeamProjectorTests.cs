using CoverageService.Models;
using CoverageService.Services;

namespace CoverageService.Tests.Services;

public class CareTeamProjectorTests
{
    private readonly CareTeamProjector _projector = new();

    [Fact]
    public void Project_WithPcpOnly_EmitsSingleParticipant()
    {
        var resource = _projector.Project(
            "M-001",
            new Coverage { Status = CoverageStatus.Active, LineOfBusiness = LineOfBusiness.Commercial },
            new[]
            {
                new CareTeamMember
                {
                    Role = CareTeamRole.PrimaryCareProvider,
                    PractitionerNpi = "1234567890",
                    DisplayName = "Dr. Test",
                    EffectiveDate = new DateTime(2025, 1, 1)
                }
            });

        resource["resourceType"]!.GetValue<string>().Should().Be("CareTeam");
        resource["status"]!.GetValue<string>().Should().Be("active");
        resource["subject"]!["reference"]!.GetValue<string>().Should().Be("Patient/M-001");
        var participants = resource["participant"]!.AsArray();
        participants.Count.Should().Be(1);
        participants[0]!["member"]!["identifier"]!["value"]!.GetValue<string>().Should().Be("1234567890");
    }

    [Fact]
    public void Project_NoMembers_EmitsProposedStatusAndNoParticipants()
    {
        var resource = _projector.Project("M-001", null, Array.Empty<CareTeamMember>());

        resource["status"]!.GetValue<string>().Should().Be("proposed");
        resource.ContainsKey("participant").Should().BeFalse();
    }

    [Fact]
    public void Project_TerminatedCoverage_EmitsInactive()
    {
        var resource = _projector.Project(
            "M-001",
            new Coverage { Status = CoverageStatus.Terminated },
            new[]
            {
                new CareTeamMember
                {
                    Role = CareTeamRole.PrimaryCareProvider,
                    PractitionerNpi = "1234567890",
                    DisplayName = "Dr. Test",
                    EffectiveDate = new DateTime(2025, 1, 1)
                }
            });

        resource["status"]!.GetValue<string>().Should().Be("inactive");
    }

    [Fact]
    public void Project_AlignsToUsCoreProfile()
    {
        var resource = _projector.Project("M-001", null, Array.Empty<CareTeamMember>());
        var profiles = resource["meta"]!["profile"]!.AsArray();
        profiles.Should().Contain(p => p!.GetValue<string>() == "http://hl7.org/fhir/us/core/StructureDefinition/us-core-careteam");
    }
}
