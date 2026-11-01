using System.Net;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.GrouperEngine.Licensed;
using CloudHealthOffice.GrouperEngine.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The licensed APR-DRG / EAPG grouper seam: the default answers that
/// nothing is configured, and the HTTP client believes a plan's grouper only
/// after checking its answer.
/// </summary>
public class LicensedGrouperTests
{
    private static AprDrgGroupingRequest Stay(string? version = "40") => new()
    {
        ClaimReference = "clm-1",
        PrincipalDiagnosis = new GroupingDiagnosis("J189", "Y"),
        SecondaryDiagnoses = [new GroupingDiagnosis("N179", "N")],
        DischargeDate = new DateOnly(2026, 10, 15),
        AgeYears = 67,
        Sex = "F",
        DischargeStatus = "01",
        RequestedVersion = version,
        BilledDrg = "139",
    };

    private static EapgGroupingRequest Visit(params int[] lines) => new()
    {
        ClaimReference = "clm-2",
        PrincipalDiagnosis = "S52501A",
        TypeOfBill = "0131",
        FromDate = new DateOnly(2026, 10, 1),
        ThroughDate = new DateOnly(2026, 10, 1),
        Lines = lines.Select(n => new EapgLineInput
        {
            LineNumber = n,
            RevenueCode = "0450",
            ProcedureCode = "99284",
            ServiceDate = new DateOnly(2026, 10, 1),
        }).ToList(),
    };

    private static (HttpLicensedGrouper Grouper, StubHandler Handler) Http(
        object? body, HttpStatusCode status = HttpStatusCode.OK, string? apiKey = "secret")
    {
        var handler = new StubHandler(status, body is null ? "" : JsonSerializer.Serialize(body));
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://grouper.plan.example/") };
        var grouper = new HttpLicensedGrouper(client, new LicensedGrouperOptions { ApiKey = apiKey });
        return (grouper, handler);
    }

    // ─────────────────────────────────────────────────────────
    //  Not configured
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_grouper_every_answer_is_not_configured_and_never_the_billed_group()
    {
        var grouper = new NotConfiguredLicensedGrouper();

        var stay = await grouper.GroupAsync(Stay());
        var visit = await grouper.GroupAsync(Visit(1));

        Assert.Equal(LicensedGroupingStatus.NotConfigured, stay.Status);
        Assert.Null(stay.AprDrg);
        Assert.Equal("139", stay.BilledDrg);
        Assert.Equal(LicensedGroupingStatus.NotConfigured, visit.Status);
        Assert.Empty(visit.Lines);
    }

    [Fact]
    public void An_empty_base_url_registers_the_not_configured_grouper()
    {
        var provider = Register(new Dictionary<string, string?>());

        Assert.IsType<NotConfiguredLicensedGrouper>(provider.GetRequiredService<IAprDrgGrouper>());
        Assert.IsType<NotConfiguredLicensedGrouper>(provider.GetRequiredService<IEapgGrouper>());
    }

    [Fact]
    public void A_base_url_registers_the_http_grouper()
    {
        var provider = Register(new Dictionary<string, string?>
        {
            ["LicensedGrouper:BaseUrl"] = "https://grouper.plan.example/v1",
        });

        Assert.IsType<HttpLicensedGrouper>(provider.GetRequiredService<IAprDrgGrouper>());
        Assert.IsType<HttpLicensedGrouper>(provider.GetRequiredService<IEapgGrouper>());
    }

    [Theory]
    [InlineData("http://grouper.plan.example")]
    [InlineData("not a url")]
    [InlineData("ftp://grouper.plan.example")]
    public void An_unusable_base_url_stops_the_host(string url)
    {
        Assert.Throws<InvalidOperationException>(() => Register(new Dictionary<string, string?>
        {
            ["LicensedGrouper:BaseUrl"] = url,
        }));
    }

    [Fact]
    public void Plain_http_is_allowed_to_loopback_and_when_explicitly_allowed()
    {
        Assert.Equal("http://localhost:8080/",
            LicensedGrouperRegistration.ValidateBaseUrl(new LicensedGrouperOptions { BaseUrl = "http://localhost:8080" }).ToString());
        Assert.Equal("http://grouper.internal/x/",
            LicensedGrouperRegistration.ValidateBaseUrl(new LicensedGrouperOptions
            {
                BaseUrl = "http://grouper.internal/x", AllowInsecureHttp = true,
            }).ToString());
    }

    private static ServiceProvider Register(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLicensedGroupers(configuration).BuildServiceProvider();
    }

    // ─────────────────────────────────────────────────────────
    //  APR-DRG over HTTP
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_good_apr_drg_answer_is_grouped_with_the_drg_soi_key()
    {
        var (grouper, handler) = Http(new
        {
            grouped = true, returnCode = "0", aprDrg = "139", soi = 3, rom = 2, mdc = 4,
            version = "40.0", grouperName = "3M APR-DRG",
        });

        var result = await grouper.GroupAsync(Stay());

        Assert.Equal(LicensedGroupingStatus.Grouped, result.Status);
        Assert.Equal("139-3", result.DrgSoiKey);
        Assert.Equal(2, result.RiskOfMortality);
        Assert.Equal("40.0", result.GrouperVersion);

        Assert.Equal("/apr-drg/v1/group", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("secret", handler.LastRequest.Headers.GetValues("X-Api-Key").Single());
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("J189", sent.RootElement.GetProperty("principalDiagnosis").GetProperty("code").GetString());
        Assert.Equal("2026-10-15", sent.RootElement.GetProperty("dischargeDate").GetString());
        Assert.Equal("40", sent.RootElement.GetProperty("requestedVersion").GetString());
    }

    [Fact]
    public async Task A_grouper_refusal_keeps_its_return_code_and_messages()
    {
        var (grouper, _) = Http(new
        {
            grouped = false, returnCode = "3", version = "40.0", messages = new[] { "Invalid principal diagnosis" },
        });

        var result = await grouper.GroupAsync(Stay());

        Assert.Equal(LicensedGroupingStatus.RejectedByGrouper, result.Status);
        Assert.Equal("3", result.GrouperReturnCode);
        Assert.Contains("Invalid principal diagnosis", result.Notes);
        Assert.Null(result.AprDrg);
    }

    [Theory]
    [InlineData("13", 3)]
    [InlineData("1393", 3)]
    [InlineData("139", 0)]
    [InlineData("139", 5)]
    public async Task A_malformed_group_or_severity_is_not_believed(string drg, int soi)
    {
        var (grouper, _) = Http(new { grouped = true, aprDrg = drg, soi, version = "40" });

        var result = await grouper.GroupAsync(Stay());

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, result.Status);
        Assert.Null(result.AprDrg);
    }

    [Fact]
    public async Task The_wrong_version_is_not_believed_because_weights_are_per_version()
    {
        var (grouper, _) = Http(new { grouped = true, aprDrg = "139", soi = 2, version = "41.0" });

        var result = await grouper.GroupAsync(Stay(version: "40"));

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, result.Status);
    }

    [Theory]
    [InlineData(null, "39", true)]
    [InlineData("40", "40", true)]
    [InlineData("40", "40.0", true)]
    [InlineData("40", "400", false)]
    [InlineData("40", "41", false)]
    [InlineData("40", null, false)]
    public void Version_matching_is_by_release_prefix(string? requested, string? used, bool matches) =>
        Assert.Equal(matches, HttpLicensedGrouper.VersionMatches(requested, used));

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_failed_call_is_unavailable(HttpStatusCode status)
    {
        var (grouper, _) = Http(new { }, status);

        var result = await grouper.GroupAsync(Stay());

        Assert.Equal(LicensedGroupingStatus.Unavailable, result.Status);
        Assert.Contains(((int)status).ToString(), result.Notes[0]);
    }

    [Fact]
    public async Task Unreadable_json_is_an_invalid_response()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{not json");
        var grouper = new HttpLicensedGrouper(
            new HttpClient(handler) { BaseAddress = new Uri("https://g.example/") }, new LicensedGrouperOptions());

        var result = await grouper.GroupAsync(Stay());

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task An_unreachable_grouper_is_unavailable_and_a_timeout_too()
    {
        var unreachable = new HttpLicensedGrouper(
            new HttpClient(new ThrowingHandler(new HttpRequestException("refused"))) { BaseAddress = new Uri("https://g.example/") },
            new LicensedGrouperOptions());
        var slow = new HttpLicensedGrouper(
            new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout"))) { BaseAddress = new Uri("https://g.example/") },
            new LicensedGrouperOptions());

        Assert.Equal(LicensedGroupingStatus.Unavailable, (await unreachable.GroupAsync(Stay())).Status);
        Assert.Equal(LicensedGroupingStatus.Unavailable, (await slow.GroupAsync(Stay())).Status);
    }

    [Fact]
    public async Task The_callers_cancellation_propagates()
    {
        var (grouper, _) = Http(new { grouped = true });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grouper.GroupAsync(Stay(), cts.Token));
    }

    // ─────────────────────────────────────────────────────────
    //  EAPG over HTTP
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_good_eapg_answer_covers_every_line_and_zeroes_packaged_factors()
    {
        var (grouper, handler) = Http(new
        {
            grouped = true, version = "3.18",
            lines = new object[]
            {
                new { lineNumber = 1, eapg = "00096", eapgType = "Significant procedure", action = "payable" },
                new { lineNumber = 2, eapg = "00097", action = "discounted", discountFactor = 0.5m },
                new { lineNumber = 3, eapg = "00390", action = "packaged", discountFactor = 1.0m },
            },
        });

        var result = await grouper.GroupAsync(Visit(1, 2, 3));

        Assert.Equal(LicensedGroupingStatus.Grouped, result.Status);
        Assert.Equal([1m, 0.5m, 0m], result.Lines.Select(l => l.DiscountFactor));
        Assert.Equal(EapgLineAction.Packaged, result.Lines[2].Action);
        Assert.Equal("/eapg/v1/group", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_missing_line_is_not_believed()
    {
        var (grouper, _) = Http(new
        {
            grouped = true,
            lines = new object[] { new { lineNumber = 1, eapg = "00096", action = "payable" } },
        });

        var result = await grouper.GroupAsync(Visit(1, 2));

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, result.Status);
        Assert.Contains("2", result.Notes[0]);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task An_extra_or_repeated_line_is_not_believed()
    {
        var (extra, _) = Http(new
        {
            grouped = true,
            lines = new object[]
            {
                new { lineNumber = 1, eapg = "00096", action = "payable" },
                new { lineNumber = 9, eapg = "00096", action = "payable" },
            },
        });
        var (repeated, _) = Http(new
        {
            grouped = true,
            lines = new object[]
            {
                new { lineNumber = 1, eapg = "00096", action = "payable" },
                new { lineNumber = 1, eapg = "00097", action = "payable" },
            },
        });

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, (await extra.GroupAsync(Visit(1))).Status);
        Assert.Equal(LicensedGroupingStatus.InvalidResponse, (await repeated.GroupAsync(Visit(1))).Status);
    }

    [Theory]
    [InlineData("payable", 0.5)]
    [InlineData("discounted", 1.0)]
    [InlineData("discounted", 0.0)]
    public async Task A_factor_that_contradicts_the_action_is_not_believed(string action, double factor)
    {
        var (grouper, _) = Http(new
        {
            grouped = true,
            lines = new object[] { new { lineNumber = 1, eapg = "00096", action, discountFactor = (decimal)factor } },
        });

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, (await grouper.GroupAsync(Visit(1))).Status);
    }

    [Fact]
    public async Task A_payable_line_without_an_eapg_is_not_believed()
    {
        var (grouper, _) = Http(new
        {
            grouped = true,
            lines = new object[] { new { lineNumber = 1, action = "payable" } },
        });

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, (await grouper.GroupAsync(Visit(1))).Status);
    }

    [Fact]
    public async Task An_unknown_action_is_not_believed()
    {
        var (grouper, _) = Http(new
        {
            grouped = true,
            lines = new object[] { new { lineNumber = 1, eapg = "00096", action = "bundledish" } },
        });

        Assert.Equal(LicensedGroupingStatus.InvalidResponse, (await grouper.GroupAsync(Visit(1))).Status);
    }

    // ─────────────────────────────────────────────────────────
    //  Stubs
    // ─────────────────────────────────────────────────────────

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }
}
