using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class TmppmRuleQueryServiceTests
{
    private readonly Mock<IMongoClient> _mongoClient = new();
    private readonly Mock<IMongoDatabase> _database = new();
    private readonly Mock<IMongoCollection<BsonDocument>> _paRulesCol = new();
    private readonly Mock<IMongoCollection<BsonDocument>> _editionsCol = new();
    private readonly Mock<IMongoCollection<BsonDocument>> _diffsCol = new();
    private readonly Mock<ILogger<TmppmRuleQueryService>> _logger = new();

    private IConfiguration BuildConfig(Dictionary<string, string?>? extra = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["Mongo:Tmppm:DatabaseName"] = "cho_terminology",
            ["Mongo:Tmppm:PaRulesCollectionName"] = "tmppm_pa_rules",
            ["Mongo:Tmppm:EditionsCollectionName"] = "tmppm_editions",
            ["Mongo:Tmppm:DiffReportsCollectionName"] = "tmppm_diff_reports"
        };
        if (extra != null)
            foreach (var kv in extra) dict[kv.Key] = kv.Value;

        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    public TmppmRuleQueryServiceTests()
    {
        _mongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null)).Returns(_database.Object);
        _database.Setup(d => d.GetCollection<BsonDocument>("tmppm_pa_rules", null)).Returns(_paRulesCol.Object);
        _database.Setup(d => d.GetCollection<BsonDocument>("tmppm_editions", null)).Returns(_editionsCol.Object);
        _database.Setup(d => d.GetCollection<BsonDocument>("tmppm_diff_reports", null)).Returns(_diffsCol.Object);
    }

    private TmppmRuleQueryService CreateService(IConfiguration? config = null)
        => new(_mongoClient.Object, config ?? BuildConfig(), _logger.Object);

    private static Mock<IAsyncCursor<T>> CreateCursor<T>(List<T> items)
    {
        var cursor = new Mock<IAsyncCursor<T>>();
        var first = true;
        cursor.Setup(c => c.MoveNextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { if (first) { first = false; return items.Count > 0; } return false; });
        cursor.Setup(c => c.Current).Returns(items);
        cursor.Setup(c => c.Dispose());
        return cursor;
    }

    // ── Constructor / Configuration ──

    [Fact]
    public void Constructor_UsesConfiguredDatabaseName()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Mongo:Tmppm:DatabaseName"] = "custom_db"
        });

        _ = new TmppmRuleQueryService(_mongoClient.Object, config, _logger.Object);

        _mongoClient.Verify(c => c.GetDatabase("custom_db", null), Times.Once());
    }

    [Fact]
    public void Constructor_FallsBackToDefaultDatabaseName_WhenConfigMissing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        _ = new TmppmRuleQueryService(_mongoClient.Object, config, _logger.Object);

        _mongoClient.Verify(c => c.GetDatabase("cho_terminology", null), Times.Once());
    }

    [Fact]
    public void Constructor_UsesConfiguredCollectionNames()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["Mongo:Tmppm:PaRulesCollectionName"] = "custom_pa_rules"
        });
        _database.Setup(d => d.GetCollection<BsonDocument>("custom_pa_rules", null)).Returns(_paRulesCol.Object);

        _ = new TmppmRuleQueryService(_mongoClient.Object, config, _logger.Object);

        _database.Verify(d => d.GetCollection<BsonDocument>("custom_pa_rules", null), Times.Once());
    }

    // ── SearchByCodeAsync — normalization ──

    [Fact]
    public async Task SearchByCodeAsync_NormalizesCodeToUppercase()
    {
        var cursor = CreateCursor(new List<BsonDocument>());
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        await sut.SearchByCodeAsync("64582");

        // No assertions on the filter internals via Moq; verify FindAsync was called once
        _paRulesCol.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task SearchByCodeAsync_ReturnsEmptyList_WhenNoDocumentsMatch()
    {
        var cursor = CreateCursor(new List<BsonDocument>());
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.SearchByCodeAsync("UNKNOWN");

        result.Should().BeEmpty();
    }

    // ── SearchByCodeAsync — mapping (MapToRuleViewModel) ──

    [Fact]
    public async Task SearchByCodeAsync_MapsBasicFields_Correctly()
    {
        var doc = new BsonDocument
        {
            { "ruleId", "RULE-001" },
            { "category", "Radiology" },
            { "tmppmRef", "TMPPM §5.2" },
            { "authRequired", true },
            { "authType", "PreAuth" },
            { "procedureCodes", new BsonArray { "64582", "64583" } },
            { "codeSystem", "CPT" },
            { "state", "TX" },
            { "sourceEdition", "2025-Q1" }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.SearchByCodeAsync("64582");

        result.Should().HaveCount(1);
        var vm = result[0];
        vm.RuleId.Should().Be("RULE-001");
        vm.Category.Should().Be("Radiology");
        vm.TmppmRef.Should().Be("TMPPM §5.2");
        vm.AuthRequired.Should().BeTrue();
        vm.AuthType.Should().Be("PreAuth");
        vm.ProcedureCodes.Should().BeEquivalentTo(["64582", "64583"]);
        vm.CodeSystem.Should().Be("CPT");
        vm.State.Should().Be("TX");
        vm.SourceEdition.Should().Be("2025-Q1");
    }

    [Fact]
    public async Task SearchByCodeAsync_MapsAgeLimit_WhenPresent()
    {
        var doc = new BsonDocument
        {
            { "ruleId", "RULE-002" },
            { "category", "Pediatric" },
            { "tmppmRef", "TMPPM §3.1" },
            { "authRequired", false },
            { "procedureCodes", new BsonArray { "99213" } },
            { "codeSystem", "CPT" },
            { "state", "TX" },
            { "sourceEdition", "2025-Q1" },
            { "ageLimit", new BsonDocument
                {
                    { "minAge", 0 },
                    { "maxAge", 17 },
                    { "unit", "years" }
                }
            }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.SearchByCodeAsync("99213");

        result.Should().HaveCount(1);
        result[0].AgeLimit.Should().NotBeNull();
        result[0].AgeLimit!.MinAge.Should().Be(0);
        result[0].AgeLimit!.MaxAge.Should().Be(17);
        result[0].AgeLimit!.Unit.Should().Be("years");
    }

    [Fact]
    public async Task SearchByCodeAsync_MapsUnitLimit_WhenPresent()
    {
        var doc = new BsonDocument
        {
            { "ruleId", "RULE-003" },
            { "category", "Therapy" },
            { "tmppmRef", "TMPPM §7.4" },
            { "authRequired", true },
            { "procedureCodes", new BsonArray { "97110" } },
            { "codeSystem", "CPT" },
            { "state", "TX" },
            { "sourceEdition", "2025-Q1" },
            { "unitLimit", new BsonDocument
                {
                    { "maxUnits", 52 },
                    { "per", "year" },
                    { "resetCondition", "CalendarYear" }
                }
            }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.SearchByCodeAsync("97110");

        result.Should().HaveCount(1);
        result[0].UnitLimit.Should().NotBeNull();
        result[0].UnitLimit!.MaxUnits.Should().Be(52);
        result[0].UnitLimit!.Per.Should().Be("year");
        result[0].UnitLimit!.ResetCondition.Should().Be("CalendarYear");
    }

    [Fact]
    public async Task SearchByCodeAsync_LeavesAgeLimitNull_WhenFieldAbsent()
    {
        var doc = new BsonDocument
        {
            { "ruleId", "RULE-004" }, { "category", "Surgery" }, { "tmppmRef", "TMPPM §2.0" },
            { "authRequired", true }, { "procedureCodes", new BsonArray { "27447" } },
            { "codeSystem", "CPT" }, { "state", "TX" }, { "sourceEdition", "2025-Q1" }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.SearchByCodeAsync("27447");

        result[0].AgeLimit.Should().BeNull();
        result[0].UnitLimit.Should().BeNull();
    }

    // ── SearchByCodeAsync — state filter ──

    [Fact]
    public async Task SearchByCodeAsync_CallsFindAsync_WhenStateFilterProvided()
    {
        var cursor = CreateCursor(new List<BsonDocument>());
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        await sut.SearchByCodeAsync("64582", state: "TX");

        _paRulesCol.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    // ── GetRulesByCategoryAsync — mapping ──

    [Fact]
    public async Task GetRulesByCategoryAsync_MapsDocumentsToViewModels()
    {
        var doc = new BsonDocument
        {
            { "ruleId", "RULE-100" },
            { "category", "DME" },
            { "tmppmRef", "TMPPM §9.0" },
            { "authRequired", true },
            { "procedureCodes", new BsonArray { "E0601" } },
            { "codeSystem", "HCPCS" },
            { "state", "TX" },
            { "sourceEdition", "2025-Q1" }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.GetRulesByCategoryAsync("DME");

        result.Should().HaveCount(1);
        result[0].RuleId.Should().Be("RULE-100");
        result[0].CodeSystem.Should().Be("HCPCS");
    }

    [Fact]
    public async Task GetRulesByCategoryAsync_CallsFindAsync_WhenStateFilterProvided()
    {
        var cursor = CreateCursor(new List<BsonDocument>());
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        await sut.GetRulesByCategoryAsync("DME", state: "TX");

        _paRulesCol.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    // ── GetCurrentEditionAsync — mapping (MapToEditionViewModel) ──

    [Fact]
    public async Task GetCurrentEditionAsync_ReturnsNull_WhenNoEditionsExist()
    {
        var cursor = CreateCursor(new List<BsonDocument>());
        _editionsCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.GetCurrentEditionAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentEditionAsync_MapsEditionFields_Correctly()
    {
        var pubDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var policyDate = new DateTime(2025, 3, 31, 0, 0, 0, DateTimeKind.Utc);
        var ingestedAt = new DateTime(2025, 1, 5, 12, 0, 0, DateTimeKind.Utc);

        var doc = new BsonDocument
        {
            { "editionId", "2025-Q1" },
            { "publicationDate", BsonDateTime.Create(pubDate) },
            { "policyThroughDate", BsonDateTime.Create(policyDate) },
            { "sourceUrl", "https://www.tmhp.com/tmppm/2025-q1.pdf" },
            { "ingestedAt", BsonDateTime.Create(ingestedAt) },
            { "chapters", new BsonArray() }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _editionsCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.GetCurrentEditionAsync();

        result.Should().NotBeNull();
        result!.EditionId.Should().Be("2025-Q1");
        result.PublicationDate.Should().Be(new DateOnly(2025, 1, 1));
        result.PolicyThroughDate.Should().Be(new DateOnly(2025, 3, 31));
        result.SourceUrl.Should().Be("https://www.tmhp.com/tmppm/2025-q1.pdf");
        result.IngestedAt.Should().Be(ingestedAt);
        result.Chapters.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCurrentEditionAsync_MapsChapters_WhenPresent()
    {
        var doc = new BsonDocument
        {
            { "editionId", "2025-Q2" },
            { "publicationDate", BsonDateTime.Create(new DateTime(2025, 4, 1, 0, 0, 0, DateTimeKind.Utc)) },
            { "policyThroughDate", BsonDateTime.Create(new DateTime(2025, 6, 30, 0, 0, 0, DateTimeKind.Utc)) },
            { "sourceUrl", "" },
            { "ingestedAt", BsonDateTime.Create(DateTime.UtcNow) },
            { "chapters", new BsonArray
                {
                    new BsonDocument
                    {
                        { "chapterId", "CH-05" },
                        { "title", "Radiology Services" },
                        { "pdfUrl", "https://www.tmhp.com/tmppm/ch5.pdf" },
                        { "sha256", "abc123" },
                        { "extractedRuleCount", 42 }
                    }
                }
            }
        };

        var cursor = CreateCursor(new List<BsonDocument> { doc });
        _editionsCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.GetCurrentEditionAsync();

        result!.Chapters.Should().HaveCount(1);
        result.Chapters[0].ChapterId.Should().Be("CH-05");
        result.Chapters[0].Title.Should().Be("Radiology Services");
        result.Chapters[0].Sha256.Should().Be("abc123");
        result.Chapters[0].ExtractedRuleCount.Should().Be(42);
    }

    // ── AutocompleteCodeAsync ──

    [Fact]
    public async Task AutocompleteCodeAsync_ReturnsEmpty_WhenPrefixIsBlank()
    {
        var sut = CreateService();
        var result = await sut.AutocompleteCodeAsync("   ");
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AutocompleteCodeAsync_NormalizesPrefix_AndQueriesCollection()
    {
        var cursor = CreateCursor(new List<BsonDocument>());
        _paRulesCol.Setup(c => c.FindAsync(
                It.IsAny<FilterDefinition<BsonDocument>>(),
                It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cursor.Object);

        var sut = CreateService();
        var result = await sut.AutocompleteCodeAsync("g02");

        result.Should().BeEmpty();
        _paRulesCol.Verify(c => c.FindAsync(
            It.IsAny<FilterDefinition<BsonDocument>>(),
            It.IsAny<FindOptions<BsonDocument, BsonDocument>>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }
}
