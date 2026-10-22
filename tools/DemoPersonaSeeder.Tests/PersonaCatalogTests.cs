using System.Text.Json;
using DemoPersonaSeeder;
using FluentAssertions;
using Xunit;

namespace DemoPersonaSeeder.Tests;

/// <summary>The cast hangs together: every reference resolves and every ID passes the services' format checks.</summary>
public class PersonaCatalogTests
{
    private static readonly DateTime AsOf = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
    private readonly DemoDataset _data = PersonaCatalog.Build(AsOf);

    [Fact]
    public void Npi_check_digit_matches_the_CMS_example()
        => Identifiers.Npi("123456789").Should().Be("1234567893");

    [Fact]
    public void Every_provider_has_a_valid_unique_NPI()
    {
        _data.Providers.Should().OnlyContain(p => Identifiers.IsValidNpi(p.Npi));
        _data.Providers.Select(p => p.Npi).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Business_keys_are_unique()
    {
        _data.Members.Select(m => m.MemberId).Should().OnlyHaveUniqueItems();
        _data.Claims.Select(c => c.ClaimNumber).Should().OnlyHaveUniqueItems();
        _data.Authorizations.Select(a => a.Number).Should().OnlyHaveUniqueItems();
        _data.Appeals.Select(a => a.AppealNumber).Should().OnlyHaveUniqueItems();
        _data.Plans.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Dependents_belong_to_a_subscriber_in_their_group_and_plan()
    {
        foreach (var dependent in _data.Members.Where(m => !m.IsSubscriber))
        {
            var subscriber = _data.Member(dependent.SubscriberMemberId!);
            subscriber.IsSubscriber.Should().BeTrue();
            subscriber.GroupNumber.Should().Be(dependent.GroupNumber);
            subscriber.PlanKey.Should().Be(dependent.PlanKey);
            dependent.RelationshipCode.Should().NotBe("18");
        }
    }

    [Fact]
    public void Every_reference_resolves()
    {
        var act = () =>
        {
            foreach (var m in _data.Members)
            {
                _data.Plan(m.PlanKey);
                if (m.PcpProviderKey is not null) _data.Provider(m.PcpProviderKey);
            }
            foreach (var a in _data.Authorizations)
            {
                _data.Member(a.MemberId);
                _data.Provider(a.RequestingProviderKey);
                if (a.ServicingProviderKey is not null) _data.Provider(a.ServicingProviderKey);
                if (a.FacilityProviderKey is not null) _data.Provider(a.FacilityProviderKey);
            }
            foreach (var c in _data.Claims)
            {
                _data.Member(c.MemberId);
                _data.Plan(c.PlanKey);
                _data.Provider(c.BillingProviderKey);
                _data.Provider(c.RenderingProviderKey);
                if (c.PriorAuthorizationNumber is not null)
                    _data.Authorizations.Should().Contain(a => a.Number == c.PriorAuthorizationNumber);
            }
            foreach (var a in _data.Appeals) _data.Claim(a.ClaimNumber);
            foreach (var c in _data.Contracts) _data.Provider(c.ProviderKey);
            foreach (var g in _data.BilledGroups) _data.Sponsors.Should().Contain(s => s.GroupNumber == g);
        };
        act.Should().NotThrow();
    }

    [Fact]
    public void Claims_use_the_member_plan_and_fall_inside_coverage()
    {
        var planStart = new DateTime(AsOf.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var c in _data.Claims)
        {
            c.PlanKey.Should().Be(_data.Member(c.MemberId).PlanKey, c.ClaimNumber);
            c.ServiceDateFrom.Should().BeOnOrAfter(planStart).And.BeBefore(AsOf, c.ClaimNumber);
        }
    }

    [Fact]
    public void Each_PCP_is_flagged_and_participates_in_the_member_plan()
    {
        foreach (var m in _data.Members.Where(m => m.PcpProviderKey is not null))
        {
            var pcp = _data.Provider(m.PcpProviderKey!);
            pcp.IsPcp.Should().BeTrue();
            pcp.PlanKeys.Should().Contain(m.PlanKey);
        }
    }

    [Fact]
    public void Billed_group_members_carry_a_premium()
        => _data.Members.Where(m => _data.BilledGroups.Contains(m.GroupNumber))
            .Should().OnlyContain(m => m.MonthlyPremium > 0);

    [Fact]
    public void Similar_names_exist_for_the_sounds_like_story()
    {
        _data.Members.Should().Contain(m => m.LastName == "Smith");
        _data.Members.Should().Contain(m => m.LastName == "Smyth");
    }

    [Fact]
    public void Stories_are_dated_from_as_of()
    {
        var expedited = _data.Authorizations.Single(a => a.Number == "AUTH-DEMO-1002");
        expedited.LevelOfService.Should().Be("U");
        expedited.Decision.Should().BeNull();
        expedited.From.Should().Be(AsOf.AddDays(1));
    }

    [Fact]
    public void Member_payload_carries_every_required_field()
    {
        var json = Serialize(Payloads.Member(_data.Member("DSB100102"), AsOf));
        foreach (var field in new[] { "memberId", "groupNumber", "firstName", "lastName", "dateOfBirth", "effectiveDate" })
            json.GetProperty(field).ToString().Should().NotBeNullOrWhiteSpace(field);
        json.GetProperty("isSubscriber").GetBoolean().Should().BeFalse();
        json.GetProperty("subscriberMemberId").GetString().Should().Be("DSB100101");
    }

    [Fact]
    public void Claim_payload_uses_the_business_plan_id_and_the_subscriber()
    {
        var claim = _data.Claim("CLM-DEMO-1004");
        var json = Serialize(Payloads.Claim(claim, _data, "demo"));
        json.GetProperty("benefitPlanId").GetString().Should().Be("DEMO-COMM-PPO");
        json.GetProperty("memberId").GetString().Should().Be("DSB100103");
        json.GetProperty("subscriberId").GetString().Should().Be("DSB100101");
        json.GetProperty("totalChargeAmount").GetDecimal().Should().Be(claim.Lines.Sum(l => l.Charge));
        json.GetProperty("claimLines").GetArrayLength().Should().Be(claim.Lines.Count);
    }

    [Fact]
    public void Coverage_payload_uses_the_plan_guid_and_flags_COB()
    {
        var json = Serialize(Payloads.Coverage(_data.Member("DSB100102"), _data, AsOf));
        json.GetProperty("planId").GetString().Should().Be(_data.Plan(PersonaCatalog.CommercialPlan).Id.ToString());
        json.GetProperty("otherInsurance").GetProperty("payerName").GetString().Should().Be("Copper Mutual Insurance");
        json.GetProperty("maintenanceReasonCode").GetString().Should().Be("COB");
    }

    [Fact]
    public void Provider_payload_participates_in_each_plan_network()
    {
        var rahman = _data.Provider("rahman");
        var json = Serialize(Payloads.Provider(rahman, _data, "demo", AsOf));
        var participations = json.GetProperty("networkParticipations").EnumerateArray().ToList();
        participations.Should().HaveCount(rahman.PlanKeys.Count);
        participations.Select(p => p.GetProperty("networkId").GetString())
            .Should().BeEquivalentTo(rahman.PlanKeys.Select(k => _data.Plan(k).InNetworkId));
    }

    private static JsonElement Serialize(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload, Seeder.Json)).RootElement.Clone();
}
