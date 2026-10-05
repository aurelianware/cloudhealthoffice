using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The PA Rule Explorer reads TMPPM rules from terminology-service's API (with
/// the portal's HttpClient, which carries the user's CHO token), never from
/// terminology-service's database.
/// </summary>
public class TmppmRuleQueryServiceTests
{
    private readonly Mock<ILogger<TmppmRuleQueryService>> _logger = new();
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:TerminologyService"] = "http://terminology.test" })
        .Build();

    private TmppmRuleQueryService Create(FakeHandler handler) => new(new HttpClient(handler), _configuration, _logger.Object);

    private const string RulesJson = """
        [{"ruleId":"TX-1","category":"Home Health","tmppmRef":"§1","authRequired":true,"procedureCodes":["99500"],
          "codeSystem":"CPT","ageLimit":{"minAge":0,"maxAge":20,"unit":"years"},"state":"TX","sourceEdition":"2026-04"}]
        """;

    [Fact]
    public void HasNoDatabaseDependency()
    {
        typeof(TmppmRuleQueryService).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType.Namespace ?? string.Empty)
            .Should().NotContain(ns => ns.StartsWith("MongoDB"));
    }

    [Fact]
    public async Task SearchByCode_CallsTheTerminologyApi_AndMapsTheRules()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, RulesJson);

        var rules = await Create(handler).SearchByCodeAsync(" 99500 ", tenantId: "ignored", state: "TX");

        handler.CapturedUrls.Should().ContainSingle()
            .Which.Should().Be("http://terminology.test/api/v1/tmppm/rules?code=99500&state=TX");
        var rule = rules.Should().ContainSingle().Subject;
        rule.RuleId.Should().Be("TX-1");
        rule.ProcedureCodes.Should().Equal("99500");
        rule.AgeLimit!.MaxAge.Should().Be(20);
    }

    [Fact]
    public async Task GetRulesByCategory_EscapesTheCategory()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, RulesJson);

        await Create(handler).GetRulesByCategoryAsync("Home Health & Hospice");

        handler.CapturedUrls.Single().Should().Be("http://terminology.test/api/v1/tmppm/rules?category=Home%20Health%20%26%20Hospice");
    }

    [Fact]
    public async Task GetCategories_MapsGroups()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """[{"priority":"P1","categories":[{"category":"Home Health","tmppmRef":"§1","ruleCount":2,"codeCount":3}]}]""");

        var groups = await Create(handler).GetCategoriesAsync("TX");

        handler.CapturedUrls.Single().Should().EndWith("/api/v1/tmppm/categories?state=TX");
        groups.Single().Categories.Single().CodeCount.Should().Be(3);
    }

    [Fact]
    public async Task GetCurrentEdition_NotFound_IsNull()
    {
        (await Create(new FakeHandler(HttpStatusCode.NotFound)).GetCurrentEditionAsync()).Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentEdition_ReturnsTheEdition()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            """{"editionId":"2026-04","publicationDate":"2026-04-01","policyThroughDate":"2026-04-30","sourceUrl":"https://tmhp","ingestedAt":"2026-04-02T00:00:00Z","chapters":[]}""");

        var edition = await Create(handler).GetCurrentEditionAsync();

        edition!.EditionId.Should().Be("2026-04");
        edition.PublicationDate.Should().Be(new DateOnly(2026, 4, 1));
    }

    [Fact]
    public async Task GetDiff_NotFound_IsNull_AndQueryIsEscaped()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);

        (await Create(handler).GetDiffAsync("2026-03", "2026-04")).Should().BeNull();
        handler.CapturedUrls.Single().Should().EndWith("/api/v1/tmppm/diffs?from=2026-03&to=2026-04");
    }

    [Fact]
    public async Task Autocomplete_EmptyPrefix_MakesNoCall()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");

        (await Create(handler).AutocompleteCodeAsync("  ")).Should().BeEmpty();
        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceFailure_IsServiceUnavailable()
    {
        await FluentActions.Awaiting(() => Create(new FakeHandler(HttpStatusCode.Forbidden)).SearchByCodeAsync("99500"))
            .Should().ThrowAsync<ServiceUnavailableException>();
    }
}
