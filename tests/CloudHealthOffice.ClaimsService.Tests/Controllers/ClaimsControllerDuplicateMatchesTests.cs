using CloudHealthOffice.Infrastructure.Security;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Controllers;

/// <summary>
/// <c>GET /api/claims/{id}/duplicate-matches</c> and the work-queue
/// duplicate hint: the examiner-facing projection of the findings
/// DuplicateClaimStage persists on <see cref="PendDetails.DuplicateFindings"/>.
/// </summary>
public class ClaimsControllerDuplicateMatchesTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Dos = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly HttpClient _client;
    private readonly IClaimRepository _repo;

    public ClaimsControllerDuplicateMatchesTests(ClaimsApiFactory factory)
    {
        _repo = factory.ClaimRepository;
        _client = factory.CreateDefaultClient(
            new ChoDevelopmentTokenHandler("examiner-1", ChoRolePermissions.TenantAdmin));
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
    }

    private static ClaimLine Line(int number, string code, decimal charge, decimal units = 1, params string[] modifiers) => new()
    {
        LineNumber = number,
        ProcedureCode = code,
        ChargeAmount = charge,
        Units = units,
        Modifiers = modifiers.ToList(),
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
    };

    private static Claim NewClaim(string id, string number, string npi, params ClaimLine[] lines) => new()
    {
        Id = id,
        TenantId = "test-tenant",
        ClaimNumber = number,
        MemberId = "MEM-1",
        BillingProviderNPI = npi,
        LineOfBusiness = LineOfBusiness.Commercial,
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
        TotalChargeAmount = lines.Sum(l => l.ChargeAmount),
        Status = ClaimStatus.Approved,
        LastUpdatedDate = DateTime.UtcNow.AddDays(-1),
        ClaimLines = lines.ToList(),
    };

    private static DuplicateFindingSnapshot Finding(
        string type, int line, string matchedId, string matchedNumber, int matchedLine) => new()
    {
        DuplicateType = type,
        RuleId = type == "Exact" ? "DUP001" : "DUP002",
        Message = $"Line {line} vs claim {matchedNumber} ({matchedId}) line {matchedLine}",
        LineNumber = line,
        MatchedClaimId = matchedId,
        MatchedClaimNumber = matchedNumber,
        MatchedLineNumber = matchedLine,
        SuggestedCarc = "18",
    };

    private Claim PendedDuplicateClaim(string id, params DuplicateFindingSnapshot[] findings)
    {
        var claim = NewClaim(id, "CLM-NEW", "1111111111",
            Line(1, "99213", 100m),
            Line(2, "99214", 150m, 1, "25"));
        claim.Status = ClaimStatus.Pended;
        claim.PendDetails = new PendDetails
        {
            PendCode = "DUPLICATE",
            PendReason = "Suspect duplicate",
            PendedAt = DateTime.UtcNow.AddHours(-1),
            DuplicateFindings = findings.ToList(),
        };
        return claim;
    }

    [Fact]
    public async Task DuplicateMatches_returns_one_entry_per_matched_claim_with_summary_and_matched_fields()
    {
        var claim = PendedDuplicateClaim("dup-claim-1",
            Finding("Exact", 1, "prior-exact", "CLM-PRIOR-A", 1),
            Finding("Suspect", 2, "prior-suspect", "CLM-PRIOR-B", 3));
        var exactPrior = NewClaim("prior-exact", "CLM-PRIOR-A", "1111111111", Line(1, "99213", 100m));
        exactPrior.Status = ClaimStatus.Paid;
        // Suspect prior: different billing NPI and charge, no modifier.
        var suspectPrior = NewClaim("prior-suspect", "CLM-PRIOR-B", "2222222222",
            Line(1, "80053", 20m), Line(2, "36415", 5m), Line(3, "99214", 175m));

        _repo.GetByIdAsync("dup-claim-1").Returns(claim);
        _repo.GetByIdAsync("prior-exact").Returns(exactPrior);
        _repo.GetByIdAsync("prior-suspect").Returns(suspectPrior);

        var response = await _client.GetAsync("/api/claims/dup-claim-1/duplicate-matches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = await response.Content.ReadFromJsonAsync<List<ClaimDuplicateMatch>>(Json);
        Assert.NotNull(matches);
        Assert.Equal(2, matches!.Count);

        var exact = matches[0];
        Assert.Equal("prior-exact", exact.MatchedClaimId);
        Assert.Equal("CLM-PRIOR-A", exact.MatchedClaimNumber);
        Assert.Equal("Exact", exact.MatchType);
        Assert.True(exact.MatchedClaimFound);
        Assert.Equal(Dos, exact.ServiceDateFrom);
        Assert.Equal(Dos, exact.ServiceDateTo);
        Assert.Equal(100m, exact.BilledAmount);
        Assert.Equal("Paid", exact.Status);
        Assert.Equal(
            new[] { "Member", "Billing provider", "Service dates", "Procedure code", "Modifiers", "Units", "Charge" },
            exact.MatchedFields);
        var exactLine = Assert.Single(exact.Lines);
        Assert.Equal(1, exactLine.LineNumber);
        Assert.Equal(1, exactLine.MatchedLineNumber);
        Assert.Equal("DUP001", exactLine.RuleId);

        var suspect = matches[1];
        Assert.Equal("prior-suspect", suspect.MatchedClaimId);
        Assert.Equal("Suspect", suspect.MatchType);
        Assert.Equal(200m, suspect.BilledAmount);
        Assert.Equal("Approved", suspect.Status);
        Assert.Equal(new[] { "Member", "Service dates", "Procedure code", "Units" }, suspect.MatchedFields);
        Assert.Equal(3, Assert.Single(suspect.Lines).MatchedLineNumber);
    }

    [Fact]
    public async Task DuplicateMatches_unreadable_matched_claim_reports_not_found_with_implied_fields()
    {
        var claim = PendedDuplicateClaim("dup-claim-2",
            Finding("Suspect", 1, "prior-gone", "CLM-GONE", 1));
        _repo.GetByIdAsync("dup-claim-2").Returns(claim);
        _repo.GetByIdAsync("prior-gone").Returns((Claim?)null);

        var response = await _client.GetAsync("/api/claims/dup-claim-2/duplicate-matches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = Assert.Single((await response.Content.ReadFromJsonAsync<List<ClaimDuplicateMatch>>(Json))!);
        Assert.Equal("CLM-GONE", match.MatchedClaimNumber);
        Assert.False(match.MatchedClaimFound);
        Assert.Null(match.Status);
        Assert.Null(match.BilledAmount);
        Assert.Null(match.ServiceDateFrom);
        Assert.Equal(new[] { "Member", "Service dates", "Procedure code" }, match.MatchedFields);
    }

    [Fact]
    public async Task DuplicateMatches_claim_without_findings_returns_empty_list()
    {
        var claim = NewClaim("clean-claim", "CLM-CLEAN", "1111111111", Line(1, "99213", 100m));
        _repo.GetByIdAsync("clean-claim").Returns(claim);

        var response = await _client.GetAsync("/api/claims/clean-claim/duplicate-matches");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var matches = await response.Content.ReadFromJsonAsync<List<ClaimDuplicateMatch>>(Json);
        Assert.NotNull(matches);
        Assert.Empty(matches!);
    }

    [Fact]
    public async Task DuplicateMatches_unknown_claim_returns_404()
    {
        _repo.GetByIdAsync("no-such-claim").Returns((Claim?)null);

        var response = await _client.GetAsync("/api/claims/no-such-claim/duplicate-matches");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WorkQueueItems_duplicate_pend_lists_matched_claim_numbers()
    {
        var claim = PendedDuplicateClaim("dup-claim-wq",
            Finding("Exact", 1, "prior-exact", "CLM-PRIOR-A", 1),
            Finding("Suspect", 2, "prior-exact", "CLM-PRIOR-A", 2));
        _repo.SearchAsync(
                memberId: null, providerNPI: null,
                serviceDateFrom: null, serviceDateTo: null,
                status: ClaimStatus.Pended, lineOfBusiness: null,
                page: 1, pageSize: Arg.Any<int>())
            .Returns(new[] { claim });

        var response = await _client.GetAsync("/api/claims/work-queue/items");
        response.EnsureSuccessStatusCode();

        var item = Assert.Single((await response.Content.ReadFromJsonAsync<List<WorkQueueItem>>(Json))!);
        Assert.Equal("DUPLICATE", item.QueueReasonCode);
        Assert.Equal("Possible Duplicate", item.QueueReason);
        Assert.Equal(new[] { "CLM-PRIOR-A" }, item.DuplicateMatchedClaimNumbers);
    }

    [Fact]
    public void Builder_fallback_suspect_on_revenue_only_line_reports_revenue_code()
    {
        // Mirrors DuplicateClaimStage.LineKey.SuspectCode: with no procedure
        // code the suspect match keyed on the revenue code.
        var claim = PendedDuplicateClaim("dup-rev", Finding("Suspect", 1, "prior-gone", "CLM-GONE", 1));
        claim.ClaimLines[0].ProcedureCode = string.Empty;
        claim.ClaimLines[0].RevenueCode = "0450";

        var match = Assert.Single(DuplicateMatchBuilder.Build(claim, new Dictionary<string, Claim>()));

        Assert.False(match.MatchedClaimFound);
        Assert.Equal(new[] { "Member", "Service dates", "Revenue code" }, match.MatchedFields);
    }

    [Fact]
    public void Builder_fallback_exact_lists_only_codes_the_line_carries()
    {
        var procedureOnly = PendedDuplicateClaim("dup-exact-p", Finding("Exact", 1, "prior-gone", "CLM-GONE", 1));
        var revenueOnly = PendedDuplicateClaim("dup-exact-r", Finding("Exact", 1, "prior-gone", "CLM-GONE", 1));
        revenueOnly.ClaimLines[0].ProcedureCode = string.Empty;
        revenueOnly.ClaimLines[0].RevenueCode = "0450";

        var p = Assert.Single(DuplicateMatchBuilder.Build(procedureOnly, new Dictionary<string, Claim>()));
        var r = Assert.Single(DuplicateMatchBuilder.Build(revenueOnly, new Dictionary<string, Claim>()));

        Assert.Equal(
            new[] { "Member", "Billing provider", "Service dates", "Procedure code", "Modifiers", "Units", "Charge" },
            p.MatchedFields);
        Assert.Equal(
            new[] { "Member", "Billing provider", "Service dates", "Revenue code", "Modifiers", "Units", "Charge" },
            r.MatchedFields);
    }

    [Fact]
    public void Builder_caps_distinct_matched_claims()
    {
        var findings = Enumerable.Range(1, DuplicateMatchBuilder.MaxMatchedClaims + 5)
            .Select(i => Finding("Suspect", 1, $"prior-{i}", $"CLM-{i}", 1))
            .ToArray();
        var claim = PendedDuplicateClaim("dup-cap", findings);

        var matches = DuplicateMatchBuilder.Build(claim, new Dictionary<string, Claim>());

        Assert.Equal(DuplicateMatchBuilder.MaxMatchedClaims, matches.Count);
        Assert.Equal(DuplicateMatchBuilder.MaxMatchedClaims, DuplicateMatchBuilder.MatchedClaimIds(claim).Count);
    }
}
