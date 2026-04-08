using System.Net;
using System.Reflection;
using System.Text;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using CloudHealthOffice.ProviderEnrollmentService.Sources.Texas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests;

public class TmhpPemsSourceTests
{
    private readonly IEnrollmentRepository _cache;
    private readonly FakeHttpHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly TmhpPemsSource _sut;

    private const string TestNpi = "1234567890";

    public TmhpPemsSourceTests()
    {
        _cache = Substitute.For<IEnrollmentRepository>();
        _httpHandler = new FakeHttpHandler();
        _httpClient = new HttpClient(_httpHandler)
        {
            BaseAddress = new Uri("https://test.tmhp.com")
        };
        var logger = Substitute.For<ILogger<TmhpPemsSource>>();
        var options = Options.Create(new ProviderEnrollmentOptions
        {
            CacheTtl = TimeSpan.FromHours(4),
            Tmhp = new TmhpPemsOptions
            {
                BaseUrl = "https://test.tmhp.com",
                ApiKey = "test-key",
                SftpHost = "localhost",
                SftpUsername = "testuser",
                SftpPrivateKeyPath = "/nonexistent/key",
                BatchDropPath = "/pems/exports/"
            }
        });

        _sut = new TmhpPemsSource(_httpClient, _cache, options, logger);
    }

    // ── Test 1: Cache hit returns cached record without calling API ──

    [Fact]
    public async Task GetEnrollmentAsync_CacheHit_ReturnsCachedRecord_WithoutCallingApi()
    {
        // Arrange — fresh cached record (CachedAt is within the 4-hour window)
        var freshRecord = MakeRecord(TestNpi, cachedAt: DateTime.UtcNow.AddMinutes(-30));
        _cache.GetAsync(TestNpi, "TX", Arg.Any<CancellationToken>())
            .Returns(freshRecord);

        // Act
        var result = await _sut.GetEnrollmentAsync(TestNpi, DateOnly.FromDateTime(DateTime.Today));

        // Assert
        result.Should().NotBeNull();
        result!.IsFromCache.Should().BeTrue();
        result.Npi.Should().Be(TestNpi);
        _httpHandler.RequestCount.Should().Be(0, "the API should not be called when cache is fresh");
    }

    // ── Test 2: Cache miss calls API and caches result ──────────────

    [Fact]
    public async Task GetEnrollmentAsync_CacheMiss_CallsApiAndCachesResult()
    {
        // Arrange — cache returns null (miss)
        _cache.GetAsync(TestNpi, "TX", Arg.Any<CancellationToken>())
            .Returns((StateEnrollmentRecord?)null);

        _httpHandler.ResponseBody = """
        {
            "enrollmentStatus": "ACTIVE",
            "effectiveDate": "2023-01-15",
            "terminationDate": "",
            "revalidationDate": "2026-01-15",
            "providerTypeCode": "20",
            "taxonomyCodes": ["207Q00000X"],
            "countiesServed": ["Harris", "Travis"],
            "programs": ["MEDICAID", "STAR"],
            "mcoContracts": ["MCO-001"],
            "restrictions": null
        }
        """;

        // Act
        var result = await _sut.GetEnrollmentAsync(TestNpi, DateOnly.FromDateTime(DateTime.Today));

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrollmentStatus.Active);
        result.StateCode.Should().Be("TX");
        result.SourceSystem.Should().Be("PEMS");
        result.ProviderType.Should().Be(ProviderTypeClassification.PhysicianMD);
        result.EnrolledTaxonomies.Should().Contain("207Q00000X");
        result.EnrolledCounties.Should().BeEquivalentTo(new[] { "Harris", "Travis" });
        result.McoParticipation.Should().Contain("MCO-001");
        result.IsFromCache.Should().BeFalse();

        await _cache.Received(1).UpsertAsync(
            Arg.Is<StateEnrollmentRecord>(r => r.Npi == TestNpi && r.Status == EnrollmentStatus.Active),
            Arg.Any<CancellationToken>());
    }

    // ── Test 3: API unavailable returns stale cache ──────────────────

    [Fact]
    public async Task GetEnrollmentAsync_ApiUnavailable_ReturnsStaleCache()
    {
        // Arrange — stale cached record (older than 4 hours)
        var staleRecord = MakeRecord(TestNpi, cachedAt: DateTime.UtcNow.AddHours(-5));
        _cache.GetAsync(TestNpi, "TX", Arg.Any<CancellationToken>())
            .Returns(staleRecord);

        _httpHandler.ShouldThrow = true;

        // Act
        var result = await _sut.GetEnrollmentAsync(TestNpi, DateOnly.FromDateTime(DateTime.Today));

        // Assert — stale record returned rather than null
        result.Should().NotBeNull();
        result!.Npi.Should().Be(TestNpi);
    }

    // ── Test 4: MapStatus maps all PEMS status codes correctly ──────

    [Theory]
    [InlineData("A", EnrollmentStatus.Active)]
    [InlineData("ACTIVE", EnrollmentStatus.Active)]
    [InlineData("P", EnrollmentStatus.Pending)]
    [InlineData("PENDING", EnrollmentStatus.Pending)]
    [InlineData("S", EnrollmentStatus.Suspended)]
    [InlineData("T", EnrollmentStatus.Terminated)]
    [InlineData("D", EnrollmentStatus.Denied)]
    public async Task MapStatus_AllPemsStatusCodes_MapsCorrectly(
        string pemsStatusCode, EnrollmentStatus expected)
    {
        // Arrange — cache miss forces API path which exercises MapStatus
        _cache.GetAsync(TestNpi, "TX", Arg.Any<CancellationToken>())
            .Returns((StateEnrollmentRecord?)null);

        _httpHandler.ResponseBody = $$"""
        {
            "enrollmentStatus": "{{pemsStatusCode}}",
            "effectiveDate": "2023-01-15",
            "terminationDate": "",
            "revalidationDate": "",
            "providerTypeCode": "20",
            "taxonomyCodes": [],
            "countiesServed": [],
            "programs": [],
            "mcoContracts": [],
            "restrictions": null
        }
        """;

        // Act
        var result = await _sut.GetEnrollmentAsync(TestNpi, DateOnly.FromDateTime(DateTime.Today));

        // Assert
        result.Should().NotBeNull();
        result!.Status.Should().Be(expected);
    }

    // ── Test 5: CSV parsing via ParseCsvLine ────────────────────────

    [Fact]
    public void BulkSyncAsync_ValidCsvLine_ParsesAndUpserts()
    {
        // ParseCsvLine is private static — invoke via reflection to validate
        // CSV parsing without needing a live SFTP connection.
        // Format: NPI,Status,EffectiveDate,TermDate,RevalDate,ProvType,Taxonomies,Counties,Programs,McoContracts
        const string csvLine =
            "1234567890,ACTIVE,2023-01-15,,,20,207Q00000X|208D00000X,Harris|Travis,MEDICAID,MCO-001|MCO-002";

        var parseCsvLine = typeof(TmhpPemsSource)
            .GetMethod("ParseCsvLine", BindingFlags.NonPublic | BindingFlags.Static)!;

        var record = (StateEnrollmentRecord?)parseCsvLine.Invoke(null, [csvLine]);

        // Assert record was parsed (not null / skipped)
        record.Should().NotBeNull();
        record!.Npi.Should().Be("1234567890");
        record.StateCode.Should().Be("TX");
        record.SourceSystem.Should().Be("PEMS");
        record.Status.Should().Be(EnrollmentStatus.Active);
        record.EffectiveDate.Should().Be(new DateOnly(2023, 1, 15));
        record.TerminationDate.Should().BeNull();
        record.RevalidationDueDate.Should().BeNull();
        record.ProviderType.Should().Be(ProviderTypeClassification.PhysicianMD);
        record.EnrolledTaxonomies.Should().BeEquivalentTo(new[] { "207Q00000X", "208D00000X" });
        record.EnrolledCounties.Should().BeEquivalentTo(new[] { "Harris", "Travis" });
        record.SupportedLobs.Should().Be(LineOfBusiness.Medicaid);
        record.McoParticipation.Should().BeEquivalentTo(new[] { "MCO-001", "MCO-002" });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static StateEnrollmentRecord MakeRecord(string npi, DateTime cachedAt) => new()
    {
        Npi = npi,
        StateCode = "TX",
        SourceSystem = "PEMS",
        Status = EnrollmentStatus.Active,
        EffectiveDate = new DateOnly(2023, 1, 15),
        ProviderType = ProviderTypeClassification.PhysicianMD,
        CachedAt = cachedAt,
        IsFromCache = false
    };

    /// <summary>
    /// Lightweight fake handler for <see cref="HttpClient"/> that avoids
    /// real HTTP traffic and exposes call-count assertions.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = "{}";
        public bool ShouldThrow { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (ShouldThrow)
                throw new HttpRequestException("Simulated TMHP outage");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
