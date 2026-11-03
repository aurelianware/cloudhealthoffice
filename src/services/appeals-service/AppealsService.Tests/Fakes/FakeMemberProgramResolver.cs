using AppealsService.Models;
using AppealsService.Services;
using CloudHealthOffice.PlanConfiguration;

namespace AppealsService.Tests.Fakes;

/// <summary>Member → program answers for controller tests, keyed by member id.</summary>
public sealed class FakeMemberProgramResolver : IMemberProgramResolver
{
    private readonly Dictionary<string, ProgramDescriptor> _programs = new(StringComparer.Ordinal);

    /// <summary>Set to make every lookup throw, as an unreachable coverage-service would.</summary>
    public bool Unavailable { get; set; }

    public void Add(string memberId, ProgramDescriptor program) => _programs[memberId] = program;

    public void Clear()
    {
        _programs.Clear();
        Unavailable = false;
    }

    public Task<ProgramDescriptor?> ResolveAsync(
        string tenantId, string memberId, LineOfBusiness lineOfBusiness, DateTime asOf, CancellationToken ct)
    {
        if (Unavailable) throw new HttpRequestException("coverage-service unavailable");
        return Task.FromResult(
            _programs.TryGetValue(memberId, out var program)
            && string.Equals(program.LineOfBusiness, HttpMemberProgramResolver.ProgramLineOfBusiness(lineOfBusiness), StringComparison.Ordinal)
                ? program
                : null);
    }
}
