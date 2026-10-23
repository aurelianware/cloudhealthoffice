using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using TenantService.Hierarchy;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// The plan → program link, and the program → plans link back
/// (plan-configuration.md §12.6). Both run through benefit-plan-service,
/// which owns plans, so that every consumer asks one service one question
/// rather than joining two records itself.
///
/// <para>
/// The distinction both directions keep: a plan that carries no program, or a
/// program with no plans, is an answer; an unreachable benefit-plan-service is
/// not. A narrowing built on "no plans" would quietly drop every plan-level
/// override under a program, so the second case throws.
/// </para>
/// </summary>
public class BenefitPlanProgramLookupTests
{
    private const string Tenant = "t1";
    private const string Program = "t1|DEFAULT|STAR";

    private const string TwoPlans = """
        [
          {"id":"a1","planId":"STAR-1","planName":"Texas STAR Gold","programId":"t1|DEFAULT|STAR"},
          {"id":"a2","planId":"STAR-2","planName":"Texas STAR Silver","programId":"t1|DEFAULT|STAR"}
        ]
        """;

    private static (HttpBenefitPlanProgramLookup Lookup, Recorder Wire) Build(
        HttpStatusCode status = HttpStatusCode.OK, string body = TwoPlans)
    {
        var wire = new Recorder(status, body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpBenefitPlanProgramLookup.HttpClientName))
            .Returns(() => new HttpClient(wire) { BaseAddress = new Uri("http://benefit-plan-service") });
        return (new HttpBenefitPlanProgramLookup(factory.Object), wire);
    }

    [Fact]
    public async Task A_programs_plans_are_asked_for_by_program_including_the_retired_ones()
    {
        var (lookup, wire) = Build();

        (await lookup.GetPlanIdsForProgramAsync(Tenant, Program)).Should().Equal("STAR-1", "STAR-2");

        wire.Paths.Should().Equal("/api/v1/plans");
        // Retired plans included: a plan that is no longer sold can still carry
        // an override, and leaving it out would show it as the tenant's rather
        // than this program's.
        wire.Queries.Single().Should().Contain("activeOnly=false").And.Contain("program=t1%7CDEFAULT%7CSTAR");
        wire.Tenants.Should().Equal(Tenant);
        // Following the hierarchy is tenant-service's own job, not the caller's:
        // a caller allowed to read a program need not be allowed to read plans.
        wire.ServiceIdentity.Should().Equal(true);
    }

    [Fact]
    public async Task A_program_with_no_plans_is_an_answer()
    {
        var (lookup, _) = Build(body: "[]");

        (await lookup.GetPlanIdsForProgramAsync(Tenant, Program)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_program_is_not_worth_a_round_trip()
    {
        var (lookup, wire) = Build();

        (await lookup.GetPlanIdsForProgramAsync(Tenant, "  ")).Should().BeEmpty();
        wire.Paths.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_service_that_could_not_answer_throws_rather_than_reading_as_no_plans(HttpStatusCode status)
    {
        // The 404 among these is deliberate: the list endpoint answers an empty
        // array for a program with no plans, so a 404 here is the route gone,
        // not an empty program.
        var (lookup, _) = Build(status, "");

        await lookup.Invoking(l => l.GetPlanIdsForProgramAsync(Tenant, Program)).Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_plan_in_the_list_that_states_no_plan_id_is_skipped_rather_than_counted()
    {
        var (lookup, _) = Build(body: """[{"id":"a1"},{"id":"a2","planId":""},{"id":"a3","planId":"STAR-9"}]""");

        (await lookup.GetPlanIdsForProgramAsync(Tenant, Program)).Should().Equal("STAR-9");
    }

    [Fact]
    public async Task A_plans_program_is_read_from_the_plan_and_a_plan_without_one_answers_null()
    {
        var (withProgram, _) = Build(body: """{"planId":"STAR-1","programId":"t1|DEFAULT|STAR"}""");
        (await withProgram.GetProgramIdAsync(Tenant, "STAR-1")).Should().Be(Program);

        var (withNone, _) = Build(body: """{"planId":"OLD-1"}""");
        (await withNone.GetProgramIdAsync(Tenant, "OLD-1")).Should().BeNull();

        // Here a 404 does mean "no such plan", which is an answer.
        var (missing, _) = Build(HttpStatusCode.NotFound, "");
        (await missing.GetProgramIdAsync(Tenant, "NOPE")).Should().BeNull();
    }

    [Fact]
    public async Task A_plan_read_that_failed_throws_too()
    {
        var (lookup, _) = Build(HttpStatusCode.ServiceUnavailable, "");

        await lookup.Invoking(l => l.GetProgramIdAsync(Tenant, "STAR-1")).Should().ThrowAsync<HttpRequestException>();
    }

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public List<string> Queries { get; } = new();
        public List<string> Tenants { get; } = new();
        public List<bool> ServiceIdentity { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Queries.Add(request.RequestUri.Query);
            Tenants.Add(request.Headers.TryGetValues("X-Tenant-ID", out var values) ? values.First() : string.Empty);
            ServiceIdentity.Add(request.Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var required) && required);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
