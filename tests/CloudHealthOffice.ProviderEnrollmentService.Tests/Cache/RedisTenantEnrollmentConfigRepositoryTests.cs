using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests.Cache;

public class RedisTenantEnrollmentConfigRepositoryTests
{
    private readonly ITenantEnrollmentConfigRepository _inner;
    private readonly IDatabase _db;
    private readonly RedisTenantEnrollmentConfigRepository _sut;

    private const string TenantId = "pchp";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    // Must match the JsonSerializerOptions used inside the production code
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RedisTenantEnrollmentConfigRepositoryTests()
    {
        _inner = Substitute.For<ITenantEnrollmentConfigRepository>();
        _db = Substitute.For<IDatabase>();

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_db);

        var options = Options.Create(new ProviderEnrollmentOptions
        {
            TenantConfigCacheTtl = Ttl
        });
        var logger = Substitute.For<ILogger<RedisTenantEnrollmentConfigRepository>>();

        _sut = new RedisTenantEnrollmentConfigRepository(_inner, redis, options, logger);
    }

    // ── 1. Redis hit returns cached config without touching inner repo ──

    [Fact]
    public async Task GetAsync_RedisHit_ReturnsCachedConfig_WithoutHittingInnerRepository()
    {
        // Arrange
        var config = MakeConfig();
        var json = JsonSerializer.Serialize(config, JsonOpts);

        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)json);

        // Act
        var result = await _sut.GetAsync(TenantId);

        // Assert
        result.Should().NotBeNull();
        result!.TenantId.Should().Be(TenantId);
        result.DefaultGateMode.Should().Be(EnrollmentGateMode.Enforce);

        await _inner.DidNotReceive()
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── 2. Redis miss falls through to inner, caches the result ─────

    [Fact]
    public async Task GetAsync_RedisMiss_HitsInnerRepository_AndCachesResult()
    {
        // Arrange — Redis returns nothing
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        var config = MakeConfig();
        _inner.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(config);

        // Act
        var result = await _sut.GetAsync(TenantId);

        // Assert — correct result
        result.Should().NotBeNull();
        result!.TenantId.Should().Be(TenantId);

        // Assert — StringSetAsync called with right key and TTL
        await _db.Received(1).StringSetAsync(
            (RedisKey)$"enrollment:config:{TenantId}",
            Arg.Any<RedisValue>(),
            Ttl,
            Arg.Any<bool>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    // ── 3. Null from inner repo does NOT get cached ─────────────────

    [Fact]
    public async Task GetAsync_NullResultFromInner_DoesNotCache()
    {
        // Arrange
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        _inner.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns((TenantEnrollmentConfig?)null);

        // Act
        var result = await _sut.GetAsync(TenantId);

        // Assert
        result.Should().BeNull();

        await _db.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<bool>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    // ── 4. Redis exception falls through to inner repo ──────────────

    [Fact]
    public async Task GetAsync_RedisThrows_FallsThroughToInnerRepository()
    {
        // Arrange — Redis blows up
        _db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var config = MakeConfig();
        _inner.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(config);

        // Act
        var result = await _sut.GetAsync(TenantId);

        // Assert — inner repo result returned despite Redis failure
        result.Should().NotBeNull();
        result!.TenantId.Should().Be(TenantId);

        await _inner.Received(1)
            .GetAsync(TenantId, Arg.Any<CancellationToken>());
    }

    // ── 5. Upsert writes to inner then invalidates Redis key ────────

    [Fact]
    public async Task UpsertAsync_WritesToInnerRepo_ThenInvalidatesRedisKey()
    {
        // Arrange
        var config = MakeConfig();

        // Act
        await _sut.UpsertAsync(config);

        // Assert — inner repo written
        await _inner.Received(1)
            .UpsertAsync(config, Arg.Any<CancellationToken>());

        // Assert — Redis key invalidated
        await _db.Received(1).KeyDeleteAsync(
            (RedisKey)$"enrollment:config:{TenantId}",
            Arg.Any<CommandFlags>());
    }

    // ── 6. ResolveFor: LOB override wins over tenant default ────────

    [Theory]
    [InlineData(LineOfBusiness.Marketplace, EnrollmentGateMode.Disabled)]
    [InlineData(LineOfBusiness.Medicaid,    EnrollmentGateMode.Warn)]
    [InlineData(LineOfBusiness.STAR,        EnrollmentGateMode.Warn)]
    public void ResolveFor_LobOverride_WinsOverTenantDefault(
        LineOfBusiness lob, EnrollmentGateMode expectedGateMode)
    {
        // Arrange — Warn by default, Marketplace overridden to Disabled
        var config = new TenantEnrollmentConfig
        {
            TenantId        = TenantId,
            DefaultGateMode = EnrollmentGateMode.Warn,
            LobOverrides    =
            [
                new LobEnrollmentOverride
                {
                    Lob      = LineOfBusiness.Marketplace,
                    GateMode = EnrollmentGateMode.Disabled
                }
            ]
        };

        // Act
        var resolved = config.ResolveFor(lob);

        // Assert
        resolved.GateMode.Should().Be(expectedGateMode);
        resolved.TenantId.Should().Be(TenantId);
        resolved.Lob.Should().Be(lob);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static TenantEnrollmentConfig MakeConfig() => new()
    {
        TenantId          = TenantId,
        DefaultGateMode   = EnrollmentGateMode.Enforce,
        EnabledStateCodes = ["TX", "CA"],
        McoIds            = ["MCO-001"]
    };
}
