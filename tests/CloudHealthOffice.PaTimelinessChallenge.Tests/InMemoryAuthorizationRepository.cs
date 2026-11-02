using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using AuthorizationService.Services.Cms0057;
using AuthorizationService.Services.Retention;

namespace CloudHealthOffice.PaTimelinessChallenge.Tests;

/// <summary>
/// The parts of the authorization store a PA Timeliness Challenge history run
/// touches, in memory, initializing records exactly as the MongoDB repository
/// does (deadline policy, then the reporting ledger, then facts through the
/// endpoint's own validator).
/// </summary>
public sealed class InMemoryAuthorizationRepository : IAuthorizationRepository
{
    private readonly ConcurrentDictionary<string, Authorization> _byId = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;

    public InMemoryAuthorizationRepository(Func<DateTime> now) => _now = now;

    public IReadOnlyCollection<Authorization> All => _byId.Values.ToList();

    public Task<Authorization?> GetByIdAsync(string id) => Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<Authorization?> GetByAuthorizationNumberAsync(string authorizationNumber) =>
        Task.FromResult(_byId.Values.FirstOrDefault(a => a.AuthorizationNumber == authorizationNumber));

    public Task<Authorization> CreateAsync(Authorization authorization)
    {
        authorization.Id ??= Guid.NewGuid().ToString();
        authorization.Revision = 0;
        AuthorizationDeadlinePolicy.Initialize(authorization);
        PaReportingEvidence.Initialize(authorization, _now());
        _byId[authorization.Id] = authorization;
        return Task.FromResult(authorization);
    }

    public Task<Authorization> UpdateAsync(Authorization authorization)
    {
        // As the MongoDB repository does: keep the ledger and capture a new decision as a fact.
        if (_byId.TryGetValue(authorization.Id, out var previous) && !ReferenceEquals(previous, authorization))
            PaReportingEvidence.PreserveAndCapture(authorization, previous, _now());
        authorization.Revision++;
        _byId[authorization.Id] = authorization;
        return Task.FromResult(authorization);
    }

    public Task<PaReportingFact?> RecordReportingFactAsync(string id, RecordPaReportingFact command, string actor, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(id, out var a)) return Task.FromResult<PaReportingFact?>(null);
        var fact = PaReportingEvidence.Prepare(a, command, actor, _now());
        lock (a)
        {
            if (!a.ReportingLedger!.Facts.Any(f => f.EventId == fact.EventId && f.SourceSystem == fact.SourceSystem))
                a.ReportingLedger.Facts.Add(fact);
        }
        return Task.FromResult<PaReportingFact?>(fact);
    }

    public Task ExtendDecisionDeadlineAsync(Authorization authorization, AuthorizationDecisionExtension extension,
        PaReportingFact? fact, CancellationToken ct = default)
    {
        authorization.DecisionDueAt = extension.ExtendedDueAt;
        authorization.DecisionExtension = extension;
        authorization.LastUpdatedDate = extension.GrantedAt;
        authorization.LastUpdatedBy = extension.GrantedBy;
        if (fact is not null) authorization.ReportingLedger!.Facts.Add(fact);
        authorization.Revision++;
        _byId[authorization.Id] = authorization;
        return Task.CompletedTask;
    }

    public Task<IEnumerable<Authorization>> ListActivityBetweenAsync(DateTime from, DateTime to) =>
        Task.FromResult<IEnumerable<Authorization>>(_byId.Values.ToList());

    public async IAsyncEnumerable<Authorization> StreamReportingCohortAsync(DateTime from, DateTime toExclusive,
        LineOfBusiness lineOfBusiness, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var a in _byId.Values.Where(a => a.ReportingLedger is { } l && l.ReceivedAt >= from && l.ReceivedAt < toExclusive
            && l.LineOfBusiness == lineOfBusiness))
        {
            ct.ThrowIfCancellationRequested();
            yield return a;
        }
        await Task.CompletedTask;
    }

    public Task<IEnumerable<Authorization>> GetOpenAuthorizationsAsync(string? tenantId = null) =>
        Task.FromResult<IEnumerable<Authorization>>(_byId.Values.Where(a => a.Status.IsOpen()).ToList());

    public Task<IEnumerable<Authorization>> SearchAsync(string? memberId, string? providerNPI, DateTime? serviceDateFrom,
        DateTime? serviceDateTo, AuthorizationStatus? status, LineOfBusiness? lineOfBusiness, int page, int pageSize) =>
        throw new NotSupportedException();

    public Task<AuthorizationsSummary> GetAuthorizationsSummaryAsync(DateTime from, DateTime to, LineOfBusiness? lineOfBusiness) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string id) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> ListTenantIdsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<Authorization>> FindRetentionCandidatesAsync(string tenantId, DateTime anchorCutoffUtc, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Authorization>>([]);

    public Task<bool> PurgeIfStillEligibleAsync(string tenantId, string id, AuthorizationStatus expectedStatus, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<bool> RaiseSlaEscalationAsync(string tenantId, string id, SlaEscalationLevel level, DateTime escalatedAtUtc, CancellationToken ct = default) =>
        Task.FromResult(false);
}
