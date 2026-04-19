using MemberService.Models;
using MemberService.Services;

namespace MemberService.Tests.Services;

public class FhirFlagProjectorTests
{
    private readonly FhirFlagProjector _projector = new();

    private static MemberAlert Alert(
        MemberAlertType type = MemberAlertType.LitigationHold,
        MemberAlertSeverity severity = MemberAlertSeverity.Critical,
        DateTime? endDate = null,
        string? requiredAction = null) => new()
        {
            Id = "alert-1",
            TenantId = "t",
            MemberId = "M-1",
            AlertType = type,
            Severity = severity,
            StartDate = DateTime.UtcNow.AddDays(-3),
            EndDate = endDate,
            Reason = "test reason",
            RequiredAction = requiredAction,
            CreatedBy = "csr"
        };

    [Fact]
    public void Project_ActiveAlert_StatusIsActive()
    {
        var flag = _projector.Project(Alert());
        flag["resourceType"]!.ToString().Should().Be("Flag");
        flag["status"]!.ToString().Should().Be("active");
    }

    [Fact]
    public void Project_EndDatedAlert_StatusIsInactive()
    {
        var flag = _projector.Project(Alert(endDate: DateTime.UtcNow.AddDays(-1)));
        flag["status"]!.ToString().Should().Be("inactive");
    }

    [Fact]
    public void Project_EmitsCodeWithAlertType()
    {
        var flag = _projector.Project(Alert(MemberAlertType.CustodyDispute));
        flag["code"]!["coding"]![0]!["code"]!.ToString().Should().Be("CustodyDispute");
        flag["code"]!["coding"]![0]!["display"]!.ToString().Should().Be("Custody Dispute");
    }

    [Fact]
    public void Project_EmitsSubjectWithMemberIdentifier()
    {
        var flag = _projector.Project(Alert());
        flag["subject"]!["type"]!.ToString().Should().Be("Patient");
        flag["subject"]!["identifier"]!["value"]!.ToString().Should().Be("M-1");
    }

    [Fact]
    public void Project_RequiredAction_EmittedAsExtension()
    {
        var flag = _projector.Project(Alert(requiredAction: "Route through legal"));
        flag["extension"]![0]!["valueString"]!.ToString().Should().Be("Route through legal");
    }

    [Fact]
    public void Project_NoRequiredAction_OmitsExtension()
    {
        var flag = _projector.Project(Alert(requiredAction: null));
        flag["extension"].Should().BeNull();
    }

    [Fact]
    public void ProjectBundle_WrapsEntries_AsSearchset()
    {
        var bundle = _projector.ProjectBundle(new[]
        {
            Alert(MemberAlertType.LitigationHold),
            Alert(MemberAlertType.VIP)
        });

        bundle["resourceType"]!.ToString().Should().Be("Bundle");
        bundle["type"]!.ToString().Should().Be("searchset");
        bundle["total"]!.GetValue<int>().Should().Be(2);
        bundle["entry"]!.AsArray().Count.Should().Be(2);
    }

    [Fact]
    public void Project_SeverityMapsToCategoryCode()
    {
        _projector.Project(Alert(severity: MemberAlertSeverity.Critical))
            ["category"]![0]!["coding"]![0]!["code"]!.ToString().Should().Be("safety");
        _projector.Project(Alert(severity: MemberAlertSeverity.Warning))
            ["category"]![0]!["coding"]![0]!["code"]!.ToString().Should().Be("admin");
        _projector.Project(Alert(severity: MemberAlertSeverity.Info))
            ["category"]![0]!["coding"]![0]!["code"]!.ToString().Should().Be("clinical");
    }
}
