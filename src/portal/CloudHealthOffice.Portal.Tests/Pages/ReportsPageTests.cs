using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The Reports page (portal-reporting.md, module standard reports): lists the
/// claims reports a reports:claims user may run, takes each report's
/// parameters, renders any report's rows from its column list, and downloads
/// the same run as CSV.
/// </summary>
public class ReportsPageTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ReportsPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "sup-1", Roles = ["ClaimsSupervisor"] });
        _user.Setup(u => u.HasPermission("reports:claims")).Returns(true);
        _claims.Setup(c => c.GetClaimReportCatalogAsync()).ReturnsAsync(Catalog());
    }

    private static List<ReportDefinitionView> Catalog() =>
    [
        new()
        {
            Id = "claims-pended-inventory", Name = "Pended claims inventory", Description = "Every claim pended now.",
            Parameters = [new() { Name = "lineOfBusiness" }, new() { Name = "pendCode" }],
            Columns = ["ClaimId", "ClaimNumber", "PendCode", "ReceivedAt", "AgeDays", "BillingProviderNpi", "BilledAmount"],
        },
        new()
        {
            Id = "claims-processing-timeliness", Name = "Claims processing timeliness", Description = "Per receipt month.",
            Parameters = [new() { Name = "from" }, new() { Name = "to" }, new() { Name = "lineOfBusiness" }],
            Columns = ["ReceiptMonth", "Received", "PercentWithin30", "Meets30DayStandard"],
            Regulation = "42 CFR 447.45(d)",
        },
    ];

    private static Dictionary<string, JsonElement> Row(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private IRenderedComponent<Reports> Open(string? reportId = null)
    {
        Services.GetRequiredService<FakeNavigationManager>().NavigateTo(reportId is null ? "/reports" : "/reports?report=" + reportId);
        return RenderComponent<Reports>();
    }

    [Fact]
    public void Without_reports_permission_the_page_says_so_and_asks_the_service_nothing()
    {
        _user.Setup(u => u.HasPermission("reports:claims")).Returns(false);

        var cut = Open();

        cut.WaitForAssertion(() => cut.Find("[data-testid=reports-none]").TextContent.Should().Contain("reports:claims"));
        _claims.Verify(c => c.GetClaimReportCatalogAsync(), Times.Never);
    }

    [Fact]
    public void Lists_the_catalog_and_shows_the_chosen_reports_parameters()
    {
        var cut = Open("claims-processing-timeliness");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=reports-catalog-item]").Should().HaveCount(2));
        cut.Find("[data-testid=report-form]").TextContent.Should().Contain("Claims processing timeliness");
        cut.Find("[data-testid=report-regulation]").TextContent.Should().Contain("447.45");
        cut.Find("[data-testid=report-form]").TextContent.Should().Contain("From").And.Contain("Line of business").And.NotContain("Pend code");
    }

    [Fact]
    public void Running_a_report_renders_its_columns_rows_and_truncation()
    {
        _claims.Setup(c => c.RunClaimReportAsync("claims-pended-inventory", It.IsAny<IReadOnlyDictionary<string, string?>>()))
            .ReturnsAsync(new ReportRunView
            {
                ReportId = "claims-pended-inventory", AsOf = DateTime.UtcNow, RunBy = "sup-1", TotalCount = 1500, Truncated = true,
                Columns = Catalog()[0].Columns,
                Items =
                [
                    Row("""{"claimId":"c-1","claimNumber":"CN-1","pendCode":"COB","receivedAt":"2026-09-01T10:00:00Z","ageDays":61,"billingProviderNpi":"1234567890","billedAmount":125.5}"""),
                ],
                Notes = ["A note."],
            });

        var cut = Open("claims-pended-inventory");
        cut.WaitForAssertion(() => cut.Find("[data-testid=report-run]"));
        cut.Find("[data-testid=report-run]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=report-row]").Should().HaveCount(1));
        var headers = cut.FindAll("[data-testid=report-table] th").Select(th => th.TextContent).ToList();
        headers.Should().Contain(["Claim number", "Billing provider NPI", "Age days", "Billed amount"]);
        var row = cut.Find("[data-testid=report-row]");
        row.TextContent.Should().Contain("COB").And.Contain("2026-09-01").And.Contain("$125.50");
        row.QuerySelector("a")!.GetAttribute("href").Should().Be("/claims/c-1");
        cut.Find("[data-testid=report-meta]").TextContent.Should().Contain("Showing 1 of 1,500");
        cut.Find("[data-testid=report-truncated]");
        cut.Find("[data-testid=report-note]").TextContent.Should().Be("A note.");
    }

    [Fact]
    public void A_refused_run_shows_the_reason()
    {
        _claims.Setup(c => c.RunClaimReportAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string?>>()))
            .ThrowsAsync(new ReportForbiddenException("claims-pended-inventory"));

        var cut = Open("claims-pended-inventory");
        cut.WaitForAssertion(() => cut.Find("[data-testid=report-run]"));
        cut.Find("[data-testid=report-run]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=report-run-error]").TextContent.Should().Contain("permission"));
    }

    [Fact]
    public void Csv_downloads_the_file_the_service_produced()
    {
        _claims.Setup(c => c.DownloadClaimReportCsvAsync("claims-pended-inventory", It.IsAny<IReadOnlyDictionary<string, string?>>()))
            .ReturnsAsync(new ReportFile("claims-pended-inventory-20261101T1200Z.csv", "ClaimId\n"u8.ToArray()));
        JSInterop.SetupVoid("downloadBase64File", _ => true);

        var cut = Open("claims-pended-inventory");
        cut.WaitForAssertion(() => cut.Find("[data-testid=report-csv]"));
        cut.Find("[data-testid=report-csv]").Click();

        cut.WaitForAssertion(() => JSInterop.VerifyInvoke("downloadBase64File"));
        var call = JSInterop.VerifyInvoke("downloadBase64File");
        call.Arguments[0].Should().Be("claims-pended-inventory-20261101T1200Z.csv");
        call.Arguments[1].Should().Be("text/csv");
    }

    [Theory]
    [InlineData("ClaimNumber", "Claim number")]
    [InlineData("BillingProviderNpi", "Billing provider NPI")]
    [InlineData("Within30Days", "Within 30 days")]
    [InlineData("Meets90DayStandard", "Meets 90 day standard")]
    public void Column_headers_read_as_words(string column, string header) =>
        ReportRunView.Header(column).Should().Be(header);

    [Fact]
    public void Cells_format_percentages_flags_and_blanks()
    {
        var row = Row("""{"percentWithin30":0.9123,"meets30DayStandard":false,"meets90DayStandard":null}""");

        ReportRunView.Cell(row, "PercentWithin30").Should().Be("91.2%");
        ReportRunView.Cell(row, "Meets30DayStandard").Should().Be("No");
        ReportRunView.Cell(row, "Meets90DayStandard").Should().BeEmpty();
        ReportRunView.Cell(row, "Missing").Should().BeEmpty();
    }
}
