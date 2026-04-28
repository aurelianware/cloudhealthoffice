using BenefitPlanService.Models;
using BenefitPlanService.Repositories;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.BenefitEngine.Domain;
using Microsoft.Extensions.Caching.Memory;

namespace BenefitPlanService.Tests.Repositories;

/// <summary>
/// Capability BP 5.6 — verifies the cache decorator hides the inner store
/// for repeat reads, invalidates on writes, and re-keys correctly when a
/// caller changes a mapping's plan scope on update. Tenant isolation and
/// plan-vs-tenant-default keying are verified by the same harness.
/// </summary>
public sealed class CachingServiceCategoryMappingRepositoryTests
{
    [Fact]
    public async Task Repeat_Read_Within_Ttl_Hits_Inner_Store_Once()
    {
        var (cached, inner) = Build();
        await cached.CreateAsync(NewMapping("tenant-a", planId: null, code: "Office Visit"));
        // First create's invalidation already cleared the cache, so the
        // first read after create is a miss; the second read should be a hit.
        var first = await cached.GetMappingsAsync("tenant-a", null);
        var second = await cached.GetMappingsAsync("tenant-a", null);

        first.Should().HaveCount(1);
        second.Should().HaveCount(1);
        inner.GetMappingsCallCount.Should().Be(1, "second read served from cache");
    }

    [Fact]
    public async Task Tenant_Isolation_On_Cache_Key()
    {
        var (cached, inner) = Build();
        await cached.CreateAsync(NewMapping("tenant-a", null, "Office Visit"));
        await cached.CreateAsync(NewMapping("tenant-b", null, "Office Visit"));

        var a = await cached.GetMappingsAsync("tenant-a", null);
        var b = await cached.GetMappingsAsync("tenant-b", null);

        a.Should().HaveCount(1);
        b.Should().HaveCount(1);
        a.Single().TenantId.Should().Be("tenant-a");
        b.Single().TenantId.Should().Be("tenant-b");
    }

    [Fact]
    public async Task Tenant_Default_And_Plan_Override_Are_Cached_Independently()
    {
        var (cached, inner) = Build();
        var planId = Guid.NewGuid();
        await cached.CreateAsync(NewMapping("tenant-a", null, "Office Visit"));
        await cached.CreateAsync(NewMapping("tenant-a", planId, "Office Visit"));

        var tenantDefaults = await cached.GetMappingsAsync("tenant-a", null);
        var planOverrides = await cached.GetMappingsAsync("tenant-a", planId);

        tenantDefaults.Should().HaveCount(1);
        tenantDefaults.Single().BenefitPlanId.Should().BeNull();
        planOverrides.Should().HaveCount(1);
        planOverrides.Single().BenefitPlanId.Should().Be(planId);
    }

    [Fact]
    public async Task Create_Invalidates_The_Affected_Cache_Scope()
    {
        var (cached, inner) = Build();
        // Warm the cache with a read returning zero mappings.
        var empty = await cached.GetMappingsAsync("tenant-a", null);
        empty.Should().BeEmpty();
        inner.GetMappingsCallCount.Should().Be(1);

        await cached.CreateAsync(NewMapping("tenant-a", null, "Office Visit"));
        var afterCreate = await cached.GetMappingsAsync("tenant-a", null);

        afterCreate.Should().HaveCount(1);
        inner.GetMappingsCallCount.Should().Be(2, "create invalidated the warmed entry");
    }

    [Fact]
    public async Task Update_Invalidates_Both_Old_And_New_Plan_Scopes_When_Re_Scoped()
    {
        var (cached, _) = Build();
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();
        var created = await cached.CreateAsync(NewMapping("tenant-a", planA, "Office Visit"));

        // Warm both scopes.
        (await cached.GetMappingsAsync("tenant-a", planA)).Should().HaveCount(1);
        (await cached.GetMappingsAsync("tenant-a", planB)).Should().BeEmpty();

        // Re-scope A → B.
        created.BenefitPlanId = planB;
        await cached.UpdateAsync(created);

        (await cached.GetMappingsAsync("tenant-a", planA)).Should().BeEmpty();
        (await cached.GetMappingsAsync("tenant-a", planB)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Delete_Invalidates_The_Affected_Cache_Scope()
    {
        var (cached, _) = Build();
        var created = await cached.CreateAsync(NewMapping("tenant-a", null, "Office Visit"));
        (await cached.GetMappingsAsync("tenant-a", null)).Should().HaveCount(1);

        await cached.DeleteAsync("tenant-a", created.Id);

        (await cached.GetMappingsAsync("tenant-a", null)).Should().BeEmpty();
    }

    [Fact]
    public async Task Zero_Ttl_Disables_Caching_Entirely()
    {
        var (cached, inner) = Build(ttl: TimeSpan.Zero);
        await cached.CreateAsync(NewMapping("tenant-a", null, "Office Visit"));

        await cached.GetMappingsAsync("tenant-a", null);
        await cached.GetMappingsAsync("tenant-a", null);
        await cached.GetMappingsAsync("tenant-a", null);

        inner.GetMappingsCallCount.Should().Be(3, "zero TTL bypasses the cache");
    }

    [Fact]
    public async Task GetByIdAsync_Returns_Null_For_Mismatched_Tenant()
    {
        var (cached, _) = Build();
        var created = await cached.CreateAsync(NewMapping("tenant-a", null, "Office Visit"));

        var crossRead = await cached.GetByIdAsync("tenant-b", created.Id);

        crossRead.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_Throws_KeyNotFound_For_Missing_Mapping()
    {
        var (cached, _) = Build();
        var ghost = NewMapping("tenant-a", null, "Office Visit");
        ghost.Id = Guid.NewGuid();

        Func<Task> act = () => cached.UpdateAsync(ghost);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    private static (CachingServiceCategoryMappingRepository cached, InMemoryServiceCategoryMappingStore inner)
        Build(TimeSpan? ttl = null)
    {
        var inner = new InMemoryServiceCategoryMappingStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = new ServiceCategoryMappingOptions { CacheTtl = ttl ?? TimeSpan.FromMinutes(5) };
        var monitor = new TestOptionsMonitor<ServiceCategoryMappingOptions>(options);
        var decorator = new CachingServiceCategoryMappingRepository(inner, inner, inner, cache, monitor);
        return (decorator, inner);
    }

    private static ServiceCategoryMapping NewMapping(string tenantId, Guid? planId, string code) => new()
    {
        TenantId = tenantId,
        BenefitPlanId = planId,
        ServiceTypeCode = code,
        ServiceTypeDescription = code,
        Rules = new List<ProcedureCodeRule>
        {
            new() { Priority = 10, CodeType = "CPT", CodePattern = "99213" },
        },
    };
}

