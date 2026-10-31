using Bunit;
using CloudHealthOffice.Portal.Pages.Edi;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The Imports &amp; EDI console's daily drop: freshness and reconciliation,
/// imports by type, each run with its files as received, Load now, and setup
/// (drop settings and the hosted SFTP login).
/// </summary>
public class DataDropPanelsTests : TestContext
{
    private readonly Mock<IDataDropService> _drops = new();

    public DataDropPanelsTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_drops.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _drops.Setup(d => d.GetRunsAsync(It.IsAny<int>())).ReturnsAsync(new List<DataDropRunRecord>
        {
            new()
            {
                Id = "run-2", Status = "CompletedWithErrors", StartedAt = new DateTime(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc),
                Files =
                {
                    new() { Name = "CPCDS_Claims_20261102.csv", Kind = "Claims", Length = 4096, DocumentId = "doc-claims" },
                    new() { Name = "um_278_20261102.x12", Kind = "Authorizations", Length = 900 },
                },
                Steps =
                {
                    new() { Name = "archive", Status = "CompletedWithErrors", Counts = { ["archived"] = 1, ["notArchived"] = 1 },
                            Errors = { "um_278_20261102.x12: document-service answered 503" } },
                    new() { Name = "claims", Status = "CompletedWithErrors", Files = { "CPCDS_Claims_20261102.csv" },
                            Counts = { ["created"] = 40, ["updated"] = 2, ["rejected"] = 3 }, Errors = { "C-9: member not found" } },
                    new() { Name = "authorizations-278", Status = "Succeeded", Files = { "um_278_20261102.x12" },
                            Counts = { ["created"] = 5 } },
                },
            },
            new()
            {
                Id = "run-1", Status = "Succeeded", StartedAt = new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc),
                CompletedAt = new DateTime(2026, 11, 1, 6, 5, 0, DateTimeKind.Utc),
                Files = { new() { Name = "CPCDS_Members.csv", Kind = "Members" }, new() { Name = "CPCDS_Claims_20261101.csv", Kind = "Claims" } },
                Steps =
                {
                    new() { Name = "members-and-coverage", Status = "Succeeded", Files = { "CPCDS_Members.csv" },
                            Counts = { ["membersCreated"] = 100, ["dependentsCreated"] = 20, ["failedCount"] = 1 } },
                    new() { Name = "claims", Status = "Succeeded", Files = { "CPCDS_Claims_20261101.csv" },
                            Counts = { ["created"] = 60, ["unchanged"] = 7 } },
                },
            },
        });
        _drops.Setup(d => d.GetReportAsync(It.IsAny<int>())).ReturnsAsync(new DataDropFreshnessReport
        {
            Status = "Stale", SourceSystem = "qnxt", ClaimExtractAgeHours = 40.5, WindowDays = 30,
            Window = new() { Runs = 2, ClaimsRejected = 3, PaidAmountInFiles = 1000, PaidAmountLoaded = 900, PaidAmountNotLoaded = 100 },
            Issues = { "The newest claim extract loaded is 40.5 hours old." },
        });
    }

    [Fact]
    public void Freshness_shows_the_status_the_money_not_loaded_and_what_needs_attention()
    {
        var cut = RenderComponent<DataDropRunsPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-freshness]").TextContent.Should().Contain("Stale"));
        cut.Find("[data-testid=drop-freshness]").TextContent.Should().Contain("from qnxt").And.Contain("40.5 h old");
        cut.Find("[data-testid=drop-reconciliation]").TextContent.Should().Contain("3 refused").And.Contain("not loaded");
        cut.Find("[data-testid=drop-issues]").TextContent.Should().Contain("40.5 hours old");
    }

    [Fact]
    public void By_type_totals_each_import_in_load_order_without_the_archive_step()
    {
        var cut = RenderComponent<DataDropRunsPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-by-type]").TextContent.Should().Contain("Claims"));
        var rows = cut.FindAll("[data-testid=drop-by-type] tbody tr").Select(r => r.TextContent).ToList();
        rows.Should().HaveCount(3);
        rows[0].Should().StartWith("Members and coverage").And.Contain("120");
        rows[1].Should().StartWith("Claims").And.Contain("100");
        rows[2].Should().StartWith("Authorizations (X12 278)");
        rows.Should().NotContain(r => r.StartsWith("Archive"));
    }

    [Fact]
    public void A_run_opens_to_its_files_with_a_download_for_each_archived_file()
    {
        var cut = RenderComponent<DataDropRunsPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=drop-runs] a").Should().NotBeEmpty());

        cut.Find("[data-testid=drop-runs] tbody a").Click();

        var detail = cut.Find("[data-testid=drop-run-detail]");
        var download = detail.QuerySelector("[data-testid=drop-file-download]")!;
        download.GetAttribute("href").Should().Be("/documents/doc-claims/download?subjectKind=ImportRun&subjectId=run-2");
        detail.TextContent.Should().Contain("Not archived").And.Contain("C-9: member not found")
            .And.Contain("document-service answered 503");
    }

    [Fact]
    public void Load_now_is_only_offered_to_importers()
    {
        RenderComponent<DataDropRunsPanel>(p => p.Add(x => x.CanRun, false))
            .FindAll("[data-testid=drop-run-now]").Should().BeEmpty();
        RenderComponent<DataDropRunsPanel>(p => p.Add(x => x.CanRun, true))
            .FindAll("[data-testid=drop-run-now]").Should().ContainSingle();
    }

    [Fact]
    public void Load_now_runs_the_drop_and_refreshes()
    {
        _drops.Setup(d => d.RunNowAsync()).ReturnsAsync(DataDropChange<DataDropRunRecord>.Ok(
            new DataDropRunRecord { Id = "run-3", Status = "Succeeded" }));
        var cut = RenderComponent<DataDropRunsPanel>(p => p.Add(x => x.CanRun, true));
        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-run-now]").HasAttribute("disabled").Should().BeFalse());

        cut.Find("[data-testid=drop-run-now]").Click();

        cut.WaitForAssertion(() => _drops.Verify(d => d.GetRunsAsync(It.IsAny<int>()), Times.Exactly(2)));
        _drops.Verify(d => d.RunNowAsync(), Times.Once);
    }

    [Fact]
    public void Setup_shows_the_drop_state_and_the_services_reasons_for_refusing_settings()
    {
        _drops.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new DataDropSettingsView
        {
            TenantId = "plan-a", DropEnabledForDeployment = true, Enabled = false, Source = "None", InboundFolder = "plan-a/inbound",
        });
        _drops.Setup(d => d.SaveSettingsAsync(It.IsAny<DataDropSettings>()))
            .ReturnsAsync(DataDropChange<DataDropSettingsView>.Refused("sourceSystem: letters, digits, '.', '_' and '-', at most 64 characters."));

        var cut = RenderComponent<DataDropSetupPanel>(p => p.Add(x => x.CanEdit, true));
        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-settings-state]").TextContent.Should().Contain("Not set up yet"));
        cut.Find("[data-testid=drop-settings]").TextContent.Should().Contain("plan-a/inbound");

        cut.Find("[data-testid=drop-settings-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-settings-errors]").TextContent.Should().Contain("sourceSystem"));
    }

    [Fact]
    public void Setup_without_hosted_sftp_explains_the_plan_hosted_path()
    {
        _drops.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new DataDropSettingsView { Source = "None" });
        _drops.Setup(d => d.GetSftpAccountAsync()).ThrowsAsync(new ServiceUnavailableException("Tenant Service"));

        var cut = RenderComponent<DataDropSetupPanel>(p => p.Add(x => x.CanEdit, true).Add(x => x.CanManageSftp, true));

        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-sftp-unavailable]").TextContent
            .Should().Contain("not set up in this deployment").And.Contain("drop bucket directly"));
    }

    [Fact]
    public void Setup_lists_the_sftp_login_and_its_key_fingerprints()
    {
        _drops.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new DataDropSettingsView { Source = "Stored" });
        _drops.Setup(d => d.GetSftpAccountAsync()).ReturnsAsync(new DataDropSftpAccount
        {
            Exists = true, Enabled = true, Host = "sftp.example.test", Port = 22, Username = "plan-a",
            ReadOnlyFolders = { "/processed", "/failed" }, KeyFingerprints = { "SHA256:abc", "SHA256:def" },
        });

        var cut = RenderComponent<DataDropSetupPanel>(p => p.Add(x => x.CanManageSftp, true));

        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-sftp-keys]").TextContent.Should().Contain("SHA256:abc").And.Contain("SHA256:def"));
        cut.Find("[data-testid=drop-sftp]").TextContent.Should().Contain("sftp.example.test:22").And.Contain("Replace keys");
    }

    [Fact]
    public void Setup_hides_the_sftp_login_from_users_who_cannot_manage_settings()
    {
        _drops.Setup(d => d.GetSettingsAsync()).ReturnsAsync(new DataDropSettingsView { Source = "None" });

        var cut = RenderComponent<DataDropSetupPanel>(p => p.Add(x => x.CanEdit, true).Add(x => x.CanManageSftp, false));

        cut.WaitForAssertion(() => cut.Find("[data-testid=drop-sftp]").TextContent.Should().Contain("tenant administrator"));
        _drops.Verify(d => d.GetSftpAccountAsync(), Times.Never);
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData("Claim unique identifier = CLAIM_ID\n\nMember identifier=MBR_ID", 2, 0)]
    [InlineData("CLAIM_ID", 0, 1)]
    [InlineData("= CLAIM_ID", 0, 1)]
    public void Column_map_lines_are_column_equals_header(string? text, int entries, int errors)
    {
        var (map, problems) = DataDropSetupPanel.ParseColumnMap(text);

        (map?.Count ?? 0).Should().Be(entries);
        problems.Should().HaveCount(errors);
        if (entries == 2)
            map!["Member identifier"].Should().Be("MBR_ID");
    }
}
