using AuthorizationService.Models;
using AuthorizationService.Services.ClaimMatching;
using Xunit;

namespace AuthorizationService.Tests.Services.ClaimMatching;

/// <summary>Matching a claim service with no authorization number to the plan's approved authorizations.</summary>
public class AuthorizationClaimMatcherTests
{
    private static readonly DateTime Today = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ServiceDate = new(2026, 4, 15);

    private static Authorization Auth(
        string number, AuthorizationStatus status = AuthorizationStatus.Approved, string member = "MEM-1",
        DateTime? from = null, DateTime? to = null, string servicing = "1234567890", params (string Code, string Status)[] services) => new()
    {
        AuthorizationNumber = number,
        MemberId = member,
        Status = status,
        RequestingProviderNPI = "9999999999",
        ServicingProviderNPI = servicing,
        RequestedServiceDateFrom = from ?? new DateTime(2026, 4, 1),
        ApprovedServiceDateFrom = from ?? new DateTime(2026, 4, 1),
        ApprovedServiceDateTo = to ?? new DateTime(2026, 4, 30),
        CriteriaReference = "InterQual 2026 Imaging",
        RequestedServices = services.Select((s, i) => new RequestedService
        {
            LineNumber = i + 1, ProcedureCode = s.Code, ServiceStatus = s.Status,
        }).ToList(),
    };

    private static IReadOnlyList<AuthorizationMatchCandidate> Match(params Authorization[] auths) =>
        AuthorizationClaimMatcher.Match(auths, "MEM-1", ServiceDate, "70551", "1234567890", Today);

    [Fact]
    public void An_approval_covering_member_date_procedure_and_provider_matches()
    {
        var match = Assert.Single(Match(Auth("A-1", services: ("70551", "A1"))));

        Assert.Equal("A-1", match.AuthorizationNumber);
        Assert.Equal(1, match.LineNumber);
        Assert.Equal("InterQual 2026 Imaging", match.CriteriaReference);
        Assert.Equal(["member", "service date", "procedure", "provider"], match.MatchedOn);
    }

    [Fact]
    public void Another_member_a_date_outside_the_period_or_a_denial_do_not_match()
    {
        Assert.Empty(Match(Auth("A-1", member: "MEM-2", services: ("70551", "A1"))));
        Assert.Empty(Match(Auth("A-1", from: new DateTime(2026, 5, 1), to: new DateTime(2026, 5, 31), services: ("70551", "A1"))));
        Assert.Empty(Match(Auth("A-1", status: AuthorizationStatus.Denied, services: ("70551", "A1"))));
        Assert.Empty(Match(Auth("A-1", status: AuthorizationStatus.Pended, services: ("70551", "A1"))));
    }

    [Fact]
    public void A_service_line_must_approve_the_procedure_and_the_named_provider_must_match()
    {
        Assert.Empty(Match(Auth("A-1", services: ("70553", "A1"))));
        Assert.Empty(Match(Auth("A-1", services: ("70551", "A3"))));
        Assert.Empty(Match(Auth("A-1", servicing: "5555555555", services: ("70551", "A1"))));
    }

    [Fact]
    public void A_line_without_its_own_decision_carries_the_approval()
    {
        var auth = Auth("A-1", services: ("70551", "A1"));
        auth.RequestedServices[0].ServiceStatus = null;

        Assert.Single(Match(auth));
    }

    [Fact]
    public void An_admission_approval_without_service_lines_matches_on_member_and_date()
    {
        var match = Assert.Single(Match(Auth("ADM-1")));
        Assert.Null(match.LineNumber);
        Assert.DoesNotContain("procedure", match.MatchedOn);
    }

    [Fact]
    public void An_expired_approval_still_covers_a_service_inside_its_period()
    {
        var expired = Auth("A-1", status: AuthorizationStatus.Expired, services: ("70551", "A1"));
        expired.ReviewDecision = "A1";

        Assert.Single(Match(expired));
    }

    [Fact]
    public void The_most_specific_match_comes_first()
    {
        var matches = Match(Auth("ADM-1"), Auth("A-1", services: ("70551", "A1")));

        Assert.Equal(["A-1", "ADM-1"], matches.Select(m => m.AuthorizationNumber));
    }
}
