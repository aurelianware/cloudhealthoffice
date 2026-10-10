using System.Reflection;
using CloudHealthOffice.Infrastructure.Security;
using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MemberService.Tests.Controllers;

/// <summary>
/// GET /api/v1/members/rating-census: premium billing's minimum-necessary view
/// of a group (synthetic members only).
/// </summary>
public class RatingCensusTests
{
    private const string Tenant = "tenant-census";

    private static (MembersController Controller, InMemoryMemberRepository Repo) Build()
    {
        var repo = new InMemoryMemberRepository();
        var events = new InMemoryMemberEventRepository();
        var controller = new MembersController(repo,
            new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance),
            events, new FhirPatientProjector(), new NoOpIdentifierEncryptor(),
            new Mock<ICoverageServiceClient>().Object, new Mock<IEnrollmentImportServiceClient>().Object,
            new Mock<IAccumulatorServiceClient>().Object);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, repo);
    }

#pragma warning disable CS0618 // SubscriberMemberId: the legacy link the census reads
    private static Member Person(string id, bool subscriber, string? subscriberId, string? rel, DateTime dob,
        bool? tobacco = null, string group = "GRP-1", bool draft = false, string tenant = Tenant) => new()
    {
        TenantId = tenant,
        MemberId = id,
        GroupNumber = group,
        IsSubscriber = subscriber,
        SubscriberMemberId = subscriberId,
        RelationshipCode = rel,
        FirstName = "Test",
        LastName = id,
        DateOfBirth = dob,
        TobaccoUser = tobacco,
        SSN = "000-00-0000",
        Address = "1 Synthetic Way",
        Email = "synthetic@example.test",
        IsDraft = draft
    };
#pragma warning restore CS0618

    [Fact]
    public async Task ReturnsTheGroupsMembers_WithOnlyRatingFields()
    {
        var (controller, repo) = Build();
        repo.Members.Add(Person("S1", true, null, "18", new DateTime(1980, 6, 15), tobacco: true));
        repo.Members.Add(Person("D1", false, "S1", "01", new DateTime(1982, 3, 1)));
        repo.Members.Add(Person("X1", true, null, "18", new DateTime(1990, 1, 1), group: "OTHER"));
        repo.Members.Add(Person("Y1", true, null, "18", new DateTime(1990, 1, 1), tenant: "someone-else"));
        repo.Members.Add(Person("Z1", true, null, "18", new DateTime(1990, 1, 1), draft: true));

        var result = await controller.GetRatingCensus("GRP-1");

        var body = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<RatingCensusResponse>().Which;
        body.Members.Select(m => m.MemberId).Should().BeEquivalentTo("S1", "D1");
        var spouse = body.Members.Single(m => m.MemberId == "D1");
        spouse.SubscriberMemberId.Should().Be("S1");
        spouse.RelationshipCode.Should().Be("01");
        spouse.DateOfBirth.Should().Be(new DateTime(1982, 3, 1));
        body.Members.Single(m => m.MemberId == "S1").TobaccoUser.Should().BeTrue();
    }

    [Fact]
    public async Task GroupNumberIsRequired()
    {
        var (controller, _) = Build();
        (await controller.GetRatingCensus(" ")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void CensusCarriesNoIdentifiersOrContactDetails()
    {
        typeof(RatingCensusMember).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(
            "MemberId", "IsSubscriber", "SubscriberMemberId", "RelationshipCode", "FirstName", "LastName", "DateOfBirth", "TobaccoUser");
    }

    [Fact]
    public void BillingReadCanReadTheCensus_ButNoOtherMemberRead()
    {
        typeof(MembersController).GetMethod(nameof(MembersController.GetRatingCensus))!
            .GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission)
            .Should().Equal("members:read,billing:read");
        typeof(MembersController).GetMethod(nameof(MembersController.GetMember))!
            .GetCustomAttributes<RequirePermissionAttribute>().Should().BeEmpty("the member read keeps the members:read default");
    }
}
