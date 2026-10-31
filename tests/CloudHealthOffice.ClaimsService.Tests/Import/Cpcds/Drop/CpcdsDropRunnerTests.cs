using System.Net;
using System.Text;
using ClaimsService.Import.Cpcds;
using ClaimsService.Import.Cpcds.Drop;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Shadow;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds.Drop;

public sealed class CpcdsDropRunnerTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private static readonly DateTime Now = new(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cpcds-drop-" + Guid.NewGuid().ToString("N"));
    private readonly ICpcdsClaimImportService _claims = Substitute.For<ICpcdsClaimImportService>();
    private readonly ICpcdsUpstreamImports _upstream = Substitute.For<ICpcdsUpstreamImports>();
    private readonly InMemoryCpcdsDropRunRepository _runs = new();
    private readonly ICpcdsImportBatchCompletedHandler _handler = Substitute.For<ICpcdsImportBatchCompletedHandler>();
    private readonly AdjudicationTenantContext _tenantContext = new();
    private readonly List<string> _calls = [];
    private readonly CpcdsDropOptions _options = new()
    {
        Enabled = true,
        QuietPeriod = TimeSpan.FromMinutes(2),
        Tenants = { [Tenant] = new CpcdsDropTenantOptions { SourceSystem = "qnxt" } },
    };

    public CpcdsDropRunnerTests()
    {
        _options.Root = _root;
        _upstream.ImportMembersAsync(Tenant, Arg.Any<CpcdsDropFile>(), Arg.Any<string>(), Arg.Any<CpcdsDropFile?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("members:" + ci.ArgAt<CpcdsDropFile>(1).Name + "+" + ci.ArgAt<CpcdsDropFile?>(3)?.Name);
                return new CpcdsDropStep { Name = "members-and-coverage", Status = CpcdsDropStepStatus.Succeeded };
            });
        _upstream.ImportClaimProvidersAsync(Tenant, Arg.Any<CpcdsDropFile>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("providers:" + ci.ArgAt<CpcdsDropFile>(1).Name);
                return new CpcdsDropStep { Name = "claim-providers", Status = CpcdsDropStepStatus.Succeeded };
            });
        _claims.ImportAsync(Tenant, Arg.Any<string>(), Arg.Any<CpcdsImportOptions>(), CpcdsDropRunner.Actor, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var options = ci.ArgAt<CpcdsImportOptions>(2);
                _calls.Add($"claims:{options.FileName}:{options.SourceSystem}:{_tenantContext.TenantId}");
                return new CpcdsImportResult { BatchId = "batch-" + options.FileName, ClaimsInFile = 3, Created = 3, PaidAmountInFile = 10m, PaidAmountLoaded = 10m };
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private CpcdsDropRunner Runner() => new(
        new FolderCpcdsDropStore(_root), _claims, _upstream, _runs, [_handler], _tenantContext,
        Options.Create(_options), Tenants(), NullLogger<CpcdsDropRunner>.Instance, new Clock(Now));

    private readonly InMemoryCpcdsDropTenantSettingsRepository _stored = new();

    private CpcdsDropTenants Tenants() => new(Options.Create(_options), _stored);

    private string Drop(string name, string content = "a,b\n1,2\n", TimeSpan? age = null, string tenant = Tenant)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, tenant, "inbound")).FullName;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Now - (age ?? TimeSpan.FromMinutes(10)));
        return path;
    }

    [Fact]
    public async Task Saved_settings_load_a_drop_and_replace_configuration()
    {
        await _stored.SaveAsync(new CpcdsDropTenantSettings
        {
            TenantId = Tenant, Enabled = true, Settings = new CpcdsDropTenantOptions { SourceSystem = "facets" },
        });
        Drop("CPCDS_Claims.csv");

        var run = await Runner().RunAsync(Tenant, default);

        run!.SourceSystem.Should().Be("facets");
        _calls.Should().Contain($"claims:CPCDS_Claims.csv:facets:{Tenant}");
    }

    [Fact]
    public async Task A_drop_the_tenant_turned_off_is_left_in_inbound()
    {
        await _stored.SaveAsync(new CpcdsDropTenantSettings
        {
            TenantId = Tenant, Enabled = false, Settings = new CpcdsDropTenantOptions { SourceSystem = "qnxt" },
        });
        var path = Drop("CPCDS_Claims.csv");

        (await Runner().RunAsync(Tenant, default)).Should().BeNull();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Members_and_coverage_load_first_then_each_claim_files_providers_then_its_claims()
    {
        Drop("CPCDS_Claims.csv");
        Drop("CPCDS_Coverages.csv");
        Drop("CPCDS_Members.csv");

        var run = await Runner().RunAsync(Tenant, default);

        run.Should().NotBeNull();
        _calls.Should().Equal(
            "members:CPCDS_Members.csv+CPCDS_Coverages.csv",
            "providers:CPCDS_Claims.csv",
            $"claims:CPCDS_Claims.csv:qnxt:{Tenant}");
        run!.Status.Should().Be(CpcdsDropRunStatus.Succeeded);
        run.Steps.Select(s => s.Name).Should().Equal("members-and-coverage", "claim-providers", "claims");
        run.Steps.Last().ImportBatchId.Should().Be("batch-CPCDS_Claims.csv");
        run.Steps.Last().Counts["created"].Should().Be(3);
        run.NewestFileWrittenAt.Should().Be(Now - TimeSpan.FromMinutes(10));

        Directory.EnumerateFiles(Path.Combine(_root, Tenant, "inbound")).Should().BeEmpty();
        Directory.EnumerateFiles(Path.Combine(_root, Tenant, "processed", run.Id)).Select(Path.GetFileName)
            .Should().BeEquivalentTo("CPCDS_Claims.csv", "CPCDS_Coverages.csv", "CPCDS_Members.csv");
        (await _runs.GetAsync(Tenant, run.Id)).Should().BeSameAs(run);
    }

    [Fact]
    public async Task Each_loaded_claim_file_is_handed_to_the_after_import_handlers_and_their_notes_kept()
    {
        Drop("claims_20261101.csv");
        _handler.OnBatchImportedAsync(Arg.Any<CpcdsImportBatchCompleted>(), Arg.Any<CancellationToken>()).Returns("shadow run s-1");

        var run = await Runner().RunAsync(Tenant, default);

        await _handler.Received(1).OnBatchImportedAsync(
            Arg.Is<CpcdsImportBatchCompleted>(b => b.TenantId == Tenant && b.DropRunId == run!.Id
                && b.ImportBatchId == "batch-claims_20261101.csv" && b.SourceSystem == "qnxt" && b.Created == 3),
            Arg.Any<CancellationToken>());
        run!.Notes.Should().Contain("shadow run s-1");
    }

    [Fact]
    public async Task A_handler_that_throws_is_noted_and_does_not_fail_the_run()
    {
        Drop("claims.csv");
        _handler.OnBatchImportedAsync(Arg.Any<CpcdsImportBatchCompleted>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var run = await Runner().RunAsync(Tenant, default);

        run!.Status.Should().Be(CpcdsDropRunStatus.Succeeded);
        run.Notes.Should().ContainSingle(n => n.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task Files_still_being_written_and_partial_uploads_are_left_alone()
    {
        Drop("claims.csv", age: TimeSpan.FromSeconds(30));
        Drop("members.csv.part");
        Drop(".claims.csv.swp");

        var run = await Runner().RunAsync(Tenant, default);

        run.Should().BeNull();
        Directory.EnumerateFiles(Path.Combine(_root, Tenant, "inbound")).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_tenant_without_a_configured_drop_is_not_loaded()
    {
        Drop("claims.csv", tenant: "tenant-b");

        (await Runner().RunAsync("tenant-b", default)).Should().BeNull();
        File.Exists(Path.Combine(_root, "tenant-b", "inbound", "claims.csv")).Should().BeTrue();
    }

    [Fact]
    public async Task Coverage_without_its_member_file_and_unrecognized_files_wait_with_a_note()
    {
        Drop("coverages.csv");
        Drop("readme.txt");
        Drop("claims.csv");

        var run = await Runner().RunAsync(Tenant, default);

        run!.Files.Select(f => f.Name).Should().Equal("claims.csv");
        run.Notes.Should().Contain(n => n.StartsWith("coverages.csv left in inbound"))
            .And.Contain(n => n.StartsWith("readme.txt left in inbound"));
        Directory.EnumerateFiles(Path.Combine(_root, Tenant, "inbound")).Select(Path.GetFileName)
            .Should().BeEquivalentTo("coverages.csv", "readme.txt");
    }

    [Fact]
    public async Task A_claim_file_that_loads_nothing_fails_its_step_and_the_files_go_to_failed()
    {
        Drop("members.csv");
        Drop("claims.csv");
        _claims.ImportAsync(Tenant, Arg.Any<string>(), Arg.Any<CpcdsImportOptions>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CpcdsImportResult { BatchId = "b", FileErrors = ["Missing required columns: \"Member id\"."] });

        var run = await Runner().RunAsync(Tenant, default);

        run!.Status.Should().Be(CpcdsDropRunStatus.CompletedWithErrors);
        run.Steps.Single(s => s.Name == "claims").Status.Should().Be(CpcdsDropStepStatus.Failed);
        run.ArchivedTo.Should().EndWith(Path.Combine("failed", run.Id));
        Directory.Exists(Path.Combine(_root, Tenant, "failed", run.Id)).Should().BeTrue();
        await _handler.DidNotReceiveWithAnyArgs().OnBatchImportedAsync(default!, default);
    }

    [Fact]
    public async Task Rejected_claims_complete_the_run_with_errors_and_name_the_claims()
    {
        Drop("claims.csv");
        _claims.ImportAsync(Tenant, Arg.Any<string>(), Arg.Any<CpcdsImportOptions>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CpcdsImportResult
            {
                BatchId = "b", ClaimsInFile = 2, Created = 1, Rejected = 1,
                Claims = [new CpcdsClaimResult { ClaimId = "C-9", Outcome = CpcdsClaimOutcome.Rejected, Errors = { "No payment amount." } }],
            });

        var run = await Runner().RunAsync(Tenant, default);

        run!.Status.Should().Be(CpcdsDropRunStatus.CompletedWithErrors);
        run.Steps.Single(s => s.Name == "claims").Errors.Should().Equal("C-9: No payment amount.");
        run.ArchivedTo.Should().EndWith(Path.Combine("processed", run.Id));
    }

    [Fact]
    public void A_file_another_run_already_took_is_not_loaded_twice()
    {
        var path = Drop("claims.csv");
        var store = new FolderCpcdsDropStore(_root);
        var ready = store.ListReady(Tenant, TimeSpan.Zero, Now).Single();

        store.Claim(Tenant, "run-1", ready).Should().NotBeNull();
        store.Claim(Tenant, "run-2", ready).Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task A_run_that_stops_moves_its_files_to_failed_instead_of_leaving_them_in_processing()
    {
        Drop("claims.csv");
        _upstream.ImportClaimProvidersAsync(Tenant, Arg.Any<CpcdsDropFile>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new IOException("disk"));

        var run = await Runner().RunAsync(Tenant, default);

        run!.Status.Should().Be(CpcdsDropRunStatus.Failed);
        Directory.Exists(Path.Combine(_root, Tenant, "processing", run.Id)).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, Tenant, "failed", run.Id)).Should().BeTrue();
    }

    [Theory]
    [InlineData("CPCDS_Members.csv", CpcdsDropFileKind.Members)]
    [InlineData("CPCDS_Coverages.csv", CpcdsDropFileKind.Coverages)]
    [InlineData("CPCDS_Claims.csv", CpcdsDropFileKind.Claims)]
    [InlineData("qnxt_member_coverage_20261101.txt", CpcdsDropFileKind.Coverages)]
    [InlineData("facets-claims-2026-11-01.psv", CpcdsDropFileKind.Claims)]
    [InlineData("readme.txt", CpcdsDropFileKind.Unknown)]
    public void File_kinds_come_from_the_name(string name, CpcdsDropFileKind kind)
        => CpcdsDropFileKinds.Classify(name).Should().Be(kind);

    [Fact]
    public void A_tenant_id_that_is_not_a_safe_folder_name_is_refused()
    {
        var store = new FolderCpcdsDropStore(_root);
        var act = () => store.ListReady("../other", TimeSpan.Zero, Now);
        act.Should().Throw<ArgumentException>();
    }

    // ── shadow adjudication after import ────────────────────────────────

    private static CpcdsImportBatchCompleted Batch() => new(Tenant, "run-1", "batch-1", "qnxt", "claims.csv", 2, 0, 1, 0);

    [Fact]
    public async Task Shadow_adjudication_runs_on_the_batch_when_the_tenant_asks_and_shadow_is_enabled()
    {
        _options.Tenants[Tenant].ShadowAfterImport = true;
        var shadow = Substitute.For<IShadowAdjudicationService>();
        shadow.RunAsync(Tenant, Arg.Any<ShadowAdjudicationRunRequest>(), "cpcds-drop", Arg.Any<CancellationToken>())
            .Returns(new ShadowAdjudicationRunResponse { Run = new ShadowAdjudicationRun { Id = "s-1", ClaimsRequested = 3, ClaimsCompared = 3 }, Results = [] });
        var handler = new ShadowAdjudicateAfterImport(shadow, Tenants(),
            Options.Create(new ShadowAdjudicationOptions { Enabled = true }));

        var note = await handler.OnBatchImportedAsync(Batch(), default);

        note.Should().Contain("s-1").And.Contain("3 of 3 claims, 3 compared").And.NotContain("Partial");
        await shadow.Received(1).RunAsync(Tenant, Arg.Is<ShadowAdjudicationRunRequest>(r => r.ImportBatchId == "batch-1"),
            "cpcds-drop", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_shadow_run_that_covers_part_of_a_large_batch_says_so()
    {
        _options.Tenants[Tenant].ShadowAfterImport = true;
        var shadow = Substitute.For<IShadowAdjudicationService>();
        shadow.RunAsync(Tenant, Arg.Any<ShadowAdjudicationRunRequest>(), "cpcds-drop", Arg.Any<CancellationToken>())
            .Returns(new ShadowAdjudicationRunResponse { Run = new ShadowAdjudicationRun { Id = "s-2", ClaimsRequested = 200, ClaimsCompared = 198 }, Results = [] });
        var handler = new ShadowAdjudicateAfterImport(shadow, Tenants(),
            Options.Create(new ShadowAdjudicationOptions { Enabled = true, MaxClaimsPerRun = 200 }));

        var note = await handler.OnBatchImportedAsync(Batch() with { Created = 450, Unchanged = 50 }, default);

        note.Should().Contain("s-2 for claims.csv: 200 of 500 claims, 198 compared").And.Contain("Partial").And.Contain("200");
    }

    [Fact]
    public async Task Shadow_adjudication_is_skipped_with_a_note_when_shadow_is_off_and_silently_when_not_asked()
    {
        var shadow = Substitute.For<IShadowAdjudicationService>();
        var off = new ShadowAdjudicationOptions { Enabled = false };

        (await new ShadowAdjudicateAfterImport(shadow, Tenants(), Options.Create(off))
            .OnBatchImportedAsync(Batch(), default)).Should().BeNull();

        _options.Tenants[Tenant].ShadowAfterImport = true;
        (await new ShadowAdjudicateAfterImport(shadow, Tenants(), Options.Create(off))
            .OnBatchImportedAsync(Batch(), default)).Should().Contain("not enabled");
        await shadow.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, default);
    }

    // ── upstream imports ────────────────────────────────────────────────

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static (HttpCpcdsUpstreamImports Imports, StubHandler Handler) Upstream(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler) { BaseAddress = new Uri("http://svc/") });
        return (new HttpCpcdsUpstreamImports(factory), handler);
    }

    private static CpcdsDropFile File_(string name) => new(name, "/x/" + name, 10, Now);

    [Fact]
    public async Task The_member_import_report_becomes_counts_and_errors_and_names_the_tenant()
    {
        var (imports, handler) = Upstream(HttpStatusCode.OK, """
            { "memberRows": 4, "coverageRows": 4, "membersInFile": 4, "rowErrors": ["M9: no name"],
              "planCodes": { "unmapped": [ { "externalPlanCode": "X" } ] },
              "import": { "membersCreated": 3, "coverageRecordsCreated": 2, "errors": [] } }
            """);

        var step = await imports.ImportMembersAsync(Tenant, File_("m.csv"), "a", File_("c.csv"), "b", default);

        step.Status.Should().Be(CpcdsDropStepStatus.CompletedWithErrors);
        step.Counts.Should().Contain(new KeyValuePair<string, decimal>("membersCreated", 3))
            .And.Contain(new KeyValuePair<string, decimal>("unmappedPlanCodes", 1));
        step.Errors.Should().Equal("M9: no name");
        step.Files.Should().Equal("m.csv", "c.csv");
        handler.Request!.RequestUri!.AbsolutePath.Should().Be("/api/v1/enrollment/import/cpcds");
        handler.Request.Headers.GetValues("X-Tenant-ID").Should().Equal(Tenant);
    }

    [Fact]
    public async Task A_refused_provider_import_is_a_failed_step()
    {
        var (imports, _) = Upstream(HttpStatusCode.UnprocessableEntity, """{ "fileErrors": ["No provider NPI column found"] }""");

        var step = await imports.ImportClaimProvidersAsync(Tenant, File_("claims.csv"), "a", null, default);

        step.Status.Should().Be(CpcdsDropStepStatus.Failed);
        step.HttpStatus.Should().Be(422);
        step.Errors.Should().Equal("No provider NPI column found");
    }
}
