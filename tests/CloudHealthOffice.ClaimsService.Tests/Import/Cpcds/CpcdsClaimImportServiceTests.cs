using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using ClaimsService.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds;

/// <summary>
/// Loading and reloading: what is written, what is left alone, and what is
/// refused. The claim store is an in-memory stand-in for the version chain.
/// </summary>
public class CpcdsClaimImportServiceTests
{
    private const string Tenant = "tenant-a";

    private readonly List<Claim> _store = [];
    private readonly List<ClaimImportTransaction> _transactions = [];
    private readonly IClaimRepository _claims = Substitute.For<IClaimRepository>();
    private readonly IClaimImportTransactionRepository _transactionRepo = Substitute.For<IClaimImportTransactionRepository>();
    private ICoreMemberCrosswalk _crosswalk = new PassThroughMemberCrosswalk();

    public CpcdsClaimImportServiceTests()
    {
        _claims.GetByClaimNumberAsync(Arg.Any<string>()).Returns(ci => _store
            .Where(c => c.ClaimNumber == ci.Arg<string>())
            .OrderByDescending(c => c.VersionNumber)
            .FirstOrDefault());
        _claims.CreateAsync(Arg.Any<Claim>()).Returns(ci =>
        {
            var claim = ci.Arg<Claim>();
            if (_store.Any(c => c.Id == claim.Id))
                throw new InvalidOperationException("duplicate key " + claim.Id);
            _store.Add(claim);
            return claim;
        });
        _claims.GetVersionAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => _store.FirstOrDefault(c => c.Id == ci.ArgAt<string>(1)));
        _claims.MarkSupersededProjectionAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var claim = _store.FirstOrDefault(c => c.Id == ci.ArgAt<string>(1));
                if (claim is null) return false;
                claim.SupersededAt = ci.ArgAt<DateTime>(3);
                claim.SupersededByVersionId = ci.ArgAt<string>(2);
                claim.VersionState = ClaimVersionState.Adjusted;
                return true;
            });
        _transactionRepo.CreateAsync(Arg.Any<ClaimImportTransaction>())
            .Returns(ci => { _transactions.Add(ci.Arg<ClaimImportTransaction>()); return ci.Arg<ClaimImportTransaction>(); });
    }

    private CpcdsClaimImportService Service() =>
        new(_claims, _transactionRepo, _crosswalk, NullLogger<CpcdsClaimImportService>.Instance);

    private Task<CpcdsImportResult> Import(string content, CpcdsImportOptions? options = null) =>
        Service().ImportAsync(Tenant, content, options ?? new CpcdsImportOptions { SourceSystem = "qnxt", FileName = "claims.csv" }, "importer");

    private IEnumerable<Claim> Current => _store.Where(c => c.SupersededAt is null);

    [Fact]
    public async Task First_load_creates_every_claim_with_a_deterministic_chain_id_and_provenance()
    {
        var result = await Import(CpcdsTestFiles.Sample());

        result.FileErrors.Should().BeEmpty();
        result.ClaimsInFile.Should().Be(4);
        result.Created.Should().Be(4);
        result.Rejected.Should().Be(0);

        var claim = _store.Single(c => c.ClaimNumber == "PRO-1001");
        claim.TenantId.Should().Be(Tenant);
        claim.ClaimVersionId.Should().Be(CpcdsClaimImportService.ChainId(Tenant, "PRO-1001")).And.StartWith("cpcds-");
        claim.Id.Should().Be(claim.ClaimVersionId + "-v1");
        claim.VersionNumber.Should().Be(1);
        claim.ExternalSource!.Format.Should().Be("cpcds");
        claim.ExternalSource.SourceSystem.Should().Be("qnxt");
        claim.ExternalSource.SourceClaimId.Should().Be("PRO-1001");
        claim.ExternalSource.ImportBatchId.Should().Be(result.BatchId);
        claim.ExternalSource.ContentHash.Should().HaveLength(64);

        _transactions.Should().HaveCount(4).And.OnlyContain(t => t.Format == "cpcds" && t.ImportBatchId == result.BatchId);
    }

    [Fact]
    public async Task Report_reconciles_money_in_the_file_against_money_loaded()
    {
        var result = await Import(CpcdsTestFiles.Sample());

        result.SubmittedAmountInFile.Should().Be(240.00m + 125.00m + 42000.00m + 310.00m);
        result.PaidAmountInFile.Should().Be(150.00m + 0m + 16500.00m + 208.00m);
        result.PaidAmountLoaded.Should().Be(result.PaidAmountInFile);
    }

    [Fact]
    public async Task Reloading_the_same_file_changes_nothing()
    {
        await Import(CpcdsTestFiles.Sample());
        var before = _store.Count;

        var result = await Import(CpcdsTestFiles.Sample());

        result.Unchanged.Should().Be(4);
        result.Created.Should().Be(0);
        result.Updated.Should().Be(0);
        _store.Should().HaveCount(before);
        result.PaidAmountLoaded.Should().Be(result.PaidAmountInFile);
    }

    [Fact]
    public async Task Row_order_does_not_count_as_a_change()
    {
        await Import(CpcdsTestFiles.SampleClaim("PRO-1001"));
        var lines = CpcdsTestFiles.SampleClaim("PRO-1001").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var reordered = string.Join('\n', lines[0], lines[2], lines[1]) + "\n";

        var result = await Import(reordered);

        result.Unchanged.Should().Be(1);
    }

    [Fact]
    public async Task A_changed_claim_gets_a_new_version_and_the_old_one_is_superseded()
    {
        await Import(CpcdsTestFiles.SampleClaim("PRO-1002"));
        var adjusted = CpcdsTestFiles.SampleClaim("PRO-1002")
            .Replace(",denied,50,", ",paid,,")
            .Replace("125.00,0.00,0.00,0.00,0.00,0.00,0.00,1", "125.00,100.00,80.00,0.00,20.00,0.00,20.00,1");

        var result = await Import(adjusted);

        result.Updated.Should().Be(1);
        var chain = _store.Where(c => c.ClaimNumber == "PRO-1002").OrderBy(c => c.VersionNumber).ToList();
        chain.Should().HaveCount(2);
        chain[0].SupersededByVersionId.Should().Be(chain[1].Id);
        chain[1].PredecessorVersionId.Should().Be(chain[0].Id);
        chain[1].ClaimVersionId.Should().Be(chain[0].ClaimVersionId, "the EOB id is the chain id and must not change");
        chain[1].Status.Should().Be(ClaimStatus.Paid);
        Current.Where(c => c.ClaimNumber == "PRO-1002").Should().ContainSingle().Which.VersionNumber.Should().Be(2);
    }

    [Fact]
    public async Task A_different_member_from_the_crosswalk_is_a_change()
    {
        await Import(CpcdsTestFiles.SampleClaim("PRO-1001"));
        var crosswalk = Substitute.For<ICoreMemberCrosswalk>();
        crosswalk.ResolveAsync(Tenant, "qnxt", "M100", Arg.Any<CancellationToken>()).Returns("CHO-100");
        _crosswalk = crosswalk;

        var result = await Import(CpcdsTestFiles.SampleClaim("PRO-1001"));

        result.Updated.Should().Be(1);
        Current.Single(c => c.ClaimNumber == "PRO-1001").MemberId.Should().Be("CHO-100");
        Current.Single(c => c.ClaimNumber == "PRO-1001").ExternalSource!.SourceMemberId.Should().Be("M100");
    }

    [Fact]
    public async Task A_member_the_crosswalk_does_not_know_is_refused()
    {
        var crosswalk = Substitute.For<ICoreMemberCrosswalk>();
        crosswalk.ResolveAsync(default!, default, default!, default).ReturnsForAnyArgs((string?)null);
        _crosswalk = crosswalk;

        var result = await Import(CpcdsTestFiles.SampleClaim("PRO-1001"));

        result.Rejected.Should().Be(1);
        result.Claims.Single().Errors.Should().ContainSingle().Which.Should().Contain("not in the member crosswalk");
        _store.Should().BeEmpty();
        _transactions.Single().Status.Should().Be("Rejected");
    }

    [Fact]
    public async Task A_claim_number_belonging_to_a_native_claim_is_refused()
    {
        _store.Add(new Claim { Id = "native-1", ClaimVersionId = "native-1", ClaimNumber = "PRO-1001", TenantId = Tenant, VersionNumber = 1 });

        var result = await Import(CpcdsTestFiles.SampleClaim("PRO-1001"));

        result.Rejected.Should().Be(1);
        result.Claims.Single().Errors.Single().Should().Contain("CHO processed itself");
        _store.Should().ContainSingle();
    }

    [Fact]
    public async Task A_claim_the_core_replaced_is_loaded_but_not_current()
    {
        var row = CpcdsTestFiles.ProfessionalRow("C-OLD");
        row["Claim adjusted to identifier"] = "C-NEW";
        var replacement = CpcdsTestFiles.ProfessionalRow("C-NEW");
        replacement["Claim adjusted from identifier"] = "C-OLD";
        var header = CpcdsTestFiles.ProfessionalHeader.Concat(["Claim adjusted to identifier", "Claim adjusted from identifier"]).ToList();

        var result = await Import(CpcdsTestFiles.Build(header, row, replacement));

        result.Created.Should().Be(2);
        var old = _store.Single(c => c.ClaimNumber == "C-OLD");
        old.SupersededAt.Should().NotBeNull();
        old.SupersededByVersionId.Should().Be(CpcdsClaimImportService.ChainId(Tenant, "C-NEW"));
        _store.Single(c => c.ClaimNumber == "C-NEW").OriginalClaimReference.Should().Be("C-OLD");
        Current.Select(c => c.ClaimNumber).Should().Equal("C-NEW");
    }

    [Fact]
    public async Task A_reload_heals_a_predecessor_left_current_by_an_interrupted_load()
    {
        await Import(CpcdsTestFiles.SampleClaim("PRO-1002"));
        var adjusted = CpcdsTestFiles.SampleClaim("PRO-1002").Replace(",denied,50,", ",denied,96,");
        await Import(adjusted);
        // Simulate the supersede write having failed.
        var v1 = _store.Single(c => c.ClaimNumber == "PRO-1002" && c.VersionNumber == 1);
        v1.SupersededAt = null;

        var result = await Import(adjusted);

        result.Unchanged.Should().Be(1);
        v1.SupersededAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Dry_run_reports_without_writing()
    {
        var result = await Import(CpcdsTestFiles.Sample(), new CpcdsImportOptions { DryRun = true });

        result.DryRun.Should().BeTrue();
        result.Created.Should().Be(4);
        _store.Should().BeEmpty();
        _transactions.Should().BeEmpty();
    }

    [Fact]
    public async Task One_bad_claim_does_not_stop_the_rest()
    {
        var bad = CpcdsTestFiles.ProfessionalRow("C-BAD");
        bad["Claim service start date"] = "31/31/2026";

        var result = await Import(CpcdsTestFiles.Build(CpcdsTestFiles.ProfessionalHeader, CpcdsTestFiles.ProfessionalRow("C-GOOD"), bad));

        result.Created.Should().Be(1);
        result.Rejected.Should().Be(1);
        result.Claims.Single(c => c.ClaimId == "C-BAD").Errors.Should().Contain(e => e.Contains("is not a date"));
        result.PaidAmountInFile.Should().Be(128.00m);
        result.PaidAmountLoaded.Should().Be(64.00m, "the gap is the rejected claim");
    }

    [Fact]
    public async Task A_file_missing_required_columns_loads_nothing()
    {
        var result = await Import("Claim unique identifier,Claim type\nC-1,oral\n");

        result.FileErrors.Should().NotBeEmpty();
        result.ClaimsInFile.Should().Be(0);
        _store.Should().BeEmpty();
    }

    [Fact]
    public void Chain_ids_differ_by_tenant()
    {
        CpcdsClaimImportService.ChainId("a", "C-1").Should().NotBe(CpcdsClaimImportService.ChainId("b", "C-1"));
        CpcdsClaimImportService.ChainId("a", "C-1").Should().Be(CpcdsClaimImportService.ChainId("a", "C-1"));
    }
}
