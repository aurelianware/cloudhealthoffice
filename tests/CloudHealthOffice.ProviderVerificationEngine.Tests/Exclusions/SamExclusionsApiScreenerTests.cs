using System.Net;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Xunit;
using static CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions.ExclusionTestData;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

public class SamExclusionsApiScreenerTests
{
    private const string ApiKey = "test-secret-key-123";
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private const string NpiHitJson = """
        {
          "totalRecords": 1,
          "excludedEntity": [
            {
              "exclusionDetails": {
                "classificationType": "Individual",
                "exclusionType": "Prohibition/Restriction",
                "exclusionProgram": "Reciprocal",
                "excludingAgencyCode": "HHS",
                "excludingAgencyName": "Department of Health and Human Services"
              },
              "exclusionIdentification": {
                "ueiSAM": null, "cageCode": null, "npi": 1234567893,
                "firstName": "JOHN", "middleName": null, "lastName": "DOE", "entityName": null
              },
              "exclusionActions": {
                "listOfActions": [
                  { "activateDate": "2021-01-15", "terminationDate": "Indefinite", "terminationType": "Indefinite", "recordStatus": "Active" }
                ]
              }
            }
          ]
        }
        """;

    private const string EmptyJson = """{ "totalRecords": 0, "excludedEntity": [] }""";

    private readonly List<TimeSpan> _delays = [];
    private readonly ListLogger<SamExclusionsApiScreener> _logger = new();

    private SamExclusionsApiScreener Screener(StubHandler handler, Action<SamOptions>? configure = null)
    {
        var options = Options(o =>
        {
            o.Sam.Enabled = true;
            o.Sam.Mode = SamScreeningMode.Api;
            o.Sam.ApiKey = ApiKey;
            o.Sam.RequestTimeout = TimeSpan.FromMilliseconds(200);
            configure?.Invoke(o.Sam);
        });
        return new SamExclusionsApiScreener(new StubHttpClientFactory(handler), Wrap(options), _logger,
            new FixedTimeProvider(Now), (d, _) => { _delays.Add(d); return Task.CompletedTask; });
    }

    [Fact]
    public async Task ActiveNpiHit_FromCannedResponse_IsExcluded()
    {
        var handler = new StubHandler((req, _) =>
            req.RequestUri!.Query.Contains("npi=1234567893") ? StubHandler.Json(NpiHitJson) : StubHandler.Json(EmptyJson));

        var outcome = await Screener(handler).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893", FirstName = "John", LastName = "Doe" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.Equal("LiveApi", outcome.Status.Mode);
        Assert.True(outcome.IsExcluded);
        var match = outcome.Matches.First();
        Assert.Equal(ExclusionScreeningSource.SamGov, match.Source);
        Assert.Equal("1234567893", match.Npi);
        Assert.Equal("Prohibition/Restriction", match.ExclusionType);
        Assert.Equal(new DateTimeOffset(2021, 1, 15, 0, 0, 0, TimeSpan.Zero), match.ExclusionDate);
        Assert.Null(match.ReinstatementDate);

        // Base URL + version are configurable and the key goes as api_key.
        Assert.All(handler.Requests, u =>
        {
            Assert.StartsWith("https://api.sam.gov/entity-information/v4/exclusions?", u.ToString());
            Assert.Contains($"api_key={ApiKey}", u.Query);
        });
        Assert.Contains(handler.Requests, u => u.Query.Contains("exclusionName=John%20Doe"));
    }

    [Fact]
    public async Task NameOnlyHit_IsPossibleMatch_NotExcluded()
    {
        var nameHit = NpiHitJson.Replace("\"npi\": 1234567893", "\"npi\": null");
        var handler = new StubHandler((req, _) =>
            req.RequestUri!.Query.Contains("exclusionName") ? StubHandler.Json(nameHit) : StubHandler.Json(EmptyJson));

        var outcome = await Screener(handler).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1497758544", FirstName = "John", LastName = "Doe" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.False(outcome.IsExcluded);
        Assert.True(Assert.Single(outcome.Matches).MatchConfidence >= 0.7f);
    }

    [Fact]
    public async Task InactiveRecord_IsNotAMatch()
    {
        var inactive = NpiHitJson.Replace("\"recordStatus\": \"Active\"", "\"recordStatus\": \"Inactive\"");
        var handler = new StubHandler((_, _) => StubHandler.Json(inactive));

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.False(outcome.IsExcluded);
        Assert.Empty(outcome.Matches);
    }

    [Fact]
    public async Task ServerError_AfterRetries_IsNotScreened()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable));

        var outcome = await Screener(handler, s => s.MaxRetries = 2).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.False(outcome.IsExcluded);
        Assert.Contains("503", outcome.Status.Note);
        Assert.Equal(3, handler.Requests.Count);       // 1 + 2 retries
        Assert.Equal(2, _delays.Count);
        Assert.True(_delays[1] > _delays[0]);          // exponential backoff
    }

    [Fact]
    public async Task Timeout_IsNotScreened()
    {
        var handler = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return StubHandler.Json(EmptyJson);
        });

        var outcome = await Screener(handler, s => s.MaxRetries = 1).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("timed out", outcome.Status.Note);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RateLimited_RetriesHonouringRetryAfter_ThenScreens()
    {
        var handler = new StubHandler((_, n) =>
        {
            if (n == 1)
            {
                var r = StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            }
            return StubHandler.Json(EmptyJson);
        });

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.Empty(outcome.Matches);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], _delays);
    }

    [Fact]
    public async Task RejectedKey_IsNotScreened_WarnsWithoutLoggingKey_AndDoesNotRetry()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.Forbidden));

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("rejected", outcome.Status.Note);
        Assert.Single(handler.Requests);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("90 days"));
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains(ApiKey));
        Assert.DoesNotContain(ApiKey, outcome.Status.Note);
    }

    [Fact]
    public async Task MissingApiKey_IsNotScreened_WithoutCallingApi()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(EmptyJson));

        var outcome = await Screener(handler, s => s.ApiKey = null).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MoreResultsThanPageLimit_IsNotScreened()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(NpiHitJson.Replace("\"totalRecords\": 1", "\"totalRecords\": 500")));

        var outcome = await Screener(handler, s => { s.PageSize = 1; s.MaxPages = 2; }).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1497758544", LastName = "Doe", FirstName = "John" }, default);

        Assert.False(outcome.Status.WasScreened);
    }

    [Fact]
    public async Task UnexpectedBody_IsNotScreened()
    {
        var handler = new StubHandler((_, _) => StubHandler.Text("<html>maintenance</html>"));

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
    }

    [Fact]
    public async Task DefaultContract_UsesNpiExclusionNameAndPageSize()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(EmptyJson));

        await Screener(handler).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893", OrganizationName = "Acme & Sons" }, default);

        Assert.Contains(handler.Requests, u => u.Query.EndsWith("&npi=1234567893&page=0&size=10"));
        Assert.Contains(handler.Requests, u => u.Query.Contains("&exclusionName=Acme%20%26%20Sons&page=0&size=10"));
    }

    [Fact]
    public async Task ConfigurableContract_StartLengthAndNameParts()
    {
        var page2 = NpiHitJson.Replace("\"totalRecords\": 1", "\"totalRecords\": 2").Replace("\"JOHN\"", "\"JAKE\"");
        var handler = new StubHandler((req, _) =>
            StubHandler.Json(req.RequestUri!.Query.Contains("start=1")
                ? page2
                : NpiHitJson.Replace("\"totalRecords\": 1", "\"totalRecords\": 2")));

        var outcome = await Screener(handler, s =>
        {
            s.PaginationStyle = SamPaginationStyle.StartLength;
            s.PageParameter = "start";
            s.SizeParameter = "length";
            s.NameSearchStyle = SamNameSearchStyle.NameParts;
            s.PageSize = 1;
        }).ScreenAsync(new ProviderScreeningRequest { FirstName = "John", LastName = "Doe", OrganizationName = "Acme" }, default);

        Assert.True(outcome.Status.WasScreened, outcome.Status.Note);
        Assert.Contains(handler.Requests, u => u.Query.Contains("firstName=John&lastName=Doe&start=0&length=1"));
        Assert.Contains(handler.Requests, u => u.Query.Contains("firstName=John&lastName=Doe&start=1&length=1"));
        Assert.Contains(handler.Requests, u => u.Query.Contains("entityName=Acme&start=0&length=1"));
        Assert.DoesNotContain(handler.Requests, u => u.Query.Contains("exclusionName"));
    }

    [Fact]
    public async Task EmptyPageBeforeReportedTotal_IsNotScreened()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("""{ "totalRecords": 3, "excludedEntity": [] }"""));

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("empty page", outcome.Status.Note);
    }

    [Fact]
    public async Task UnmappableEntity_IsNotScreened()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{ "totalRecords": 1, "excludedEntity": [ { "someNewShape": { "npi": "1234567893" } } ] }"""));

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("unrecognized", outcome.Status.Note);
    }

    [Fact]
    public async Task PaginationIgnoredByServer_RepeatedPage_IsNotScreened()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(NpiHitJson.Replace("\"totalRecords\": 1", "\"totalRecords\": 3")));

        var outcome = await Screener(handler, s => s.PageSize = 1).ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("repeated", outcome.Status.Note);
    }

    [Fact]
    public async Task RetryAfter_LongerThanComputedCap_IsHonouredExactly()
    {
        var handler = new StubHandler((_, n) =>
        {
            if (n == 1)
            {
                var r = StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
                return r;
            }
            return StubHandler.Json(EmptyJson);
        });

        var outcome = await Screener(handler, s => s.MaxRetryDelay = TimeSpan.FromSeconds(60))
            .ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.True(outcome.Status.WasScreened);
        Assert.Equal([TimeSpan.FromSeconds(90)], _delays);
    }

    [Fact]
    public async Task RetryAfter_BeyondMaxServerRetryDelay_IsNotScreened_WithoutEarlyRetry()
    {
        var handler = new StubHandler((_, _) =>
        {
            var r = StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
            return r;
        });

        var outcome = await Screener(handler).ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);

        Assert.False(outcome.Status.WasScreened);
        Assert.Contains("rate limited", outcome.Status.Note);
        Assert.Single(handler.Requests);
        Assert.Empty(_delays);
    }
}
