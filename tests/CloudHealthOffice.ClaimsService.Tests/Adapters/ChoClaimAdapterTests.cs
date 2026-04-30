using ClaimsService.Adapters;
using ClaimsService.Models;
using ClaimsService.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Adapters;

public class ChoClaimAdapterTests
{
    private const string Tenant = "tenant-a";

    private static ChoClaimAdapter Build(IClaimRepository repo)
        => new(repo, NullLogger<ChoClaimAdapter>.Instance);

    private static Claim SampleClaim(string id = "c-1", string claimNumber = "CN-001")
        => new()
        {
            Id = id,
            TenantId = Tenant,
            ClaimNumber = claimNumber,
            MemberId = "M1",
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            Status = ClaimStatus.Submitted,
            TotalChargeAmount = 100m,
            ServiceDateFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            SubmittedDate = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
            ClaimVersionId = id,
            VersionNumber = 1,
            VersionState = ClaimVersionState.Submitted,
            ClaimLines = new List<ClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Modifiers = new List<string> { "25" },
                    DiagnosisPointers = new List<int> { 1 },
                    Units = 1,
                    ChargeAmount = 100m,
                    ServiceDateFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                    ServiceDateTo = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
                },
            },
            DiagnosisCodes = new List<DiagnosisCode>
            {
                new() { Code = "E11.9", CodeQualifier = "ABK", PointerNumber = 1 },
            },
        };

    [Fact]
    public void Platform_is_cho()
    {
        var repo = Substitute.For<IClaimRepository>();
        Build(repo).Platform.Should().Be("cho");
    }

    [Fact]
    public async Task GetClaimAsync_uses_GetByIdAsync_when_only_ClaimId_provided()
    {
        var claim = SampleClaim("c-1");
        var repo = Substitute.For<IClaimRepository>();
        repo.GetByIdAsync("c-1").Returns(claim);

        var resp = await Build(repo).GetClaimAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant,
            ClaimId = "c-1",
        });

        resp.Platform.Should().Be("cho");
        resp.Claim.Should().NotBeNull();
        resp.Claim!.Id.Should().Be("c-1");
        await repo.Received(1).GetByIdAsync("c-1");
        await repo.DidNotReceive().GetLatestVersionAsync(Arg.Any<string>(), Arg.Any<DateTime>());
    }

    [Fact]
    public async Task GetClaimAsync_uses_GetLatestVersionAsync_when_ClaimVersionId_provided()
    {
        var claim = SampleClaim("c-2");
        var asOf = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var repo = Substitute.For<IClaimRepository>();
        repo.GetLatestVersionAsync("chain-2", asOf).Returns(claim);

        var resp = await Build(repo).GetClaimAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant,
            ClaimVersionId = "chain-2",
            AsOf = asOf,
        });

        resp.Claim!.Id.Should().Be("c-2");
        await repo.Received(1).GetLatestVersionAsync("chain-2", asOf);
        await repo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task GetClaimAsync_returns_null_payload_when_not_found()
    {
        var repo = Substitute.For<IClaimRepository>();
        repo.GetByIdAsync("missing").Returns((Claim?)null);

        var resp = await Build(repo).GetClaimAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant,
            ClaimId = "missing",
        });

        resp.Claim.Should().BeNull();
    }

    [Fact]
    public async Task GetClaimAsync_throws_when_neither_id_provided()
    {
        var repo = Substitute.For<IClaimRepository>();
        await Build(repo).Invoking(a => a.GetClaimAsync(new ClaimAdapterRequest { TenantId = Tenant }))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetClaimByNumberAsync_delegates_to_repo()
    {
        var claim = SampleClaim("c-3", "CN-XYZ");
        var repo = Substitute.For<IClaimRepository>();
        repo.GetByClaimNumberAsync("CN-XYZ").Returns(claim);

        var resp = await Build(repo).GetClaimByNumberAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant,
            ClaimNumber = "CN-XYZ",
        });

        resp.Claim!.ClaimNumber.Should().Be("CN-XYZ");
    }

    [Fact]
    public async Task GetClaimByNumberAsync_throws_when_claim_number_missing()
    {
        var repo = Substitute.For<IClaimRepository>();
        await Build(repo).Invoking(a => a.GetClaimByNumberAsync(new ClaimAdapterRequest { TenantId = Tenant }))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetClaimVersionAsync_delegates_to_repo()
    {
        var claim = SampleClaim("c-4");
        claim.VersionNumber = 3;
        var repo = Substitute.For<IClaimRepository>();
        repo.GetVersionAsync("chain-4", "v-3").Returns(claim);

        var resp = await Build(repo).GetClaimVersionAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant,
            ClaimVersionId = "chain-4",
            VersionId = "v-3",
        });

        resp.Claim!.VersionNumber.Should().Be(3);
    }

    [Fact]
    public async Task GetClaimVersionAsync_throws_when_either_id_missing()
    {
        var repo = Substitute.For<IClaimRepository>();
        await Build(repo).Invoking(a => a.GetClaimVersionAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant, ClaimVersionId = "chain-only"
        })).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListClaimVersionsAsync_returns_versions_and_continuation_token()
    {
        var first = SampleClaim("v-1");
        first.VersionNumber = 2;
        var second = SampleClaim("v-2");
        second.VersionNumber = 1;
        var repo = Substitute.For<IClaimRepository>();
        repo.ListVersionsAsync("chain-5", 10, null)
            .Returns((new List<Claim> { first, second }, "next-page-token"));

        var resp = await Build(repo).ListClaimVersionsAsync(new ClaimAdapterRequest
        {
            TenantId = Tenant,
            ClaimVersionId = "chain-5",
            PageSize = 10,
        });

        resp.Versions.Should().HaveCount(2);
        resp.Versions[0].Id.Should().Be("v-1");
        resp.ContinuationToken.Should().Be("next-page-token");
    }

    [Fact]
    public async Task ListClaimVersionsAsync_throws_when_chain_id_missing()
    {
        var repo = Substitute.For<IClaimRepository>();
        await Build(repo).Invoking(a => a.ListClaimVersionsAsync(new ClaimAdapterRequest { TenantId = Tenant }))
            .Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// AdapterClaim → Claim → IClaimRepository.CreateAsync → Claim → AdapterClaim.
    /// The most important test on the AdapterClaim DTO decision: the round-trip
    /// must be lossless on the submission path. Every domain field present on
    /// the input AdapterClaim must reach CreateAsync intact, and every field
    /// the repo sets must surface on the response AdapterClaim.
    /// </summary>
    [Fact]
    public async Task SubmitClaimAsync_round_trips_AdapterClaim_losslessly()
    {
        var domainClaim = SampleClaim("c-submit");
        domainClaim.SubscriberId = "S1";
        domainClaim.BenefitPlanId = "BP-1";
        domainClaim.PriorAuthorizationNumber = "PA-42";
        domainClaim.AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = "InNetwork",
            AllowedAmount = 80m,
            DeductibleAmount = 10m,
            CoinsuranceAmount = 5m,
            CopayAmount = 5m,
            PatientResponsibility = 20m,
            PayerPayment = 60m,
            RemarkCodes = new List<string> { "N123" },
        };
        domainClaim.PendDetails = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = "NCCI pair edit",
            PendedAt = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
        };
        domainClaim.AiExamination = new AiExamination
        {
            RecommendedDisposition = "Approve",
            ConfidenceScore = 0.88,
            Rationale = "Modifier 59 supports separate procedure",
            ModelId = "claude-opus-4-7",
            PromptVersion = "ncci-pend-v1",
        };

        // Round-trip the input through the DTO (mirrors how a real consumer
        // would build a submission request) and capture what the repo sees.
        var inputAdapter = AdapterClaim.From(domainClaim);

        Claim? captured = null;
        var repo = Substitute.For<IClaimRepository>();
        repo.CreateAsync(Arg.Do<Claim>(c => captured = c)).Returns(call => call.Arg<Claim>());

        var resp = await Build(repo).SubmitClaimAsync(new ClaimSubmissionAdapterRequest
        {
            TenantId = Tenant,
            Claim = inputAdapter,
            CorrelationId = "corr-1",
        });

        // 1. Repo received the fully-mapped domain Claim.
        captured.Should().NotBeNull();
        captured!.Id.Should().Be("c-submit");
        captured.ClaimNumber.Should().Be("CN-001");
        captured.SubscriberId.Should().Be("S1");
        captured.BenefitPlanId.Should().Be("BP-1");
        captured.PriorAuthorizationNumber.Should().Be("PA-42");
        captured.ClaimLines.Should().HaveCount(1);
        captured.ClaimLines[0].ProcedureCode.Should().Be("99213");
        captured.ClaimLines[0].Modifiers.Should().Equal("25");
        captured.DiagnosisCodes.Should().HaveCount(1);
        captured.DiagnosisCodes[0].Code.Should().Be("E11.9");
        captured.AdjudicationResult.Should().NotBeNull();
        captured.AdjudicationResult!.AllowedAmount.Should().Be(80m);
        captured.AdjudicationResult.RemarkCodes.Should().Equal("N123");
        captured.PendDetails.Should().NotBeNull();
        captured.PendDetails!.PendCode.Should().Be("NCCI");
        captured.AiExamination.Should().NotBeNull();
        captured.AiExamination!.RecommendedDisposition.Should().Be("Approve");
        captured.AiExamination.ConfidenceScore.Should().Be(0.88);
        captured.ClaimVersionId.Should().Be("c-submit");
        captured.VersionNumber.Should().Be(1);
        captured.VersionState.Should().Be(ClaimVersionState.Submitted);

        // 2. Response wraps back into AdapterClaim with all fields preserved.
        resp.Platform.Should().Be("cho");
        resp.Claim.Should().NotBeNull();
        resp.Claim!.Id.Should().Be("c-submit");
        resp.Claim.SubscriberId.Should().Be("S1");
        resp.Claim.PriorAuthorizationNumber.Should().Be("PA-42");
        resp.Claim.ClaimLines.Should().HaveCount(1);
        resp.Claim.ClaimLines[0].Modifiers.Should().Equal("25");
        resp.Claim.AdjudicationResult.Should().NotBeNull();
        resp.Claim.AdjudicationResult!.AllowedAmount.Should().Be(80m);
        resp.Claim.PendDetails!.PendCode.Should().Be("NCCI");
        resp.Claim.AiExamination!.ConfidenceScore.Should().Be(0.88);
    }

    [Fact]
    public async Task SubmitClaimAsync_delegates_to_CreateAsync_only_no_event_publisher_calls()
    {
        var repo = Substitute.For<IClaimRepository>();
        repo.CreateAsync(Arg.Any<Claim>()).Returns(call => call.Arg<Claim>());

        await Build(repo).SubmitClaimAsync(new ClaimSubmissionAdapterRequest
        {
            TenantId = Tenant,
            Claim = AdapterClaim.From(SampleClaim()),
        });

        // Only CreateAsync is called — Update / projection bypass / publisher
        // are explicitly NOT on the adapter's submission path. 5.3 is the
        // capability that wires event emission for submissions.
        await repo.Received(1).CreateAsync(Arg.Any<Claim>());
        await repo.DidNotReceive().UpdateAsync(Arg.Any<Claim>());
        await repo.DidNotReceive().UpdateAdjudicationProjectionAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<AdjudicationResult>(),
            Arg.Any<IReadOnlyList<LineAdjudicationResult>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchClaimsAsync_passes_filters_through_to_repo()
    {
        var repo = Substitute.For<IClaimRepository>();
        repo.SearchAsync(
            "M1", "1234567890",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            ClaimStatus.Submitted, LineOfBusiness.Commercial, 2, 25)
            .Returns(new List<Claim> { SampleClaim("s-1"), SampleClaim("s-2") });

        var resp = await Build(repo).SearchClaimsAsync(new ClaimSearchAdapterRequest
        {
            TenantId = Tenant,
            MemberId = "M1",
            ProviderNPI = "1234567890",
            ServiceDateFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
            Status = ClaimStatus.Submitted,
            LineOfBusiness = LineOfBusiness.Commercial,
            Page = 2,
            PageSize = 25,
        });

        resp.Claims.Should().HaveCount(2);
        resp.Platform.Should().Be("cho");
    }

    [Fact]
    public async Task SearchClaimsForMemberAsync_returns_page_with_total_count()
    {
        var repo = Substitute.For<IClaimRepository>();
        repo.SearchForMemberAsync(
            "M9",
            Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<ClaimStatus?>(), Arg.Any<string?>(),
            Arg.Any<ClaimType?>(),
            Arg.Any<decimal?>(), Arg.Any<decimal?>(),
            1, 50)
            .Returns(((IReadOnlyList<Claim>)new List<Claim> { SampleClaim("m-1") }, 17));

        var resp = await Build(repo).SearchClaimsForMemberAsync(new ClaimMemberSearchAdapterRequest
        {
            TenantId = Tenant,
            MemberId = "M9",
        });

        resp.Claims.Should().HaveCount(1);
        resp.TotalCount.Should().Be(17);
    }

    [Fact]
    public async Task SearchClaimsForMemberAsync_throws_when_member_id_missing()
    {
        var repo = Substitute.For<IClaimRepository>();
        await Build(repo).Invoking(a => a.SearchClaimsForMemberAsync(new ClaimMemberSearchAdapterRequest
        {
            TenantId = Tenant
        })).Should().ThrowAsync<ArgumentException>();
    }
}
