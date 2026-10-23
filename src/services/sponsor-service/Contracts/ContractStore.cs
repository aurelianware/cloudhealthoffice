using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace SponsorService.Contracts;

/// <summary>
/// Where sponsor policies and contract terms are kept. Every read and write is
/// scoped by tenant, as the sponsor store is.
/// </summary>
public interface IContractStore
{
    Task<IReadOnlyList<SponsorPolicy>> ListPoliciesAsync(string tenantId, string groupNumber, CancellationToken ct = default);

    /// <summary>
    /// Every policy of the tenant carrying this policy number, whichever group
    /// it belongs to. A policy number is unique within a group and nothing
    /// makes it unique across them, so this answers a list rather than one
    /// policy and the caller decides what more than one means.
    /// </summary>
    Task<IReadOnlyList<SponsorPolicy>> FindPoliciesByNumberAsync(string tenantId, string policyNumber, CancellationToken ct = default);
    Task<SponsorPolicy?> GetPolicyAsync(string tenantId, string id, CancellationToken ct = default);
    Task<SponsorPolicy> InsertPolicyAsync(SponsorPolicy policy, CancellationToken ct = default);

    /// <summary>
    /// Replaces the policy while the stored revision is still the one the
    /// caller read; false means it changed underneath and nothing was written.
    /// </summary>
    Task<bool> ReplacePolicyAsync(SponsorPolicy policy, long expectedRevision, CancellationToken ct = default);

    Task<IReadOnlyList<PolicyTerms>> ListPolicyTermsAsync(string tenantId, string policyId, CancellationToken ct = default);
    Task<PolicyTerms?> GetPolicyTermsAsync(string tenantId, string id, CancellationToken ct = default);
    Task<PolicyTerms> InsertPolicyTermsAsync(PolicyTerms terms, CancellationToken ct = default);
    Task<bool> ReplacePolicyTermsAsync(PolicyTerms terms, long expectedRevision, CancellationToken ct = default);

    Task<IReadOnlyList<ContractTerm>> ListTermsAsync(string tenantId, string groupNumber, CancellationToken ct = default);
    Task<ContractTerm?> GetTermAsync(string tenantId, string id, CancellationToken ct = default);
    Task<ContractTerm> InsertTermAsync(ContractTerm term, CancellationToken ct = default);
    Task<bool> ReplaceTermAsync(ContractTerm term, long expectedRevision, CancellationToken ct = default);
}

/// <summary>
/// MongoDB, collections <c>sponsor-policies</c>, <c>sponsor-policy-terms</c>
/// and <c>sponsor-contract-terms</c>. All are revision-guarded the same way the
/// sponsor record is: a replace succeeds only while the stored revision is
/// still the one the write was based on, so a concurrent edit fails rather
/// than being silently undone.
/// </summary>
public sealed class ContractStoreMongo : IContractStore
{
    private readonly IMongoCollection<SponsorPolicy> _policies;
    private readonly IMongoCollection<PolicyTerms> _policyTerms;
    private readonly IMongoCollection<ContractTerm> _terms;

    public ContractStoreMongo(IMongoDatabase database, IConfiguration configuration)
    {
        _policies = database.GetCollection<SponsorPolicy>(
            configuration["SponsorContracts:PolicyCollection"] ?? "sponsor-policies");
        _policyTerms = database.GetCollection<PolicyTerms>(
            configuration["SponsorContracts:PolicyTermCollection"] ?? "sponsor-policy-terms");
        _terms = database.GetCollection<ContractTerm>(
            configuration["SponsorContracts:TermCollection"] ?? "sponsor-contract-terms");
    }

    public async Task<IReadOnlyList<SponsorPolicy>> ListPoliciesAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var b = Builders<SponsorPolicy>.Filter;
        var found = await _policies.Find(b.Eq(p => p.TenantId, tenantId) & b.Eq(p => p.GroupNumber, groupNumber))
            .ToListAsync(ct);
        return found.OrderBy(p => p.PolicyNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<SponsorPolicy>> FindPoliciesByNumberAsync(
        string tenantId, string policyNumber, CancellationToken ct = default)
    {
        var b = Builders<SponsorPolicy>.Filter;
        var found = await _policies.Find(b.Eq(p => p.TenantId, tenantId) & b.Eq(p => p.PolicyNumber, policyNumber))
            .ToListAsync(ct);
        return found.OrderBy(p => p.GroupNumber, StringComparer.Ordinal).ToList();
    }

    public async Task<SponsorPolicy?> GetPolicyAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var b = Builders<SponsorPolicy>.Filter;
        return await _policies.Find(b.Eq(p => p.TenantId, tenantId) & b.Eq(p => p.Id, id)).FirstOrDefaultAsync(ct);
    }

    public async Task<SponsorPolicy> InsertPolicyAsync(SponsorPolicy policy, CancellationToken ct = default)
    {
        policy.Revision = 0;
        await _policies.InsertOneAsync(policy, cancellationToken: ct);
        return policy;
    }

    public async Task<bool> ReplacePolicyAsync(SponsorPolicy policy, long expectedRevision, CancellationToken ct = default)
    {
        var b = Builders<SponsorPolicy>.Filter;
        policy.Revision = expectedRevision + 1;
        var result = await _policies.ReplaceOneAsync(
            b.Eq(p => p.TenantId, policy.TenantId) & b.Eq(p => p.Id, policy.Id) & PolicyRevisionIs(expectedRevision),
            policy, cancellationToken: ct);
        if (result.MatchedCount != 0) return true;
        policy.Revision = expectedRevision;
        return false;
    }

    public async Task<IReadOnlyList<PolicyTerms>> ListPolicyTermsAsync(string tenantId, string policyId, CancellationToken ct = default)
    {
        var b = Builders<PolicyTerms>.Filter;
        var found = await _policyTerms.Find(b.Eq(t => t.TenantId, tenantId) & b.Eq(t => t.PolicyId, policyId)).ToListAsync(ct);
        return found.OrderBy(t => t.PeriodStart).ToList();
    }

    public async Task<PolicyTerms?> GetPolicyTermsAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var b = Builders<PolicyTerms>.Filter;
        return await _policyTerms.Find(b.Eq(t => t.TenantId, tenantId) & b.Eq(t => t.Id, id)).FirstOrDefaultAsync(ct);
    }

    public async Task<PolicyTerms> InsertPolicyTermsAsync(PolicyTerms terms, CancellationToken ct = default)
    {
        terms.Revision = 0;
        await _policyTerms.InsertOneAsync(terms, cancellationToken: ct);
        return terms;
    }

    public async Task<bool> ReplacePolicyTermsAsync(PolicyTerms terms, long expectedRevision, CancellationToken ct = default)
    {
        var b = Builders<PolicyTerms>.Filter;
        terms.Revision = expectedRevision + 1;
        var result = await _policyTerms.ReplaceOneAsync(
            b.Eq(t => t.TenantId, terms.TenantId) & b.Eq(t => t.Id, terms.Id) & PolicyTermsRevisionIs(expectedRevision),
            terms, cancellationToken: ct);
        if (result.MatchedCount != 0) return true;
        terms.Revision = expectedRevision;
        return false;
    }

    public async Task<IReadOnlyList<ContractTerm>> ListTermsAsync(string tenantId, string groupNumber, CancellationToken ct = default)
    {
        var b = Builders<ContractTerm>.Filter;
        var found = await _terms.Find(b.Eq(t => t.TenantId, tenantId) & b.Eq(t => t.GroupNumber, groupNumber)).ToListAsync(ct);
        return found.OrderBy(t => t.PeriodStart).ToList();
    }

    public async Task<ContractTerm?> GetTermAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var b = Builders<ContractTerm>.Filter;
        return await _terms.Find(b.Eq(t => t.TenantId, tenantId) & b.Eq(t => t.Id, id)).FirstOrDefaultAsync(ct);
    }

    public async Task<ContractTerm> InsertTermAsync(ContractTerm term, CancellationToken ct = default)
    {
        term.Revision = 0;
        await _terms.InsertOneAsync(term, cancellationToken: ct);
        return term;
    }

    public async Task<bool> ReplaceTermAsync(ContractTerm term, long expectedRevision, CancellationToken ct = default)
    {
        var b = Builders<ContractTerm>.Filter;
        term.Revision = expectedRevision + 1;
        var result = await _terms.ReplaceOneAsync(
            b.Eq(t => t.TenantId, term.TenantId) & b.Eq(t => t.Id, term.Id) & TermRevisionIs(expectedRevision),
            term, cancellationToken: ct);
        if (result.MatchedCount != 0) return true;
        term.Revision = expectedRevision;
        return false;
    }

    // The document still has the revision the write was based on. A 0 also
    // matches a document written before the field existed, as the sponsor
    // store's guard does. Written out per type rather than generically, so the
    // field is named through the property and not a string.

    private static FilterDefinition<SponsorPolicy> PolicyRevisionIs(long expected)
    {
        var b = Builders<SponsorPolicy>.Filter;
        return expected == 0
            ? b.Or(b.Eq(p => p.Revision, 0L), b.Exists(p => p.Revision, false))
            : b.Eq(p => p.Revision, expected);
    }

    private static FilterDefinition<PolicyTerms> PolicyTermsRevisionIs(long expected)
    {
        var b = Builders<PolicyTerms>.Filter;
        return expected == 0
            ? b.Or(b.Eq(t => t.Revision, 0L), b.Exists(t => t.Revision, false))
            : b.Eq(t => t.Revision, expected);
    }

    private static FilterDefinition<ContractTerm> TermRevisionIs(long expected)
    {
        var b = Builders<ContractTerm>.Filter;
        return expected == 0
            ? b.Or(b.Eq(t => t.Revision, 0L), b.Exists(t => t.Revision, false))
            : b.Eq(t => t.Revision, expected);
    }
}
