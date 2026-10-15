using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The 270 subscriber search options the simulator accepts (X12 5010):
/// member ID, member ID + name, or last name + first name + DOB without an ID.
/// </summary>
public class EligibilitySimulatorTests
{
    private static readonly DateTime Dob = new(1980, 3, 14);

    [Fact]
    public void Validate_IdOnly_IsAccepted()
    {
        EligibilitySimulator.Validate(new Eligibility270Criteria { SubscriberId = "MBR-001" })
            .Should().BeNull();
    }

    [Fact]
    public void Validate_IdPlusName_IsAccepted()
    {
        EligibilitySimulator.Validate(new Eligibility270Criteria
        {
            SubscriberId = "MBR-001", FirstName = "Ana", LastName = "Diaz"
        }).Should().BeNull();
    }

    [Fact]
    public void Validate_NamePlusDobWithoutId_IsAccepted()
    {
        EligibilitySimulator.Validate(new Eligibility270Criteria
        {
            FirstName = "Ana", LastName = "Diaz", DateOfBirth = Dob
        }).Should().BeNull();
    }

    [Fact]
    public void Validate_NameWithoutDob_IsRejected()
    {
        EligibilitySimulator.Validate(new Eligibility270Criteria { FirstName = "Ana", LastName = "Diaz" })
            .Should().Be(EligibilitySimulator.CriteriaRequiredMessage);
    }

    [Fact]
    public void Validate_LastNameAndDobWithoutFirstName_IsRejected()
    {
        EligibilitySimulator.Validate(new Eligibility270Criteria { LastName = "Diaz", DateOfBirth = Dob })
            .Should().NotBeNull();
    }

    [Fact]
    public void Validate_WhitespaceIdAndNothingElse_IsRejected()
    {
        EligibilitySimulator.Validate(new Eligibility270Criteria { SubscriberId = "   " })
            .Should().NotBeNull();
    }

    private static Mock<IMemberService> Members(params MemberSummary[] results)
    {
        var members = new Mock<IMemberService>();
        members.Setup(m => m.SearchMembersAsync(It.IsAny<string>())).ReturnsAsync(results.ToList());
        return members;
    }

    private static MemberSummary M(string id, string first, string last, DateTime dob) =>
        new() { MemberId = id, FirstName = first, LastName = last, DateOfBirth = dob };

    [Fact]
    public async Task Resolve_SingleExactLastNameAndDobMatch_IsResolved()
    {
        var members = Members(
            M("MBR-1", "Ana", "Diaz", Dob),
            M("MBR-2", "Ana", "Diazmonte", Dob),     // last name not exact
            M("MBR-3", "Ana", "Diaz", Dob.AddDays(1))); // DOB differs

        var result = await EligibilitySimulator.ResolveByNameAndDobAsync(members.Object, "Ana", " diaz ", Dob);

        result.Status.Should().Be(MemberResolutionStatus.Resolved);
        result.Member!.MemberId.Should().Be("MBR-1");
        members.Verify(m => m.SearchMembersAsync("diaz"), Times.Once);
    }

    [Fact]
    public async Task Resolve_NoMatch_IsNotFound()
    {
        var result = await EligibilitySimulator.ResolveByNameAndDobAsync(
            Members(M("MBR-3", "Ana", "Diaz", Dob.AddYears(1))).Object, "Ana", "Diaz", Dob);

        result.Status.Should().Be(MemberResolutionStatus.NotFound);
        result.Member.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_TwoMatchesSameFirstName_IsAmbiguous()
    {
        var result = await EligibilitySimulator.ResolveByNameAndDobAsync(
            Members(M("MBR-1", "Ana", "Diaz", Dob), M("MBR-9", "Ana", "Diaz", Dob)).Object, "Ana", "Diaz", Dob);

        result.Status.Should().Be(MemberResolutionStatus.Ambiguous);
        result.Candidates.Select(c => c.MemberId).Should().BeEquivalentTo("MBR-1", "MBR-9");
    }

    [Fact]
    public async Task Resolve_FirstNameNarrowsTwins_ToOne()
    {
        var result = await EligibilitySimulator.ResolveByNameAndDobAsync(
            Members(M("MBR-1", "Ana", "Diaz", Dob), M("MBR-2", "Luis", "Diaz", Dob)).Object, "luis", "Diaz", Dob);

        result.Status.Should().Be(MemberResolutionStatus.Resolved);
        result.Member!.MemberId.Should().Be("MBR-2");
    }

    [Fact]
    public void BuildInquiry_UsesEligibilityServiceFieldNames()
    {
        var criteria = new Eligibility270Criteria
        {
            FirstName = "Ana", LastName = "Diaz", DateOfBirth = Dob,
            ServiceDate = new DateTime(2026, 10, 1), ServiceType = "88"
        };

        var body = EligibilitySimulator.BuildInquiry(criteria, "MBR-1");

        body["subscriberId"].Should().Be("MBR-1");
        body["subscriberFirstName"].Should().Be("Ana");
        body["subscriberLastName"].Should().Be("Diaz");
        body["subscriberDOB"].Should().Be(Dob);
        body["serviceTypeCode"].Should().Be("88");
        body["serviceDateFrom"].Should().Be(new DateTime(2026, 10, 1));
    }

    [Fact]
    public void BuildInquiry_OmitsDobWhenUnknown()
    {
        var body = EligibilitySimulator.BuildInquiry(new Eligibility270Criteria { SubscriberId = "MBR-1" }, "MBR-1");

        body.Should().NotContainKey("subscriberDOB");
    }
}
