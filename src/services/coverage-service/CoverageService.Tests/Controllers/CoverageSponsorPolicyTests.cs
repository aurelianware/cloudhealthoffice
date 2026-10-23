using CoverageService.Controllers;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using CoverageService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CoverageService.Tests.Controllers;

/// <summary>
/// The sponsor policy a coverage is held under
/// (docs/architecture/plan-configuration.md §9b, §12.12).
///
/// <para>
/// A §9b policy is a group plus a policy number pointing at a benefit plan.
/// Coverage already carried the group and the plan, so this is the third part
/// of that identity and belongs here rather than on the member: one member can
/// hold medical under one policy and dental under another, and a renewal that
/// moves someone is a new coverage span.
/// </para>
/// </summary>
public class CoverageSponsorPolicyTests
{
    private const string Tenant = "t1";

    private static (CoverageController ctl, List<Coverage> created) Build()
    {
        var created = new List<Coverage>();
        var repo = new Mock<ICoverageRepository>();
        repo.Setup(r => r.CreateAsync(It.IsAny<Coverage>()))
            .ReturnsAsync((Coverage c) =>
            {
                created.Add(c);
                c.Id = "cov-1";
                return c;
            });

        var ctl = new CoverageController(
            repo.Object,
            new TestActor(),
            NullLogger<CoverageController>.Instance,
            new Mock<IPcpAssignmentService>().Object,
            new Mock<ICareTeamProjector>().Object);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, created);
    }

    private static CreateCoverageRequest Request(string? sponsorPolicyNumber) => new()
    {
        MemberId = "M1",
        GroupNumber = "GRP0001",
        SponsorPolicyNumber = sponsorPolicyNumber,
        PlanId = "PPO500",
        EffectiveDate = DateTime.UtcNow.AddMonths(-1),
    };

    [Fact]
    public async Task A_created_coverage_keeps_the_policy_it_was_enrolled_under()
    {
        var (ctl, created) = Build();

        await ctl.CreateCoverage(Request("GRP0001-0002"));

        var coverage = created.Should().ContainSingle().Subject;
        coverage.GroupNumber.Should().Be("GRP0001");
        coverage.SponsorPolicyNumber.Should().Be("GRP0001-0002");
    }

    [Fact]
    public async Task A_coverage_enrolled_by_group_number_has_no_policy_and_that_is_not_a_defect()
    {
        // Still the commonest case, and the state every coverage written before
        // policies existed is in.
        var (ctl, created) = Build();

        await ctl.CreateCoverage(Request(sponsorPolicyNumber: null));

        created.Should().ContainSingle().Which.SponsorPolicyNumber.Should().BeNull();
    }

    [Fact]
    public void The_policy_is_not_the_other_carriers_policy_number()
    {
        // The two sit one object apart and mean opposite things: this one is
        // the member's own policy with this payer, OtherInsurance's is another
        // carrier's for coordination of benefits. Reading one as the other
        // would send a member's own policy out as a COB record.
        var coverage = new Coverage
        {
            TenantId = Tenant,
            MemberId = "M1",
            GroupNumber = "GRP0001",
            PlanId = "PPO500",
            SponsorPolicyNumber = "GRP0001-0002",
            OtherInsurance = new OtherInsuranceInfo { PayerName = "Other Payer", PolicyNumber = "XYZ-999" },
        };

        coverage.SponsorPolicyNumber.Should().Be("GRP0001-0002");
        coverage.OtherInsurance!.PolicyNumber.Should().Be("XYZ-999");
    }
}
