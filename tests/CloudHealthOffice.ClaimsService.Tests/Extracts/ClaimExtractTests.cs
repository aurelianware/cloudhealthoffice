using System.Security.Cryptography;
using System.Text.Json;
using ClaimsService.Controllers;
using ClaimsService.Extracts;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Extracts;

public sealed class ClaimExtractTests : IDisposable
{
    private const string Tenant = "plan-a";
    private static readonly DateTime Now = new(2026, 11, 2, 7, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "claim-extract-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryClaimExtractStore _store = new();
    private readonly InMemorySource _source = new();
    private readonly ClaimExtractOptions _options = new() { PageSize = 2, SettleDelay = TimeSpan.FromMinutes(5) };
    private readonly Clock _clock = new(Now);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task First_run_delivers_every_version_but_drafts_then_the_manifest()
    {
        await Enable();
        var original = Claim("c1", "v1", Now.AddDays(-3), ClaimVersionState.Adjusted, superseded: Now.AddDays(-2));
        var corrected = Claim("c2", "v1", Now.AddDays(-2), ClaimVersionState.Paid, version: 2);
        _source.Claims.AddRange([original, corrected, Claim("c3", "v3", Now.AddDays(-1), ClaimVersionState.Draft)]);

        var outcome = await Runner().RunAsync(Tenant, "admin-1", full: false, default);

        var run = outcome.Run!;
        run.Status.Should().Be(ClaimExtractRunStatus.Succeeded);
        run.Full.Should().BeTrue("there was no earlier delivery");
        run.ClaimVersions.Should().Be(2);
        run.Lines.Should().Be(4);
        run.DataUseAgreement.Should().Be("DUA-2026-014");

        var folder = Path.Combine(_root, Tenant, "outbound", "extracts", "claims", run.Id);
        var claims = File.ReadAllLines(Path.Combine(folder, "claims.csv"));
        claims[0].Should().StartWith("ClaimId,ClaimVersionId,VersionNumber,VersionState,PredecessorVersionId,SupersededAt,");
        claims.Skip(1).Select(l => l.Split(',')[0]).Should().Equal("c1", "c2");
        File.ReadAllLines(Path.Combine(folder, "claim-lines.csv")).Should().HaveCount(5);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var files = manifest.RootElement.GetProperty("files");
        files.GetArrayLength().Should().Be(2);
        var claimsFile = files[0];
        claimsFile.GetProperty("rows").GetInt64().Should().Be(2);
        claimsFile.GetProperty("sha256").GetString().Should().Be(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, "claims.csv")))).ToLowerInvariant());
        manifest.RootElement.GetProperty("dataUseAgreement").GetString().Should().Be("DUA-2026-014");

        var settings = await _store.GetSettingsAsync(Tenant);
        settings!.Watermark.Should().Be(Now - _options.SettleDelay);
        settings.LeaseOwner.Should().BeNull();
        (await _store.ListRunsAsync(Tenant, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task Next_run_delivers_only_what_changed_after_the_watermark()
    {
        await Enable();
        _source.Claims.Add(Claim("c1", "v1", Now.AddDays(-3), ClaimVersionState.Paid));
        await Runner().RunAsync(Tenant, "schedule", full: false, default);
        var watermark = (await _store.GetSettingsAsync(Tenant))!.Watermark!.Value;

        _source.Claims.Add(Claim("c2", "v2", watermark, ClaimVersionState.Paid)); // already covered by the first window
        _source.Claims.Add(Claim("c3", "v3", watermark.AddMinutes(1), ClaimVersionState.Denied));
        _clock.Now = Now.AddDays(1);

        var run = (await Runner().RunAsync(Tenant, "schedule", full: false, default)).Run!;

        run.Full.Should().BeFalse();
        run.WindowFrom.Should().Be(watermark);
        Ids(run).Should().Equal("c3");
    }

    [Fact]
    public async Task Paging_through_equal_update_times_neither_skips_nor_repeats()
    {
        await Enable();
        var at = Now.AddHours(-3);
        foreach (var id in new[] { "e", "a", "d", "b", "c" })
            _source.Claims.Add(Claim(id, id, at, ClaimVersionState.Paid));

        var run = (await Runner().RunAsync(Tenant, "schedule", full: false, default)).Run!;

        Ids(run).Should().Equal("a", "b", "c", "d", "e");
    }

    [Fact]
    public async Task Full_run_delivers_everything_again()
    {
        await Enable();
        _source.Claims.Add(Claim("c1", "v1", Now.AddDays(-3), ClaimVersionState.Paid));
        await Runner().RunAsync(Tenant, "schedule", full: false, default);
        _clock.Now = Now.AddHours(1);

        var run = (await Runner().RunAsync(Tenant, "admin-1", full: true, default)).Run!;

        run.Full.Should().BeTrue();
        run.WindowFrom.Should().BeNull();
        Ids(run).Should().Equal("c1");
    }

    [Fact]
    public async Task Nothing_runs_until_the_tenant_turns_it_on_with_an_agreement()
    {
        (await Runner().RunAsync(Tenant, "schedule", false, default)).Refusal.Should().Be(ClaimExtractRefusal.NotEnabled);
        await _store.SaveSettingsAsync(Tenant, true, null, "admin-1", Now);
        (await Runner().RunAsync(Tenant, "schedule", false, default)).Refusal.Should().Be(ClaimExtractRefusal.NotEnabled);
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task A_tenant_already_running_is_not_run_twice()
    {
        await Enable();
        (await _store.TryLeaseAsync(Tenant, "other", Now, Now.AddHours(1))).Should().NotBeNull();

        (await Runner().RunAsync(Tenant, "schedule", false, default)).Refusal.Should().Be(ClaimExtractRefusal.AlreadyRunning);
    }

    [Fact]
    public async Task A_failed_delivery_keeps_the_watermark_so_the_next_run_sends_it_again()
    {
        await Enable();
        _source.Claims.Add(Claim("c1", "v1", Now.AddDays(-3), ClaimVersionState.Paid));
        var failing = Substitute.For<IExtractDestination>();
        failing.WriteAsync(default!, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs<Task<string>>(_ => throw new IOException("bucket unavailable"));

        var failed = (await Runner(failing).RunAsync(Tenant, "schedule", false, default)).Run!;

        failed.Status.Should().Be(ClaimExtractRunStatus.Failed);
        failed.Error.Should().Contain("bucket unavailable");
        var settings = await _store.GetSettingsAsync(Tenant);
        settings!.Watermark.Should().BeNull();
        settings.LeaseOwner.Should().BeNull();

        var retried = (await Runner().RunAsync(Tenant, "schedule", false, default)).Run!;
        Ids(retried).Should().Equal("c1");
    }

    [Fact]
    public async Task The_manifest_is_written_after_the_data_files()
    {
        await Enable();
        var written = new List<string>();
        var recording = Substitute.For<IExtractDestination>();
        recording.WriteAsync(default!, default!, default!, default!, default!, default).ReturnsForAnyArgs(call =>
        {
            written.Add(call.ArgAt<string>(3));
            return Task.FromResult("mem://" + call.ArgAt<string>(3));
        });

        await Runner(recording).RunAsync(Tenant, "schedule", false, default);

        written.Should().Equal("claims.csv", "claim-lines.csv", "manifest.json");
    }

    [Fact]
    public void Fields_are_quoted_as_RFC_4180_and_not_altered()
    {
        ClaimExtractRows.Record(["a,b", "say \"hi\"", "=SUM(A1)", "-5", ""])
            .Should().Be("\"a,b\",\"say \"\"hi\"\"\",=SUM(A1),-5,");
    }

    [Fact]
    public void Header_and_line_rows_match_their_columns()
    {
        var claim = Claim("c1", "v1", Now, ClaimVersionState.Paid);
        ClaimExtractRows.Header(claim).Should().HaveCount(ClaimExtractColumns.Headers.Count);
        ClaimExtractRows.Lines(claim).Should().AllSatisfy(l => l.Should().HaveCount(ClaimExtractColumns.Lines.Count));
    }

    [Fact]
    public async Task Folder_destination_refuses_a_path_outside_the_tenant_folder()
    {
        var local = Path.Combine(_root, "x.csv");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(local, "a");
        var destination = new FolderExtractDestination(_root);

        var act = () => destination.WriteAsync("../other", "claims", "run", "claims.csv", local, default);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Worker_runs_each_enabled_tenant_once_a_day_after_the_run_time()
    {
        await Enable();
        _source.Claims.Add(Claim("c1", "v1", Now.AddDays(-3), ClaimVersionState.Paid));
        var services = new ServiceCollection()
            .AddSingleton<IClaimExtractStore>(_store)
            .AddScoped(_ => Runner())
            .BuildServiceProvider();
        var options = new ClaimExtractOptions { Enabled = true, RunAtUtc = TimeSpan.FromHours(8) };
        var worker = new ClaimExtractWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLogger<ClaimExtractWorker>.Instance, _clock);

        await worker.RunDueAsync(default); // 07:00, before 08:00
        (await _store.ListRunsAsync(Tenant, 10)).Should().BeEmpty();

        _clock.Now = Now.AddHours(2);
        await worker.RunDueAsync(default);
        _clock.Now = Now.AddHours(5);
        await worker.RunDueAsync(default);

        (await _store.ListRunsAsync(Tenant, 10)).Should().ContainSingle();
    }

    [Fact]
    public async Task Turning_it_on_needs_a_data_use_agreement()
    {
        var controller = Controller();

        var refused = await controller.Put(new ClaimExtractSettingsRequest { Enabled = true, DataUseAgreement = " " }, default);
        refused.Result.Should().BeOfType<BadRequestObjectResult>();

        var saved = await controller.Put(new ClaimExtractSettingsRequest { Enabled = true, DataUseAgreement = "DUA-7" }, default);
        var view = (ClaimExtractSettingsView)((OkObjectResult)saved.Result!).Value!;
        view.Enabled.Should().BeTrue();
        view.DataUseAgreement.Should().Be("DUA-7");
        view.Folder.Should().Be("/outbound/extracts/claims/");
        (await _store.GetSettingsAsync(Tenant))!.UpdatedBy.Should().Be("admin-1");
    }

    [Fact]
    public async Task Starting_a_run_while_off_is_refused()
    {
        (await Controller().Start(full: false)).Should().BeOfType<ConflictObjectResult>();
    }

    private ClaimExtractsController Controller()
    {
        var actor = Substitute.For<ICurrentActor>();
        actor.UserId.Returns("admin-1");
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new ClaimExtractsController(_store, Substitute.For<IServiceScopeFactory>(), Substitute.For<IHostApplicationLifetime>(),
            actor, NullLogger<ClaimExtractsController>.Instance, _clock)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private Task Enable() => _store.SaveSettingsAsync(Tenant, true, "DUA-2026-014", "admin-1", Now.AddDays(-10));

    private ClaimExtractRunner Runner(IExtractDestination? destination = null) => new(
        _store, _source, destination ?? new FolderExtractDestination(_root), Options.Create(_options),
        NullLogger<ClaimExtractRunner>.Instance, _clock);

    private IEnumerable<string> Ids(ClaimExtractRun run) =>
        File.ReadAllLines(Path.Combine(_root, Tenant, "outbound", "extracts", "claims", run.Id, "claims.csv"))
            .Skip(1).Select(l => l.Split(',')[0]);

    private static Claim Claim(string id, string versionId, DateTime updated, ClaimVersionState state, int version = 1, DateTime? superseded = null) => new()
    {
        Id = id,
        TenantId = Tenant,
        ClaimVersionId = versionId,
        VersionNumber = version,
        VersionState = state,
        SupersededAt = superseded,
        ClaimNumber = "CLM-" + id,
        MemberId = "M-1, Jr", // a comma, to exercise quoting
        BillingProviderNPI = "1234567893",
        LastUpdatedDate = updated,
        ServiceDateFrom = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes = [new DiagnosisCode { Code = "E119", PointerNumber = 1 }],
        AdjudicationResult = new AdjudicationResult { AllowedAmount = 80m, PayerPayment = 64m, PatientResponsibility = 16m },
        ClaimLines =
        [
            new ClaimLine { LineNumber = 1, ProcedureCode = "99213", Modifiers = ["25"], ChargeAmount = 100m },
            new ClaimLine { LineNumber = 2, ProcedureCode = "36415", ChargeAmount = 10m },
        ],
    };

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    /// <summary>Answers as <see cref="ClaimExtractSourceMongo"/> does.</summary>
    private sealed class InMemorySource : IClaimExtractSource
    {
        public List<Claim> Claims { get; } = [];

        public Task<IReadOnlyList<Claim>> ReadAsync(
            string tenantId, DateTime? from, DateTime to, (DateTime LastUpdated, string Id)? after, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Claim>>(Claims
                .Where(c => c.TenantId == tenantId && c.VersionState != ClaimVersionState.Draft)
                .Where(c => c.LastUpdatedDate <= to && (from is null || c.LastUpdatedDate > from))
                .Where(c => after is null || c.LastUpdatedDate > after.Value.LastUpdated
                            || (c.LastUpdatedDate == after.Value.LastUpdated && string.CompareOrdinal(c.Id, after.Value.Id) > 0))
                .OrderBy(c => c.LastUpdatedDate).ThenBy(c => c.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToList());
    }
}
