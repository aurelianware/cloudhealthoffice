using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CloudHealthOffice.Infrastructure.Security;
using SponsorService.Contracts;

namespace SponsorService.Tests.Fakes;

/// <summary>
/// The contract store in memory, with the same revision guard as Mongo: a
/// replace lands only while the stored revision is still the one the write was
/// based on. Documents are round-tripped through JSON so that a caller holding
/// a record cannot change what is stored by mutating its own copy, which is how
/// the real store behaves and what keeps the concurrency tests honest.
///
/// <para>
/// The revision is held beside the JSON rather than in it, because
/// <c>Revision</c> is <c>[JsonIgnore]</c>: it is how the store guards a write
/// and never part of what an API hands back, so round-tripping a record
/// through JSON drops it. Keeping it outside is what the Mongo store does too,
/// where the revision lives in the document's own field and the guard is a
/// filter on it.
/// </para>
/// </summary>
public sealed class InMemoryContractStore : IContractStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, Stored> _policies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stored> _policyTerms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stored> _terms = new(StringComparer.Ordinal);

    /// <summary>A document as it sits in the store, with the revision it is at.</summary>
    private sealed record Stored(string Json, long Revision);

    /// <summary>
    /// Runs once, just before the next policy replace. A concurrent edit is an
    /// edit that lands between a caller's read and its write, so a test that
    /// bumps the revision beforehand proves nothing: the caller would read the
    /// new revision and its write would match. This is where the other writer
    /// goes.
    /// </summary>
    public Action? BeforeReplacePolicy
    {
        get => _beforeReplacePolicy;
        set => _beforeReplacePolicy = value;
    }

    /// <summary>The same interleaving point for a policy's terms.</summary>
    public Action? BeforeReplacePolicyTerms
    {
        get => _beforeReplacePolicyTerms;
        set => _beforeReplacePolicyTerms = value;
    }

    /// <summary>The same interleaving point for a contract term.</summary>
    public Action? BeforeReplaceTerm
    {
        get => _beforeReplaceTerm;
        set => _beforeReplaceTerm = value;
    }

    private Action? _beforeReplacePolicy;
    private Action? _beforeReplacePolicyTerms;
    private Action? _beforeReplaceTerm;

    /// <summary>Runs the other writer, and only once, so the retry can land.</summary>
    private static void Interleave(ref Action? writer)
    {
        var once = writer;
        writer = null;
        once?.Invoke();
    }

    /// <summary>Changes a stored record behind the caller's back, to make a concurrent edit.</summary>
    public void BumpPolicyRevision(string tenantId, string id)
    {
        var key = Key(tenantId, id);
        var stored = _policies[key];
        _policies[key] = stored with { Revision = stored.Revision + 1 };
    }

    public void BumpPolicyTermsRevision(string tenantId, string id)
    {
        var key = Key(tenantId, id);
        var stored = _policyTerms[key];
        _policyTerms[key] = stored with { Revision = stored.Revision + 1 };
    }

    public void BumpTermRevision(string tenantId, string id)
    {
        var key = Key(tenantId, id);
        var stored = _terms[key];
        _terms[key] = stored with { Revision = stored.Revision + 1 };
    }

    public Task<IReadOnlyList<SponsorPolicy>> ListPoliciesAsync(string tenantId, string groupNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SponsorPolicy>>(All<SponsorPolicy>(_policies)
            .Where(p => p.TenantId == tenantId && p.GroupNumber == groupNumber)
            .OrderBy(p => p.PolicyNumber, StringComparer.Ordinal)
            .ToList());

    public Task<SponsorPolicy?> GetPolicyAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(Read<SponsorPolicy>(_policies, Key(tenantId, id)));

    public Task<SponsorPolicy> InsertPolicyAsync(SponsorPolicy policy, CancellationToken ct = default)
    {
        policy.Revision = 0;
        _policies[Key(policy.TenantId, policy.Id)] = new Stored(JsonSerializer.Serialize(policy, Json), 0);
        return Task.FromResult(policy);
    }

    public Task<bool> ReplacePolicyAsync(SponsorPolicy policy, long expectedRevision, CancellationToken ct = default)
    {
        Interleave(ref _beforeReplacePolicy);
        var key = Key(policy.TenantId, policy.Id);
        if (!_policies.TryGetValue(key, out var stored) || stored.Revision != expectedRevision)
        {
            policy.Revision = expectedRevision;
            return Task.FromResult(false);
        }
        policy.Revision = expectedRevision + 1;
        _policies[key] = new Stored(JsonSerializer.Serialize(policy, Json), policy.Revision);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PolicyTerms>> ListPolicyTermsAsync(string tenantId, string policyId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PolicyTerms>>(All<PolicyTerms>(_policyTerms)
            .Where(t => t.TenantId == tenantId && t.PolicyId == policyId)
            .OrderBy(t => t.PeriodStart)
            .ToList());

    public Task<PolicyTerms?> GetPolicyTermsAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(Read<PolicyTerms>(_policyTerms, Key(tenantId, id)));

    public Task<PolicyTerms> InsertPolicyTermsAsync(PolicyTerms terms, CancellationToken ct = default)
    {
        terms.Revision = 0;
        _policyTerms[Key(terms.TenantId, terms.Id)] = new Stored(JsonSerializer.Serialize(terms, Json), 0);
        return Task.FromResult(terms);
    }

    public Task<bool> ReplacePolicyTermsAsync(PolicyTerms terms, long expectedRevision, CancellationToken ct = default)
    {
        Interleave(ref _beforeReplacePolicyTerms);
        var key = Key(terms.TenantId, terms.Id);
        if (!_policyTerms.TryGetValue(key, out var stored) || stored.Revision != expectedRevision)
        {
            terms.Revision = expectedRevision;
            return Task.FromResult(false);
        }
        terms.Revision = expectedRevision + 1;
        _policyTerms[key] = new Stored(JsonSerializer.Serialize(terms, Json), terms.Revision);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ContractTerm>> ListTermsAsync(string tenantId, string groupNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContractTerm>>(All<ContractTerm>(_terms)
            .Where(t => t.TenantId == tenantId && t.GroupNumber == groupNumber)
            .OrderBy(t => t.PeriodStart)
            .ToList());

    public Task<ContractTerm?> GetTermAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(Read<ContractTerm>(_terms, Key(tenantId, id)));

    public Task<ContractTerm> InsertTermAsync(ContractTerm term, CancellationToken ct = default)
    {
        term.Revision = 0;
        _terms[Key(term.TenantId, term.Id)] = new Stored(JsonSerializer.Serialize(term, Json), 0);
        return Task.FromResult(term);
    }

    public Task<bool> ReplaceTermAsync(ContractTerm term, long expectedRevision, CancellationToken ct = default)
    {
        Interleave(ref _beforeReplaceTerm);
        var key = Key(term.TenantId, term.Id);
        if (!_terms.TryGetValue(key, out var stored) || stored.Revision != expectedRevision)
        {
            term.Revision = expectedRevision;
            return Task.FromResult(false);
        }
        term.Revision = expectedRevision + 1;
        _terms[key] = new Stored(JsonSerializer.Serialize(term, Json), term.Revision);
        return Task.FromResult(true);
    }

    private static string Key(string tenantId, string id) => $"{tenantId}|{id}";

    private static T? Read<T>(Dictionary<string, Stored> from, string key) where T : class =>
        from.TryGetValue(key, out var stored) ? Hydrate<T>(stored) : null;

    private static IEnumerable<T> All<T>(Dictionary<string, Stored> from) where T : class =>
        from.Values.Select(Hydrate<T>);

    private static T Hydrate<T>(Stored stored) where T : class
    {
        var document = JsonSerializer.Deserialize<T>(stored.Json, Json)!;
        switch (document)
        {
            case SponsorPolicy policy: policy.Revision = stored.Revision; break;
            case PolicyTerms terms: terms.Revision = stored.Revision; break;
            case ContractTerm term: term.Revision = stored.Revision; break;
        }
        return document;
    }
}

/// <summary>A clock that does not move unless a test moves it.</summary>
public sealed class FixedClock(DateTime now) : TimeProvider
{
    public DateTime Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
}

/// <summary>A caller with the permissions a test gives it, in one tenant.</summary>
public sealed class TestActor(string userId, string tenantId, params string[] permissions) : ICurrentActor
{
    private readonly HashSet<string> _permissions = new(permissions, StringComparer.OrdinalIgnoreCase);
    public bool IsAuthenticated => true;
    public string UserId { get; } = userId;
    public string? DisplayName => UserId;
    public string? Email => null;
    public string TenantId { get; } = tenantId;
    public bool IsService => false;
    public IReadOnlyCollection<string> Roles { get; } = Array.Empty<string>();
    public bool HasPermission(string permission) => _permissions.Contains(permission);
}
