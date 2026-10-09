using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.BenefitEngine.Tests;

/// <summary>
/// REV rules (<c>CodeType = "REV"</c>, as in the shipped
/// system-defaults.json "Inpatient Hospital" 0100–0219 rule) match the
/// line's revenue code. Claim lines always reach the resolver with code type
/// "CPT", and an 837I accommodation line has no HCPCS, so the rule was never
/// matched: the inpatient stay fell back to POS inference on CLM05-1 = "11"
/// and resolved as an office visit (found by the golden-path 837I DRG test).
/// </summary>
public class ServiceCategoryResolverRevenueCodeTests
{
    private static readonly ServiceCategoryMapping Inpatient = new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-a",
        ServiceTypeCode = "Inpatient Hospital",
        ServiceTypeDescription = "Inpatient Hospital",
        Rules = [new ProcedureCodeRule { Priority = 20, CodeType = "REV", CodePattern = "0100", CodeRangeEnd = "0219" }],
    };

    private static readonly ServiceCategoryMapping Surgery = new()
    {
        Id = Guid.NewGuid(),
        TenantId = "tenant-a",
        ServiceTypeCode = "Surgery",
        ServiceTypeDescription = "Surgery",
        Rules = [new ProcedureCodeRule { Priority = 10, CodeType = "CPT", CodePattern = "10000", CodeRangeEnd = "69999" }],
    };

    private static Task<ServiceCategoryMatch?> Resolve(string procedureCode, string? revenueCode) =>
        new ServiceCategoryResolver(new Repo(Inpatient, Surgery), NullLogger<ServiceCategoryResolver>.Instance)
            .ResolveAsync("tenant-a", Guid.NewGuid(), new DateOnly(2026, 2, 10),
                procedureCode, "CPT", "11", Array.Empty<string>(), revenueCode);

    [Theory]
    [InlineData("0120")]
    [InlineData("120")] // three-digit form of the same revenue code
    public async Task RevenueCodeOnlyLine_MatchesRevRule(string revenueCode)
    {
        var match = await Resolve(procedureCode: "", revenueCode);

        Assert.Equal("Inpatient Hospital", match!.ServiceTypeCode);
        Assert.Equal("TenantDefault", match.MatchedBy);
    }

    [Fact]
    public async Task RevenueCodeOutsideRange_DoesNotMatchRevRule()
    {
        var match = await Resolve(procedureCode: "", revenueCode: "0250");

        Assert.Equal("SystemDefault", match!.MatchedBy);
    }

    [Fact]
    public async Task CptRules_StillMatchTheProcedureCode()
    {
        var match = await Resolve(procedureCode: "27447", revenueCode: "0360");

        Assert.Equal("Surgery", match!.ServiceTypeCode);
    }

    [Fact]
    public async Task LineWithoutRevenueCode_NeverMatchesRevRule()
    {
        var match = await Resolve(procedureCode: "0120", revenueCode: null);

        Assert.Equal("SystemDefault", match!.MatchedBy);
    }

    private sealed class Repo : IServiceCategoryMappingRepository
    {
        private readonly IReadOnlyList<ServiceCategoryMapping> _mappings;
        public Repo(params ServiceCategoryMapping[] mappings) => _mappings = mappings;
        public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
            => Task.FromResult(benefitPlanId is null ? _mappings : Array.Empty<ServiceCategoryMapping>());
    }
}
