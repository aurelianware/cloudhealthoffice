using CloudHealthOffice.Infrastructure.Caching;
using CloudHealthOffice.ProviderEnrollmentService.Cache;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.ProviderEnrollmentService.Tests.Cache;

/// <summary>
/// Post-A.7.2 tests: the decorator now delegates to <see cref="ICacheProvider"/>
/// instead of reaching into <c>IConnectionMultiplexer</c>. We assert the same
/// observable behaviour — hit-short-circuits-inner, miss-calls-inner,
/// write-invalidates — through the abstraction.
/// </summary>
public class RedisTenantEnrollmentConfigRepositoryTests
{
    private readonly ITenantEnrollmentConfigRepository _inner = Substitute.For<ITenantEnrollmentConfigRepository>();
    private readonly ICacheProvider _cache = Substitute.For<ICacheProvider>();
    private readonly RedisTenantEnrollmentConfigRepository _sut;

    private const string TenantId = "txmco01";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly string ExpectedKey = $"enrollment:config:{TenantId}";

    public RedisTenantEnrollmentConfigRepositoryTests()
    {
        var options = Options.Create(new ProviderEnrollmentOptions { TenantConfigCacheTtl = Ttl });
        _sut = new RedisTenantEnrollmentConfigRepository(
            _inner, _cache, options,
            Substitute.For<ILogger<RedisTenantEnrollmentConfigRepository>>());
    }

    [Fact]
    public async Task GetAsync_DelegatesToGetOrSetAsync_WithCorrectKeyAndTtl()
    {
        var config = MakeConfig();

        _cache.GetOrSetAsync<TenantEnrollmentConfig>(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<TenantEnrollmentConfig?>>>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CacheScope>(),
                Arg.Any<CancellationToken>())
            .Returns(config);

        var result = await _sut.GetAsync(TenantId);

        result.Should().NotBeNull();
        result!.TenantId.Should().Be(TenantId);

        await _cache.Received(1).GetOrSetAsync<TenantEnrollmentConfig>(
            ExpectedKey,
            Arg.Any<Func<CancellationToken, Task<TenantEnrollmentConfig?>>>(),
            Ttl,
            CacheScope.Tenant,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_FactoryHitsInnerRepository_WhenInvoked()
    {
        var config = MakeConfig();
        _inner.GetAsync(TenantId, Arg.Any<CancellationToken>()).Returns(config);

        // Capture and invoke the factory the SUT handed to GetOrSetAsync.
        Func<CancellationToken, Task<TenantEnrollmentConfig?>>? factory = null;
        _cache.GetOrSetAsync<TenantEnrollmentConfig>(
                Arg.Any<string>(),
                Arg.Do<Func<CancellationToken, Task<TenantEnrollmentConfig?>>>(f => factory = f),
                Arg.Any<TimeSpan>(),
                Arg.Any<CacheScope>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => factory!(CancellationToken.None));

        var result = await _sut.GetAsync(TenantId);

        result.Should().NotBeNull();
        await _inner.Received(1).GetAsync(TenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpsertAsync_WritesToInnerRepo_ThenInvalidatesCache()
    {
        var config = MakeConfig();

        await _sut.UpsertAsync(config);

        Received.InOrder(() =>
        {
            _inner.UpsertAsync(config, Arg.Any<CancellationToken>());
            _cache.RemoveAsync(ExpectedKey, CacheScope.Tenant, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task DeleteAsync_RemovesFromInnerRepo_ThenInvalidatesCache()
    {
        await _sut.DeleteAsync(TenantId);

        Received.InOrder(() =>
        {
            _inner.DeleteAsync(TenantId, Arg.Any<CancellationToken>());
            _cache.RemoveAsync(ExpectedKey, CacheScope.Tenant, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ListAsync_BypassesCache()
    {
        var configs = new List<TenantEnrollmentConfig> { MakeConfig() };
        _inner.ListAsync(Arg.Any<CancellationToken>()).Returns(configs);

        var result = await _sut.ListAsync();

        result.Should().HaveCount(1);
        await _inner.Received(1).ListAsync(Arg.Any<CancellationToken>());

        // No cache interactions on the admin path
        await _cache.DidNotReceive().GetOrSetAsync<TenantEnrollmentConfig>(
            Arg.Any<string>(),
            Arg.Any<Func<CancellationToken, Task<TenantEnrollmentConfig?>>>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CacheScope>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_CacheProviderThrows_BubblesUp()
    {
        // ICacheProvider is expected to absorb Redis failures internally;
        // if it throws anyway, the decorator surfaces the exception so
        // an infrastructure failure doesn't get silently swallowed twice.
        _cache.GetOrSetAsync<TenantEnrollmentConfig>(
                Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<TenantEnrollmentConfig?>>>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CacheScope>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("cache layer bug"));

        await FluentActions.Awaiting(() => _sut.GetAsync(TenantId))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(LineOfBusiness.Marketplace, EnrollmentGateMode.Disabled)]
    [InlineData(LineOfBusiness.Medicaid,    EnrollmentGateMode.Warn)]
    [InlineData(LineOfBusiness.STAR,        EnrollmentGateMode.Warn)]
    public void ResolveFor_LobOverride_WinsOverTenantDefault(
        LineOfBusiness lob, EnrollmentGateMode expectedGateMode)
    {
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

        var resolved = config.ResolveFor(lob);

        resolved.GateMode.Should().Be(expectedGateMode);
        resolved.TenantId.Should().Be(TenantId);
        resolved.Lob.Should().Be(lob);
    }

    private static TenantEnrollmentConfig MakeConfig() => new()
    {
        TenantId          = TenantId,
        DefaultGateMode   = EnrollmentGateMode.Enforce,
        EnabledStateCodes = ["TX", "CA"],
        McoIds            = ["MCO-001"]
    };
}
