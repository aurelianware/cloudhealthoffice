using System.Net;
using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using TenantService.Hierarchy;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// Reading a sponsor's policies and their classes from sponsor-service, so that
/// an override list can be narrowed to one sponsor's branch
/// (plan-configuration.md §9b, §12.9).
///
/// <para>
/// The distinction these are mostly about is the one the whole hierarchy is
/// built on: a group with no contract has no policies, and that is an answer,
/// while a service that could not be asked is not. The first narrows the levels
/// below the sponsor to nothing; the second must throw, so that the caller
/// reports them unnarrowed instead of answering that the sponsor overrides
/// nothing.
/// </para>
/// </summary>
public class SponsorPolicyLookupTests
{
    private const string Tenant = "t1";

    private static (HttpSponsorPolicyLookup Lookup, Recorder Wire) Lookup(
        HttpStatusCode status = HttpStatusCode.OK, string body = "[]")
    {
        var wire = new Recorder(status, body);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(HttpSponsorPolicyLookup.HttpClientName))
            .Returns(() => new HttpClient(wire) { BaseAddress = new Uri("http://sponsor-service") });
        return (new HttpSponsorPolicyLookup(factory.Object), wire);
    }

    [Fact]
    public async Task A_groups_policies_come_back_with_their_classes_as_node_ids()
    {
        var (lookup, wire) = Lookup(body: """
            [
              { "id": "t1|123456|123456-0001", "policyNumber": "123456-0001",
                "classes": [ { "code": "HOURLY" }, { "code": "SALARIED" } ] },
              { "id": "t1|123456|123456-0002", "policyNumber": "123456-0002", "classes": [] }
            ]
            """);

        var policies = await lookup.GetPoliciesAsync(Tenant, "123456");

        policies.Select(p => p.Id).Should().Equal("t1|123456|123456-0001", "t1|123456|123456-0002");
        policies[0].ClassScopeIds.Should().Equal("t1|123456|123456-0001|HOURLY", "t1|123456|123456-0001|SALARIED");
        policies[1].ClassScopeIds.Should().BeEmpty();

        wire.Paths.Should().Equal("/api/v1/sponsors/123456/policies");
        wire.Tenants.Should().Equal(Tenant);
        // Following the hierarchy is tenant-service's job, not the caller's: a
        // caller allowed to read a sponsor's settings need not be allowed to
        // read its contract.
        wire.ServiceIdentity.Should().Equal(true);
    }

    [Fact]
    public async Task A_group_number_with_a_slash_in_it_does_not_change_the_route()
    {
        var (lookup, wire) = Lookup();

        await lookup.GetPoliciesAsync(Tenant, "123/456");

        wire.Paths.Should().Equal("/api/v1/sponsors/123%2F456/policies");
    }

    [Fact]
    public async Task A_group_with_no_contract_has_no_policies_and_that_is_an_answer()
    {
        var (lookup, _) = Lookup();

        (await lookup.GetPoliciesAsync(Tenant, "123456")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_sponsor_has_no_policies_rather_than_throwing()
    {
        // 404 here is the group itself: it has no policies, which narrows the
        // levels below it to nothing. That is correct, not a failure.
        var (lookup, _) = Lookup(HttpStatusCode.NotFound, "{}");

        (await lookup.GetPoliciesAsync(Tenant, "999999")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task A_service_that_could_not_be_asked_throws_rather_than_answering_none(HttpStatusCode status)
    {
        // The whole point: "could not look" must never come back looking like
        // "there is none", because the caller would narrow on it and answer
        // that the sponsor overrides nothing.
        var (lookup, _) = Lookup(status, "{}");

        await ((Func<Task>)(() => lookup.GetPoliciesAsync(Tenant, "123456")))
            .Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_policy_with_no_id_is_skipped_rather_than_becoming_a_node_with_an_empty_name()
    {
        var (lookup, _) = Lookup(body: """
            [ { "policyNumber": "123456-0001" }, { "id": "t1|123456|123456-0002" } ]
            """);

        (await lookup.GetPoliciesAsync(Tenant, "123456")).Select(p => p.Id).Should().Equal("t1|123456|123456-0002");
    }

    [Fact]
    public async Task A_class_with_no_code_is_skipped()
    {
        var (lookup, _) = Lookup(body: """
            [ { "id": "t1|123456|123456-0001", "classes": [ { "name": "Hourly" }, { "code": "HOURLY" } ] } ]
            """);

        (await lookup.GetPoliciesAsync(Tenant, "123456"))
            .Single().ClassScopeIds.Should().Equal("t1|123456|123456-0001|HOURLY");
    }

    [Fact]
    public async Task A_body_that_is_not_an_array_is_no_policies_rather_than_a_crash()
    {
        var (lookup, _) = Lookup(body: """{ "error": "something" }""");

        (await lookup.GetPoliciesAsync(Tenant, "123456")).Should().BeEmpty();
    }

    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        public List<string> Tenants { get; } = new();
        public List<bool> ServiceIdentity { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Tenants.Add(request.Headers.TryGetValues("X-Tenant-ID", out var values) ? values.First() : string.Empty);
            ServiceIdentity.Add(request.Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var required) && required);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

/// <summary>
/// The node-id forms, which sponsor-service builds and tenant-service has to
/// rebuild to follow the hierarchy (the two services share no package).
///
/// <para>
/// These are the tripwire for that duplication: if sponsor-service's
/// <c>SponsorPolicy.IdFor</c> or <c>SponsorClass.ScopeIdFor</c> changes shape,
/// these fail rather than the narrowing quietly matching nothing and reporting
/// that a sponsor overrides none of its own policies.
/// </para>
/// </summary>
public class SponsorScopeIdTests
{
    [Fact]
    public void A_class_is_named_by_its_policy_and_its_code()
    {
        SponsorScopeIds.ForClass("t1|123456|123456-0001", "HOURLY")
            .Should().Be("t1|123456|123456-0001|HOURLY");
    }

    [Fact]
    public void A_policys_id_names_the_group_it_belongs_to()
    {
        SponsorScopeIds.GroupNumberOf("t1|123456|123456-0001").Should().Be("123456");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456-0001")]
    [InlineData("t1|123456")]
    [InlineData("t1||123456-0001")]
    [InlineData("t1|123456|123456-0001|HOURLY")]
    public void An_id_that_is_not_a_policys_cannot_be_told_which_group_it_is(string id)
    {
        // Null is "cannot tell", which the caller reports as an unnarrowed
        // level. A guess here would narrow the classes to the wrong group's or
        // to none.
        SponsorScopeIds.GroupNumberOf(id).Should().BeNull();
    }
}

/// <summary>A setting scope that names a level without the level it hangs off (§12.9).</summary>
public class SettingScopeCoherenceTests
{
    [Fact]
    public void A_scope_that_names_the_whole_chain_is_coherent()
    {
        new SettingScope
        {
            Sponsor = "123456",
            SponsorPolicy = "t1|123456|123456-0001",
            Class = "t1|123456|123456-0001|HOURLY",
        }.Incoherent().Should().BeEmpty();
    }

    [Fact]
    public void A_class_without_its_policy_is_refused()
    {
        // Resolving it would check the class and then the tenant, skipping the
        // policy and the sponsor, and answer as though neither had set
        // anything.
        new SettingScope { Sponsor = "123456", Class = "t1|123456|123456-0001|HOURLY" }
            .Incoherent().Should().ContainSingle().Which.Should().Contain("belongs to a sponsor policy");
    }

    [Fact]
    public void A_policy_without_its_sponsor_is_refused()
    {
        new SettingScope { SponsorPolicy = "t1|123456|123456-0001" }
            .Incoherent().Should().ContainSingle().Which.Should().Contain("belongs to a sponsor");
    }

    [Fact]
    public void A_class_with_neither_is_refused_for_both_reasons()
    {
        new SettingScope { Class = "t1|123456|123456-0001|HOURLY" }.Incoherent().Should().HaveCount(2);
    }

    [Fact]
    public void A_scope_with_nothing_on_the_contract_side_is_coherent()
    {
        new SettingScope { Program = "P1" }.Incoherent().Should().BeEmpty();
        SettingScope.Empty.Incoherent().Should().BeEmpty();
    }

    [Fact]
    public void The_chain_runs_from_the_class_up_to_the_tenant()
    {
        var chain = new SettingScope
        {
            LineOfBusiness = "Commercial",
            Carrier = "C1",
            Program = "P1",
            BenefitPlan = "PL1",
            Sponsor = "123456",
            SponsorPolicy = "t1|123456|123456-0001",
            Class = "t1|123456|123456-0001|HOURLY",
        }.Chain();

        chain.Select(n => n.Level).Should().Equal(
            SettingLevel.Class,
            SettingLevel.SponsorPolicy,
            SettingLevel.Sponsor,
            SettingLevel.BenefitPlan,
            SettingLevel.Program,
            SettingLevel.LineOfBusiness,
            SettingLevel.Carrier,
            SettingLevel.Tenant);
    }

    [Fact]
    public void Two_scopes_differing_only_in_their_class_do_not_share_a_cache_key()
    {
        var policy = new SettingScope { Sponsor = "123456", SponsorPolicy = "t1|123456|123456-0001" };

        var hourly = policy with { Class = "t1|123456|123456-0001|HOURLY" };
        var salaried = policy with { Class = "t1|123456|123456-0001|SALARIED" };

        hourly.CacheKey.Should().NotBe(salaried.CacheKey);
        hourly.CacheKey.Should().NotBe(policy.CacheKey);
    }
}
