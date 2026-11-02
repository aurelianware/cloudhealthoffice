using System.Net;
using System.Text;
using AppealsService.Models;
using AppealsService.Services;
using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests.Services;

/// <summary>Member → active coverage → benefit plan → program, as <see cref="HttpMemberProgramResolver"/> reads it.</summary>
public class MemberProgramResolverTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateTime AsOf = new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);

    private static ProgramDescriptor Program(string code, string state, string lob = LinesOfBusiness.Medicaid, bool active = true) => new()
    {
        Id = $"{Tenant}|CARRIER|{code}", Code = code, Name = code, CarrierId = "CARRIER",
        LineOfBusiness = lob, StateCode = state, Active = active
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://coverage-service/") };
    }

    private static HttpResponseMessage Coverages(params string[] planIds) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "[" + string.Join(",", planIds.Select(p => $"{{\"memberId\":\"M1\",\"planId\":\"{p}\"}}")) + "]",
            Encoding.UTF8, "application/json")
    };

    private static (HttpMemberProgramResolver Resolver, StubHandler Handler) Build(
        InMemoryProgramDirectory programs, Func<HttpRequestMessage, HttpResponseMessage> coverage)
    {
        var handler = new StubHandler(coverage);
        return (new HttpMemberProgramResolver(new Factory(handler), programs, NullLogger<HttpMemberProgramResolver>.Instance), handler);
    }

    [Fact]
    public async Task Coverage_plan_and_program_give_the_state()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("MEDI-CAL", "CA"), "PLAN-MC");
        var (resolver, handler) = Build(programs, _ => Coverages("PLAN-MC"));

        var program = await resolver.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default);

        program!.StateCode.Should().Be("CA");
        var request = handler.Requests.Single();
        request.RequestUri!.PathAndQuery.Should().Be("/api/v1/coverage/member/M1/active?serviceDate=2026-11-02");
        request.Headers.GetValues("X-Tenant-ID").Should().Equal(Tenant);
    }

    [Fact]
    public async Task Only_a_program_of_the_cases_line_of_business_counts()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("MAPD", "TX", LinesOfBusiness.Medicare), "PLAN-MA");
        programs.Add(Tenant, Program("STAR", "TX"), "PLAN-STAR");
        var (resolver, _) = Build(programs, _ => Coverages("PLAN-MA", "PLAN-STAR"));

        (await resolver.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default))!.Code.Should().Be("STAR");
        (await resolver.ResolveAsync(Tenant, "M1", LineOfBusiness.MedicarePartD, AsOf, default))!.Code.Should().Be("MAPD");
        (await resolver.ResolveAsync(Tenant, "M1", LineOfBusiness.Commercial, AsOf, default)).Should().BeNull();
    }

    [Fact]
    public async Task Programs_in_two_states_give_no_answer()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("STAR", "TX"), "PLAN-TX");
        programs.Add(Tenant, Program("MEDI-CAL", "CA"), "PLAN-CA");
        var (resolver, _) = Build(programs, _ => Coverages("PLAN-TX", "PLAN-CA"));

        (await resolver.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default)).Should().BeNull();
    }

    [Fact]
    public async Task No_active_coverage_a_plan_without_a_program_or_an_inactive_program_give_no_answer()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("OLD", "CA", active: false), "PLAN-OLD");

        var (noCoverage, _) = Build(programs, _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        (await noCoverage.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default)).Should().BeNull();

        var (noProgram, _) = Build(programs, _ => Coverages("PLAN-NONE", "PLAN-OLD"));
        (await noProgram.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default)).Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_service_throws_rather_than_answering_none()
    {
        var programs = new InMemoryProgramDirectory();
        var (coverageDown, _) = Build(programs, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await coverageDown.Invoking(r => r.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default))
            .Should().ThrowAsync<HttpRequestException>();

        programs.Unavailable = true;
        var (tenantDown, _) = Build(programs, _ => Coverages("PLAN-MC"));
        await tenantDown.Invoking(r => r.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Program_directory_asks_with_the_service_identity_only_when_configured(bool useServiceIdentity)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var directory = new HttpProgramDirectory(new Factory(handler), new MemoryCache(new MemoryCacheOptions()),
            new ProgramDirectoryOptions { UseServiceIdentity = useServiceIdentity }, NullLogger<HttpProgramDirectory>.Instance);

        await directory.ForBenefitPlanAsync(Tenant, "PLAN-1");

        handler.Requests.Single().Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var required);
        required.Should().Be(useServiceIdentity);
    }

    [Fact]
    public async Task Coverage_lookup_asks_with_the_service_identity()
    {
        var (resolver, handler) = Build(new InMemoryProgramDirectory(), _ => Coverages());

        await resolver.ResolveAsync(Tenant, "M1", LineOfBusiness.Medicaid, AsOf, default);

        handler.Requests.Single().Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var required)
            .Should().BeTrue();
        required.Should().BeTrue();
    }
}
