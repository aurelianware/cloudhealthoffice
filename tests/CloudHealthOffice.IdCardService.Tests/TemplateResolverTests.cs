using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.IdCardService.Tests;

public class TemplateResolverTests
{
    private readonly InMemoryIdCardTemplateRepository _repo = new();
    private readonly TemplateResolver _resolver;

    public TemplateResolverTests()
    {
        _resolver = new TemplateResolver(_repo, NullLogger<TemplateResolver>.Instance);
    }

    [Fact]
    public async Task SpecificSponsorPlan_Wins()
    {
        await _repo.UpsertAsync(TestFixtures.GlobalDefault());
        await _repo.UpsertAsync(TestFixtures.SponsorDefault(TestFixtures.TenantId, TestFixtures.GroupNumber));
        await _repo.UpsertAsync(TestFixtures.SponsorPlan(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId));

        var t = await _resolver.ResolveAsync(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId, "en-US");

        Assert.NotNull(t);
        Assert.Equal($"tmpl-{TestFixtures.GroupNumber}-{TestFixtures.PlanId}", t!.Id);
    }

    [Fact]
    public async Task NoSpecific_FallsBackToSponsorDefault()
    {
        await _repo.UpsertAsync(TestFixtures.GlobalDefault());
        await _repo.UpsertAsync(TestFixtures.SponsorDefault(TestFixtures.TenantId, TestFixtures.GroupNumber));

        var t = await _resolver.ResolveAsync(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId, "en-US");

        Assert.NotNull(t);
        Assert.Equal($"tmpl-{TestFixtures.GroupNumber}", t!.Id);
    }

    [Fact]
    public async Task NoSponsorNoPlanTemplate_FallsBackToGlobal_Succeeds()
    {
        // Only the global default is seeded. Phase-1 policy says the global
        // must always exist; this asserts the fall-through path.
        await _repo.UpsertAsync(TestFixtures.GlobalDefault());

        var t = await _resolver.ResolveAsync(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId, "en-US");

        Assert.NotNull(t);
        Assert.True(t!.IsGlobalDefault);
    }

    [Fact]
    public async Task MissingGlobal_ReturnsNull()
    {
        // Deployment misconfiguration — the startup health check should catch
        // this, but at runtime we surface null so the orchestrator can emit
        // NO_TEMPLATE_AVAILABLE.
        var t = await _resolver.ResolveAsync(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId, "en-US");
        Assert.Null(t);
    }

    [Fact]
    public async Task SpecificMissingLanguage_FallsThroughToGlobalSupporting()
    {
        // Sponsor-plan template supports only en-US; a request in es-US must
        // fall through to the global template, which supports both.
        await _repo.UpsertAsync(TestFixtures.GlobalDefault());
        await _repo.UpsertAsync(TestFixtures.SponsorPlan(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId));

        var t = await _resolver.ResolveAsync(TestFixtures.TenantId, TestFixtures.GroupNumber, TestFixtures.PlanId, "es-US");

        Assert.NotNull(t);
        Assert.True(t!.IsGlobalDefault);
    }
}
