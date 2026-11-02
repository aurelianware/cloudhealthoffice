using AuthorizationService.Services;
using AuthorizationService.Services.StatePrograms;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthorizationService.Tests.Services;

/// <summary>
/// A prior authorization's line of business is the member's program's
/// (docs/architecture/line-of-business.md, step 6): it picks the CMS-0057-F
/// timeframes, so it should not rest on what the submitter typed.
/// </summary>
public class AuthorizationLineOfBusinessResolverTests
{
    private const string Tenant = "t1";
    private static readonly DateTime AsOf = new(2026, 11, 2);

    private sealed class FakeCoverage(params string[] planIds) : IStateDentalMemberLookup
    {
        public bool Unavailable { get; init; }

        public Task<IReadOnlyList<ActiveCoveragePlan>> ActiveCoverageAsync(
            string tenantId, string memberId, DateTime asOf, CancellationToken ct) =>
            Unavailable
                ? throw new HttpRequestException("coverage-service unavailable")
                : Task.FromResult<IReadOnlyList<ActiveCoveragePlan>>(
                    planIds.Select(p => new ActiveCoveragePlan($"cov-{p}", p)).ToList());

        public Task<StateDentalMember?> MemberAsync(string tenantId, string memberId, CancellationToken ct) =>
            Task.FromResult<StateDentalMember?>(null);
    }

    private static ProgramDescriptor Program(string id, string lineOfBusiness) => new()
    {
        Id = id, Code = id, Name = id, CarrierId = "car-1", LineOfBusiness = lineOfBusiness, StateCode = "TX",
    };

    private static InMemoryProgramDirectory Programs()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("MA", "Medicare"), "PLAN-MA", "PLAN-MA-DENTAL");
        programs.Add(Tenant, Program("STAR", "Medicaid"), "PLAN-STAR");
        return programs;
    }

    private static Task<LineOfBusiness?> Resolve(IStateDentalMemberLookup coverage, IProgramDirectory programs) =>
        new ProgramAuthorizationLineOfBusinessResolver(
                coverage, programs, NullLogger<ProgramAuthorizationLineOfBusinessResolver>.Instance)
            .ResolveAsync(Tenant, "M1", AsOf, CancellationToken.None);

    [Fact]
    public async Task The_members_active_program_names_the_line_of_business()
    {
        (await Resolve(new FakeCoverage("PLAN-MA"), Programs())).Should().Be(LineOfBusiness.Medicare);
    }

    [Fact]
    public async Task Two_plans_under_programs_of_the_same_line_of_business_agree()
    {
        (await Resolve(new FakeCoverage("PLAN-MA", "PLAN-MA-DENTAL"), Programs())).Should().Be(LineOfBusiness.Medicare);
    }

    [Fact]
    public async Task A_dual_eligible_member_has_no_single_answer()
    {
        // Medicare and Medicaid on the same day: which one the request is
        // under is the submitter's to say.
        (await Resolve(new FakeCoverage("PLAN-MA", "PLAN-STAR"), Programs())).Should().BeNull();
    }

    [Fact]
    public async Task No_active_coverage_or_no_program_gives_no_answer()
    {
        (await Resolve(new FakeCoverage(), Programs())).Should().BeNull();
        (await Resolve(new FakeCoverage("PPO500"), Programs())).Should().BeNull();
    }

    [Fact]
    public async Task An_outage_gives_no_answer_instead_of_failing_the_request()
    {
        (await Resolve(new FakeCoverage("PLAN-MA") { Unavailable = true }, Programs())).Should().BeNull();
        (await Resolve(new FakeCoverage("PLAN-MA"), new InMemoryProgramDirectory { Unavailable = true })).Should().BeNull();
    }
}
