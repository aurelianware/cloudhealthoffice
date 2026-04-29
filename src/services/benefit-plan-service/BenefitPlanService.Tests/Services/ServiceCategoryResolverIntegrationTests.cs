using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Tests.Fakes;
using BenefitPlanService.Tests.Repositories;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Capability BP 5.6 — end-to-end resolver integration test.
///
/// <para>
/// Wires the real <see cref="ServiceCategoryResolver"/> on top of the
/// real <see cref="CachingServiceCategoryMappingRepository"/> on top of
/// an <see cref="InMemoryServiceCategoryMappingStore"/>. Verifies the
/// three-tier resolution semantics:
/// </para>
/// <list type="number">
///   <item>Plan-specific override wins over tenant default.</item>
///   <item>Tenant default wins over POS-inference fallback.</item>
///   <item>POS-inference fallback applies when no mappings match.</item>
/// </list>
/// <para>
/// The audit field <c>MatchedBy</c> distinguishes the three layers and is
/// asserted explicitly so future regressions in the resolver's layering
/// are caught here.
/// </para>
/// </summary>
public sealed class ServiceCategoryResolverIntegrationTests
{
    [Fact]
    public async Task Plan_Specific_Override_Wins_Over_Tenant_Default()
    {
        var planId = Guid.NewGuid();
        var (resolver, write) = Build();

        await write.CreateAsync(Mapping(
            "tenant-a", null, "Office Visit",
            new ProcedureCodeRule { Priority = 10, CodeType = "CPT", CodePattern = "99213" }));
        await write.CreateAsync(Mapping(
            "tenant-a", planId, "Specialist Visit",
            new ProcedureCodeRule { Priority = 10, CodeType = "CPT", CodePattern = "99213" }));

        var match = await resolver.ResolveAsync(
            "tenant-a", planId,
            serviceDate: new DateOnly(2026, 4, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        match.Should().NotBeNull();
        match!.ServiceTypeCode.Should().Be("Specialist Visit");
        match.MatchedBy.Should().Be("PlanOverride");
    }

    [Fact]
    public async Task Tenant_Default_Wins_Over_POS_Fallback()
    {
        var planId = Guid.NewGuid();
        var (resolver, write) = Build();

        await write.CreateAsync(Mapping(
            "tenant-a", null, "Office Visit",
            new ProcedureCodeRule { Priority = 10, CodeType = "CPT", CodePattern = "99213" }));

        var match = await resolver.ResolveAsync(
            "tenant-a", planId,
            serviceDate: new DateOnly(2026, 4, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        match.Should().NotBeNull();
        match!.ServiceTypeCode.Should().Be("Office Visit");
        match.MatchedBy.Should().Be("TenantDefault");
    }

    [Fact]
    public async Task POS_Fallback_Applies_When_No_Mapping_Matches()
    {
        var (resolver, _) = Build();

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 4, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        match.Should().NotBeNull();
        match!.MatchedBy.Should().Be("SystemDefault");
        match.ServiceTypeCode.Should().Be("98", "POS 11 (office) maps to X12 service type 98");
    }

    [Fact]
    public async Task Returns_Null_When_No_Mapping_And_POS_Has_No_Inference()
    {
        var (resolver, _) = Build();

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 4, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "99",
            modifiers: Array.Empty<string>(), revenueCode: null);

        match.Should().BeNull();
    }

    [Fact]
    public async Task Newest_Mapping_Wins_When_Multiple_Rows_Share_ServiceTypeCode()
    {
        // Mirrors the seeder version-bump re-apply scenario: two seed rows
        // for the same serviceTypeCode coexist in the store. The newer row
        // (later CreatedAt) must win deterministically, so the resolver's
        // first-match-wins semantics produce stable adjudication regardless
        // of insertion order surfaced by the underlying store.
        var (resolver, write) = Build();

        var older = new ServiceCategoryMapping
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-a",
            BenefitPlanId = null,
            ServiceTypeCode = "Office Visit (v1)",
            ServiceTypeDescription = "v1",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            Rules = new List<ProcedureCodeRule>
            {
                new() { Priority = 10, CodeType = "CPT", CodePattern = "99213" },
            },
        };
        var newer = new ServiceCategoryMapping
        {
            Id = Guid.NewGuid(),
            TenantId = "tenant-a",
            BenefitPlanId = null,
            ServiceTypeCode = "Office Visit (v2)",
            ServiceTypeDescription = "v2",
            CreatedAt = DateTimeOffset.UtcNow,
            Rules = new List<ProcedureCodeRule>
            {
                new() { Priority = 10, CodeType = "CPT", CodePattern = "99213" },
            },
        };

        // Insert older first, then newer — store-insertion order is
        // deliberately the opposite of the desired iteration order.
        await write.CreateAsync(older);
        await write.CreateAsync(newer);

        var match = await resolver.ResolveAsync(
            "tenant-a", Guid.NewGuid(),
            serviceDate: new DateOnly(2026, 4, 1),
            procedureCode: "99213", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);

        match.Should().NotBeNull();
        match!.ServiceTypeCode.Should().Be("Office Visit (v2)",
            "newest mapping should win regardless of insertion order");
    }

    [Fact]
    public async Task Range_Match_Hits_Codes_Inside_The_Range()
    {
        var planId = Guid.NewGuid();
        var (resolver, write) = Build();

        await write.CreateAsync(Mapping(
            "tenant-a", null, "Office Visit",
            new ProcedureCodeRule
            {
                Priority = 10,
                CodeType = "CPT",
                CodePattern = "99201",
                CodeRangeEnd = "99215",
            }));

        foreach (var code in new[] { "99201", "99213", "99215" })
        {
            var match = await resolver.ResolveAsync(
                "tenant-a", planId,
                serviceDate: new DateOnly(2026, 4, 1),
                procedureCode: code, codeType: "CPT", placeOfService: "11",
                modifiers: Array.Empty<string>(), revenueCode: null);

            match.Should().NotBeNull();
            match!.ServiceTypeCode.Should().Be("Office Visit");
        }

        // Outside-range fall through to POS fallback.
        var outOfRange = await resolver.ResolveAsync(
            "tenant-a", planId,
            serviceDate: new DateOnly(2026, 4, 1),
            procedureCode: "99216", codeType: "CPT", placeOfService: "11",
            modifiers: Array.Empty<string>(), revenueCode: null);
        outOfRange!.MatchedBy.Should().Be("SystemDefault");
    }

    private static (IServiceCategoryResolver resolver, IServiceCategoryMappingWriteRepository write)
        Build()
    {
        var inner = new InMemoryServiceCategoryMappingStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new ServiceCategoryMappingOptions { CacheTtl = TimeSpan.Zero };
        var monitor = new TestOptionsMonitor<ServiceCategoryMappingOptions>(options);
        var decorator = new CachingServiceCategoryMappingRepository(
            inner, inner, inner, cache, monitor);
        var resolver = new ServiceCategoryResolver(
            decorator, NullLogger<ServiceCategoryResolver>.Instance);
        return (resolver, decorator);
    }

    private static ServiceCategoryMapping Mapping(
        string tenantId, Guid? planId, string code, ProcedureCodeRule rule) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        BenefitPlanId = planId,
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        Rules = new List<ProcedureCodeRule> { rule },
    };
}
