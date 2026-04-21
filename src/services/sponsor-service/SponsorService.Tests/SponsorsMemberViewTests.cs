using SponsorService.Controllers;
using SponsorService.Models;

namespace SponsorService.Tests;

/// <summary>
/// Covers the sponsor-member-view projection. The controller action itself is
/// thin (repo lookup + projection); the interesting logic is the projection,
/// which exposes a static <see cref="SponsorsController.ProjectMemberView"/>
/// for deterministic testing.
/// </summary>
public class SponsorsMemberViewTests
{
    [Fact]
    public void ProjectMemberView_IncludesBrokerAndOpenEnrollment()
    {
        var sponsor = new Sponsor
        {
            GroupNumber = "GRP-100",
            EmployerName = "Acme Co",
            LineOfBusiness = LineOfBusiness.Commercial,
            Status = SponsorStatus.Active,
            ContactName = "Jane Smith",
            ContactPhone = "214-555-0100",
            ContactEmail = "benefits@acme.com",
            Broker = new BrokerInfo
            {
                AgencyName = "Broker Co",
                Name = "Bob Broker",
                Phone = "555-0101",
                Email = "bob@broker.co",
                Npn = "N12345"
            },
            OpenEnrollment = new OpenEnrollmentWindow
            {
                Start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                End = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc)
            }
        };

        var view = SponsorsController.ProjectMemberView(sponsor,
            new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc));

        view.GroupNumber.Should().Be("GRP-100");
        view.SponsorName.Should().Be("Acme Co");
        view.Broker!.Name.Should().Be("Bob Broker");
        view.Broker.AgencyName.Should().Be("Broker Co");
        view.OpenEnrollment!.Status.Should().Be("Open");
    }

    [Fact]
    public void ProjectMemberView_OmitsEmptyContactSection()
    {
        var sponsor = new Sponsor
        {
            GroupNumber = "GRP-101",
            EmployerName = "No-Contact Co",
            Status = SponsorStatus.Active,
            LineOfBusiness = LineOfBusiness.Commercial
        };

        var view = SponsorsController.ProjectMemberView(sponsor, DateTime.UtcNow);
        view.PrimaryContact.Should().BeNull();
        view.Broker.Should().BeNull();
        view.OpenEnrollment.Should().BeNull();
    }

    [Theory]
    [InlineData("2026-01-01", "Upcoming")]
    [InlineData("2026-11-15", "Open")]
    [InlineData("2027-01-01", "Closed")]
    public void OpenEnrollment_Status_ShiftsWithAsOf(string asOfIso, string expected)
    {
        var window = new OpenEnrollmentWindow
        {
            Start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc)
        };
        var asOf = DateTime.Parse(asOfIso).ToUniversalTime();
        window.Status(asOf).ToString().Should().Be(expected);
    }
}
