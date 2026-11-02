using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AppealsService.Models;
using AppealsService.Tests.Fakes;

namespace AppealsService.Tests.Integration;

/// <summary>
/// Intake on the shared line of business (docs/architecture/line-of-business.md): the old
/// Marketplace spelling is read as Exchange, the old MedicarePartD value is refused (it
/// would otherwise land on Part C clocks), and lines of business with no appeal rules are
/// refused.
/// </summary>
public class AppealLineOfBusinessIntakeTests : IClassFixture<AppealsWebApplicationFactory>
{
    private readonly AppealsWebApplicationFactory _factory;

    public AppealLineOfBusinessIntakeTests(AppealsWebApplicationFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private HttpClient NewClient()
    {
        var client = _factory.CreateDefaultClient(new CloudHealthOffice.Infrastructure.Security.ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-lob");
        return client;
    }

    private static StringContent Body(string lineOfBusiness, string? benefitType = null, string appealType = "Reconsideration")
    {
        var json = new JsonObject
        {
            ["claimId"] = "CLM-LOB-1",
            ["claimNumber"] = "CLM-0001",
            ["memberId"] = "M-LOB-1",
            ["patientName"] = "Jane Doe",
            ["providerNPI"] = "1234567890",
            ["appealReason"] = "Denied service was medically necessary.",
            ["appealType"] = appealType,
            ["appealLevel"] = "FirstLevel",
            ["lineOfBusiness"] = lineOfBusiness,
        };
        if (benefitType is not null) json["benefitType"] = benefitType;
        return new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
    }

    [Theory]
    [InlineData("Marketplace")]
    [InlineData("Exchange")]
    [InlineData("exchange")]
    public async Task Marketplace_and_Exchange_create_an_Exchange_appeal(string sent)
    {
        _factory.Reset();

        var response = await NewClient().PostAsync("/api/appeals", Body(sent));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var appeal = JsonSerializer.Deserialize<Appeal>(await response.Content.ReadAsStringAsync(), JsonOptions)!;
        appeal.LineOfBusiness.Should().Be(LineOfBusiness.Exchange);
        appeal.BenefitType.Should().Be(AppealBenefitType.MedicalOrService);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"lineOfBusiness\":\"Exchange\"");
    }

    [Fact]
    public async Task A_Part_D_case_is_Medicare_with_the_PartDDrug_benefit_type_and_runs_on_Part_D_clocks()
    {
        _factory.Reset();
        var before = DateTime.UtcNow;

        var response = await NewClient().PostAsync("/api/appeals", Body("Medicare", "PartDDrug"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var appeal = JsonSerializer.Deserialize<Appeal>(await response.Content.ReadAsStringAsync(), JsonOptions)!;
        appeal.Coverage.Should().Be(AppealCoverage.PartD);
        appeal.TargetResponseDate!.Value.ToUniversalTime().Should().BeCloseTo(before.AddDays(7), TimeSpan.FromMinutes(1)); // 42 CFR 423.590(a)
    }

    [Theory]
    [InlineData("MedicarePartD", null)]           // the old value: would silently become Part C
    [InlineData("TRICARE", null)]                 // no appeal rules modelled (32 CFR 199.10)
    [InlineData("VA", null)]
    [InlineData("Unknown", null)]
    [InlineData("Dental", null)]
    [InlineData("Commercial", "PartDDrug")]       // Part D is Medicare only
    public async Task Unsupported_lines_of_business_are_refused(string lineOfBusiness, string? benefitType)
    {
        _factory.Reset();

        var response = await NewClient().PostAsync("/api/appeals", Body(lineOfBusiness, benefitType));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Repo.SnapshotEvents().Should().BeEmpty("a refused create persists nothing");
    }
}
