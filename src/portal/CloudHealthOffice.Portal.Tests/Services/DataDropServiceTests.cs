using System.Net;
using System.Text;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The Imports &amp; EDI console's client for the daily file drop
/// (claims-service) and the hosted SFTP login (tenant-service).
/// </summary>
public class DataDropServiceTests
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:ClaimsService"] = "http://claims/api",
            ["Services:TenantService"] = "http://tenants/api",
        })
        .Build();

    private readonly Mock<ITenantContextService> _tenant = new();
    private readonly List<string> _bodies = new();

    public DataDropServiceTests() => _tenant.Setup(t => t.GetTenantIdAsync()).ReturnsAsync("plan-a");

    private DataDropService Service(FakeHandler handler)
        => new(new HttpClient(handler), _configuration, _tenant.Object, NullLogger<DataDropService>.Instance);

    private FakeHandler Answer(HttpStatusCode status, string? body = null) => new(request =>
    {
        if (request.Content is not null)
            _bodies.Add(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var response = new HttpResponseMessage(status);
        if (body is not null)
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return response;
    });

    [Fact]
    public async Task Runs_are_read_with_their_files_steps_and_archived_document_ids()
    {
        var handler = Answer(HttpStatusCode.OK, """
            [ { "id": "run-1", "status": "CompletedWithErrors", "startedAt": "2026-11-01T06:00:00Z",
                "files": [ { "name": "CPCDS_Claims.csv", "kind": "Claims", "length": 2048, "documentId": "doc-1" } ],
                "steps": [ { "name": "claims", "status": "CompletedWithErrors", "counts": { "created": 10, "rejected": 2 },
                             "errors": ["C-9: member not found"] } ] } ]
            """);

        var runs = await Service(handler).GetRunsAsync(500);

        handler.CapturedUrls.Should().Equal("http://claims/api/v1/claims/import/cpcds/drops?limit=200");
        var run = runs.Should().ContainSingle().Subject;
        run.Status.Should().Be("CompletedWithErrors");
        run.Files.Single().DocumentId.Should().Be("doc-1");
        run.Steps.Single().Counts["rejected"].Should().Be(2);
    }

    [Fact]
    public async Task The_report_comes_from_the_drops_report_endpoint()
    {
        var handler = Answer(HttpStatusCode.OK, """
            { "status": "Stale", "claimExtractAgeHours": 40.5, "windowDays": 30,
              "window": { "claimsRejected": 3, "paidAmountInFiles": 100.5, "paidAmountLoaded": 90 },
              "issues": ["The newest claim extract loaded is 40.5 hours old"] }
            """);

        var report = await Service(handler).GetReportAsync(30);

        handler.CapturedUrls.Should().Equal("http://claims/api/v1/claims/import/cpcds/drops/report?days=30");
        report!.Status.Should().Be("Stale");
        report.Window.ClaimsRejected.Should().Be(3);
        report.Issues.Should().ContainSingle();
    }

    [Fact]
    public async Task Saving_settings_puts_them_and_returns_the_new_view()
    {
        var handler = Answer(HttpStatusCode.OK, """{ "tenantId": "plan-a", "enabled": true, "loading": true, "source": "Stored" }""");

        var result = await Service(handler).SaveSettingsAsync(new DataDropSettings
        {
            Enabled = true, SourceSystem = "qnxt", LineOfBusiness = "Medicaid",
            ColumnMap = new() { ["Claim unique identifier"] = "CLAIM_ID" },
        });

        result.Succeeded.Should().BeTrue();
        result.Value!.Source.Should().Be("Stored");
        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls.Single().Should().Be("http://claims/api/v1/claims/import/cpcds/drops/settings");
        _bodies.Single().Should().Contain("\"lineOfBusiness\":\"Medicaid\"").And.Contain("\"sourceSystem\":\"qnxt\"")
            .And.Contain("\"Claim unique identifier\":\"CLAIM_ID\"");
    }

    [Fact]
    public async Task Refused_settings_come_back_as_the_services_own_errors()
    {
        var handler = Answer(HttpStatusCode.BadRequest,
            """{ "error": "invalid_settings", "errors": ["expectedEveryHours: between 1 and 744."] }""");

        var result = await Service(handler).SaveSettingsAsync(new DataDropSettings { ExpectedEveryHours = 0 });

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Equal("expectedEveryHours: between 1 and 744.");
    }

    [Fact]
    public async Task Load_now_with_nothing_waiting_is_neither_a_run_nor_an_error()
    {
        var handler = Answer(HttpStatusCode.NoContent);

        var result = await Service(handler).RunNowAsync();

        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls.Single().Should().Be("http://claims/api/v1/claims/import/cpcds/drops/run");
        result.Value.Should().BeNull();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_now_without_drop_settings_says_so()
    {
        var handler = Answer(HttpStatusCode.NotFound,
            """{ "error": "No CPCDS drop is configured for this tenant; save drop settings first." }""");

        var result = await Service(handler).RunNowAsync();

        result.Errors.Should().ContainSingle().Which.Should().Contain("save drop settings first");
    }

    [Fact]
    public async Task The_sftp_login_is_the_current_tenants()
    {
        var handler = Answer(HttpStatusCode.OK, """
            { "exists": true, "enabled": true, "host": "sftp.example.test", "port": 22, "username": "plan-a",
              "keyFingerprints": ["SHA256:abc"] }
            """);

        var account = await Service(handler).GetSftpAccountAsync();

        handler.CapturedUrls.Should().Equal("http://tenants/api/v1/tenants/plan-a/cpcds-sftp");
        account!.Username.Should().Be("plan-a");
        account.KeyFingerprints.Should().Equal("SHA256:abc");
    }

    [Fact]
    public async Task Keys_are_sent_as_public_key_lines_and_a_bad_key_comes_back_with_its_reason()
    {
        var handler = Answer(HttpStatusCode.BadRequest,
            """{ "error": "invalid_keys", "errors": ["publicKeys[0]: not an OpenSSH public key."] }""");

        var result = await Service(handler).SetSftpKeysAsync(["ssh-rsa nope"]);

        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Put);
        _bodies.Single().Should().Be("""{"publicKeys":["ssh-rsa nope"]}""");
        result.Errors.Should().Equal("publicKeys[0]: not an OpenSSH public key.");
    }

    [Fact]
    public async Task Turning_the_login_off_deletes_it()
    {
        var handler = Answer(HttpStatusCode.OK, """{ "exists": true, "enabled": false }""");

        var result = await Service(handler).DisableSftpAsync();

        handler.CapturedRequests.Single().Method.Should().Be(HttpMethod.Delete);
        result.Value!.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_tenant_there_is_no_sftp_call()
    {
        _tenant.Setup(t => t.GetTenantIdAsync()).ReturnsAsync((string?)null);
        var handler = Answer(HttpStatusCode.OK, "{}");

        (await Service(handler).GetSftpAccountAsync()).Should().BeNull();
        (await Service(handler).SetSftpKeysAsync(["k"])).Errors.Should().ContainSingle();
        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_service_that_cannot_answer_is_unavailable()
    {
        var sut = Service(Answer(HttpStatusCode.ServiceUnavailable, """{ "error": "sftp_unavailable" }"""));

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSftpAccountAsync());
        ex.ServiceName.Should().Be("Tenant Service");
    }

    [Fact]
    public async Task A_report_the_user_may_not_see_is_null_not_an_error()
    {
        var report = await Service(Answer(HttpStatusCode.Forbidden)).GetReportAsync();

        report.Should().BeNull();
    }
}
