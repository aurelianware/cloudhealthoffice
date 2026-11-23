using System.Net;
using System.Text;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>The Shadow Adjudication quick start's loads: claims, members and coverage, authorizations.</summary>
public class ShadowQuickStartClientTests
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:ClaimsService"] = "http://claims/api",
            ["Services:AuthorizationService"] = "http://auth/api/",
            ["Services:EnrollmentImportService"] = "http://enroll/api",
        })
        .Build();

    private readonly List<string> _urls = new();

    private ShadowQuickStartClient Client(HttpStatusCode status, string body) => new(
        new HttpClient(new FakeHandler(request =>
        {
            _urls.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        })),
        _configuration, NullLogger<ShadowQuickStartClient>.Instance);

    private static ShadowQuickStartFile File(string name, string text) => new(name, new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Loaded_claims_carry_their_import_batch_and_a_summary()
    {
        var client = Client(HttpStatusCode.OK, """
            { "batchId": "batch-9", "claimsInFile": 12, "created": 10, "updated": 0, "unchanged": 1, "rejected": 1,
              "paidAmountLoaded": 4520.4, "fileErrors": [],
              "claims": [ { "claimId": "C-7", "errors": ["member not found"] }, { "claimId": "C-1", "errors": [] } ] }
            """);

        var step = await client.ImportClaimsAsync(File("claims.csv", "x"), "Medicaid", "qnxt");

        _urls.Should().Equal("http://claims/api/v1/claims/import/cpcds");
        step.Succeeded.Should().BeTrue();
        step.ImportBatchId.Should().Be("batch-9");
        step.Summary.Should().Be("12 claims: 11 loaded ($4,520 core paid), 1 refused");
        step.Problems.Should().Equal("C-7: member not found");
    }

    [Fact]
    public async Task A_claim_file_with_nothing_loaded_has_no_batch_to_run()
    {
        var client = Client(HttpStatusCode.UnprocessableEntity, """
            { "batchId": "batch-0", "claimsInFile": 0, "rejected": 0, "fileErrors": ["No header row with a claim identifier."] }
            """);

        var step = await client.ImportClaimsAsync(File("claims.csv", "x"), "Medicaid", null);

        step.Succeeded.Should().BeFalse();
        step.ImportBatchId.Should().BeNull();
        step.Problems.Should().Equal("No header row with a claim identifier.");
    }

    [Fact]
    public async Task An_X12_278_file_goes_to_the_278_importer()
    {
        var client = Client(HttpStatusCode.OK, """{ "authorizationsInFile": 2, "created": 2 }""");

        var step = await client.ImportAuthorizationsAsync(File("auths.txt", "﻿  ISA*00*          *00*"), "Medicaid", null);

        _urls.Should().Equal("http://auth/api/v1/authorizations/import/x12-278");
        step.Summary.Should().StartWith("2 authorizations: 2 created");
    }

    [Fact]
    public async Task Members_without_permission_say_so()
    {
        var client = Client(HttpStatusCode.Forbidden, "");

        var step = await client.ImportMembersAsync(File("members.csv", "x"), null);

        _urls.Should().Equal("http://enroll/api/v1/enrollment/import/cpcds");
        step.Succeeded.Should().BeFalse();
        step.Summary.Should().Contain("permission");
    }
}
