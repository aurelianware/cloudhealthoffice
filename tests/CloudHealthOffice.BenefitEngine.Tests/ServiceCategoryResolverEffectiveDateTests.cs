using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// Capability BP 5.10 — pin effective-date filtering on
/// <see cref="ServiceCategoryResolver"/>. The resolver consults
/// <see cref="ServiceCategoryMapping.EffectiveStart"/>,
/// <see cref="ServiceCategoryMapping.EffectiveEnd"/>, and
/// <see cref="ServiceCategoryMapping.IsActive"/> against the claim
/// line's service date. Both bounds are inclusive; null means open;
/// IsActive=false drops the row regardless of window.
/// </summary>
public class ServiceCategoryResolverEffectiveDateTests
{
    private static IServiceCategoryResolver Build(params ServiceCategoryMapping[] mappings)
    {
        var repo = new InMemoryMappingRepo(mappings);
        return new ServiceCategoryResolver(repo, NullLogger<ServiceCategoryResolver>.Instance);
    }

    private static ServiceCategoryMapping Mapping(
        string code,
        DateOnly? start = null,
        DateOnly? end = null,
        bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-a",
        BenefitPlanId = null,
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        EffectiveStart = start,
        EffectiveEnd = end,
        IsActive = isActive,
        Rules = new List<ProcedureCodeRule>
        {
            new() { Priority = 10, CodeType = "CPT", CodePattern = "99213" }
        }
    };

    [Fact]
    public async Task MatchInWindow_ReturnsMapping()
    {
        var resolver = Build(Mapping("Office Visit",
            start: new DateOnly(2026, 1, 1),
            end: new DateOnly(2026, 12, 31)));

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 6, 15),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        Assert.NotNull(match);
        Assert.Equal("Office Visit", match!.ServiceTypeCode);
        Assert.Equal("TenantDefault", match.MatchedBy);
    }

    [Fact]
    public async Task EffectiveStartFuture_ServiceDateToday_FallsThroughToPosFallback()
    {
        var resolver = Build(Mapping("Office Visit",
            start: new DateOnly(2027, 1, 1)));

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 6, 15),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        // Mapping was filtered out → resolver falls through to POS-11
        // inference, which yields service-type 98 with MatchedBy=SystemDefault.
        Assert.NotNull(match);
        Assert.Equal("98", match!.ServiceTypeCode);
        Assert.Equal("SystemDefault", match.MatchedBy);
    }

    [Fact]
    public async Task EffectiveEndPast_ServiceDateInsidePast_Matches()
    {
        var resolver = Build(Mapping("Office Visit (CMS 2025)",
            start: new DateOnly(2025, 1, 1),
            end: new DateOnly(2025, 12, 31)));

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2025, 8, 15),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        Assert.NotNull(match);
        Assert.Equal("Office Visit (CMS 2025)", match!.ServiceTypeCode);
    }

    [Fact]
    public async Task IsActiveFalse_NeverMatches_EvenWhenWindowCovers()
    {
        var resolver = Build(Mapping("Office Visit",
            start: new DateOnly(2026, 1, 1),
            end: new DateOnly(2026, 12, 31),
            isActive: false));

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 6, 15),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        // Filtered out → falls through to POS fallback
        Assert.NotNull(match);
        Assert.Equal("SystemDefault", match!.MatchedBy);
    }

    [Fact]
    public async Task BothBoundsNull_AlwaysMatches()
    {
        var resolver = Build(Mapping("Office Visit"));

        var matchPast = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2010, 1, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);
        var matchFuture = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2050, 12, 31),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        Assert.Equal("Office Visit", matchPast!.ServiceTypeCode);
        Assert.Equal("Office Visit", matchFuture!.ServiceTypeCode);
    }

    [Fact]
    public async Task DeterministicAcrossRuns_ReplayingFixtureYieldsSameMatch()
    {
        var resolver = Build(
            Mapping("Office Visit (2025)",
                start: new DateOnly(2025, 1, 1),
                end: new DateOnly(2025, 12, 31)),
            Mapping("Office Visit (2026)",
                start: new DateOnly(2026, 1, 1),
                end: new DateOnly(2026, 12, 31)));

        for (var i = 0; i < 5; i++)
        {
            var match = await resolver.ResolveAsync(
                "tenant-a", Guid.NewGuid(),
                serviceDate: new DateOnly(2026, 6, 15),
                procedureCode: "99213", codeType: "CPT", placeOfService: "11",
                modifiers: Array.Empty<string>(), revenueCode: null);

            Assert.NotNull(match);
            Assert.Equal("Office Visit (2026)", match!.ServiceTypeCode);
        }
    }

    [Fact]
    public async Task InclusiveBounds_MatchOnEdgeDates()
    {
        var resolver = Build(Mapping("Office Visit",
            start: new DateOnly(2026, 1, 1),
            end: new DateOnly(2026, 12, 31)));

        var matchStart = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 1, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);
        var matchEnd = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 12, 31),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        Assert.Equal("Office Visit", matchStart!.ServiceTypeCode);
        Assert.Equal("Office Visit", matchEnd!.ServiceTypeCode);
    }

    private sealed class InMemoryMappingRepo : IServiceCategoryMappingRepository
    {
        private readonly List<ServiceCategoryMapping> _mappings;

        public InMemoryMappingRepo(IEnumerable<ServiceCategoryMapping> mappings)
            => _mappings = mappings.ToList();

        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
            string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
        {
            IReadOnlyList<ServiceCategoryMapping> result = _mappings
                .Where(m => m.TenantId == tenantId && m.BenefitPlanId == benefitPlanId)
                .ToList();
            return Task.FromResult(result);
        }
    }
}
