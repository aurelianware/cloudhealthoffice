using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Rules.Platform;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.E2E;

/// <summary>
/// Integration smoke tests for the TX platform seed rules running through
/// the full PriorAuthRuleEngineService pipeline with real rule implementations.
/// No external dependencies — uses an in-memory rule repository.
/// </summary>
public class PriorAuthRuleEngineIntegrationTests : IAsyncLifetime
{
    private readonly InMemoryPaRuleRepository _repo = new();
    private PriorAuthRuleEngineService _engine = null!;

    public Task InitializeAsync()
    {
        var seedRules = TxMedicaidSeedRules.GetAll();
        foreach (var rule in seedRules)
            _repo.Store(rule);

        _engine = new PriorAuthRuleEngineService(
            _repo,
            new IPaRule[]
            {
                new TxGoldCardExemptionRule(),
                new ProcedureRequiresAuthRule(),
                new QuantityLimitRule(),
                new DiagnosisRequiredRule(),
                new ProviderTypeExemptionRule(),
                new MemberAgeLimitRule()
            },
            Options.Create(new PriorAuthRuleEngineOptions()),
            NullLogger<PriorAuthRuleEngineService>.Instance);

        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── 1. Seed rules integrity ──────────────────────────────────

    [Fact]
    public void SeedRules_AllTxPlatformRules_PresentAfterSeeding()
    {
        var rules = TxMedicaidSeedRules.GetAll();

        Assert.Equal(10, rules.Count);

        var expectedIds = new[]
        {
            "TX-STAR-REG-001", "TX-STAR-QTY-001", "TX-STAR-QTY-002",
            "TX-STAR-PA-001", "TX-STAR-PCP-001",
            "TX-STARPLUS-REG-001", "TX-STARPLUS-PA-001", "TX-STARPLUS-DX-001",
            "TX-STARKIDS-REG-001", "TX-STARKIDS-AGE-001"
        };

        foreach (var id in expectedIds)
            Assert.Contains(rules, r => r.RuleId == id);

        Assert.All(rules, r =>
        {
            Assert.Equal("TX", r.StateCode);
            Assert.Equal(RuleScope.Platform, r.Scope);
            Assert.Null(r.TenantId);
            Assert.True(r.IsEnabled);
        });

        // Idempotency: calling GetAll again yields the same count
        var secondBatch = TxMedicaidSeedRules.GetAll();
        Assert.Equal(rules.Count, secondBatch.Count);
    }

    // ── 2. Gold card exemption ───────────────────────────────────

    [Fact]
    public async Task EvaluateAsync_TxStar_GoldCardProvider_ReturnsApprove()
    {
        var context = MakeStarContext() with
        {
            ProviderHistory = new ProviderApprovalHistory
            {
                Npi = "1234567890",
                LookbackDays = 180,
                TotalDecisions = 25,
                ApprovedDecisions = 24 // 96% ≥ 90% threshold, 25 ≥ 20 min decisions
            }
        };

        var result = await _engine.EvaluateAsync(context);

        Assert.Equal(PaDecisionOutcome.Approve, result.Outcome);
        Assert.Equal("TX-STAR-REG-001", result.FiringRuleId);
        Assert.Contains("Gold Card", result.FiringRuleName);
    }

    // ── 3. Chiropractic under visit limit ────────────────────────

    [Fact]
    public async Task EvaluateAsync_TxStar_ChiropracticUnderVisitLimit_ReturnsApprove()
    {
        var context = MakeStarContext(
            procedures: ["98941"],
            memberHistory: new MemberAuthHistory
            {
                MemberId = "MBR-001",
                BenefitPeriod = "2026",
                ProcedureCodes = ["98941"],
                AuthorisedVisits = 15, // 15 + 1 = 16 ≤ 20 limit
                AuthorisedUnits = 0,
                AuthorisedAmount = 0m
            });

        var result = await _engine.EvaluateAsync(context);

        Assert.Equal(PaDecisionOutcome.Approve, result.Outcome);
        Assert.Equal("TX-STAR-QTY-001", result.FiringRuleId);
    }

    // ── 4. Chiropractic over visit limit ─────────────────────────

    [Fact]
    public async Task EvaluateAsync_TxStar_ChiropracticOverVisitLimit_ReturnsPend()
    {
        var context = MakeStarContext(
            procedures: ["98941"],
            memberHistory: new MemberAuthHistory
            {
                MemberId = "MBR-001",
                BenefitPeriod = "2026",
                ProcedureCodes = ["98941"],
                AuthorisedVisits = 20, // 20 + 1 = 21 > 20 limit
                AuthorisedUnits = 0,
                AuthorisedAmount = 0m
            });

        var result = await _engine.EvaluateAsync(context);

        Assert.Equal(PaDecisionOutcome.Pend, result.Outcome);
        Assert.Equal("TX-STAR-QTY-001", result.FiringRuleId);
    }

    // ── 5. STARKids EPSDT under-21 ───────────────────────────────

    [Fact]
    public async Task EvaluateAsync_TxStarKids_MemberUnder21_ReturnsApprove()
    {
        var context = new PaRuleContext
        {
            TenantId = "txmco01",
            StateCode = "TX",
            Lob = PaLineOfBusiness.Medicaid,
            Program = "STARKids",
            RequestingProviderNpi = "1234567890",
            ServicingProviderNpi = "1234567890",
            MemberId = "MBR-002",
            ServiceDate = DateOnly.FromDateTime(DateTime.Today),
            ProcedureCodes = ["99213"],
            DiagnosisCodes = [],
            EstimatedCost = 150m,
            MemberDateOfBirth = DateOnly.FromDateTime(DateTime.Today.AddYears(-10))
        };

        var result = await _engine.EvaluateAsync(context);

        Assert.Equal(PaDecisionOutcome.Approve, result.Outcome);
        Assert.Equal("TX-STARKIDS-AGE-001", result.FiringRuleId);
        Assert.Contains("EPSDT", result.FiringRuleName);
    }

    // ── 6. No rules for Exchange LOB ─────────────────────────────

    [Fact]
    public async Task EvaluateAsync_NoRulesForExchange_ReturnsPend()
    {
        var context = new PaRuleContext
        {
            TenantId = "txmco01",
            StateCode = "TX",
            Lob = PaLineOfBusiness.Exchange,
            Program = null,
            RequestingProviderNpi = "1234567890",
            ServicingProviderNpi = "1234567890",
            MemberId = "MBR-003",
            ServiceDate = DateOnly.FromDateTime(DateTime.Today),
            ProcedureCodes = ["99213"],
            DiagnosisCodes = [],
            EstimatedCost = 100m
        };

        var result = await _engine.EvaluateAsync(context);

        Assert.Equal(PaDecisionOutcome.Pend, result.Outcome);
        Assert.Equal("NoRulesConfigured", result.FiringRuleId);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static PaRuleContext MakeStarContext(
        IReadOnlyList<string>? procedures = null,
        MemberAuthHistory? memberHistory = null) => new()
    {
        TenantId = "txmco01",
        StateCode = "TX",
        Lob = PaLineOfBusiness.Medicaid,
        Program = "STAR",
        RequestingProviderNpi = "1234567890",
        ServicingProviderNpi = "1234567890",
        MemberId = "MBR-001",
        ServiceDate = DateOnly.FromDateTime(DateTime.Today),
        ProcedureCodes = procedures ?? ["99213"],
        DiagnosisCodes = [],
        EstimatedCost = 200m,
        MemberHistory = memberHistory
    };

    // ── In-memory repository ────────────────────────────────────

    private sealed class InMemoryPaRuleRepository : IPaRuleRepository
    {
        private readonly List<PaRuleDocument> _store = [];

        public void Store(PaRuleDocument rule) => _store.Add(rule);

        public Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(
            RuleSetKey key, CancellationToken ct = default)
        {
            var results = _store
                .Where(r => r.StateCode == key.StateCode
                         && r.Lob == key.Lob
                         && r.Program == key.Program
                         && r.TenantId == key.TenantId
                         && r.IsEnabled)
                .OrderBy(r => (int)r.Category)
                .ThenBy(r => r.Priority)
                .ToList();

            return Task.FromResult<IReadOnlyList<PaRuleDocument>>(results);
        }

        public Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default)
        {
            _store.RemoveAll(r => r.RuleId == rule.RuleId && r.StateCode == rule.StateCode);
            _store.Add(rule);
            return Task.CompletedTask;
        }

        public Task BulkUpsertAsync(
            IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
        {
            foreach (var rule in rules)
            {
                _store.RemoveAll(r => r.RuleId == rule.RuleId && r.StateCode == rule.StateCode);
                _store.Add(rule);
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string ruleId, string stateCode, CancellationToken ct = default)
        {
            _store.RemoveAll(r => r.RuleId == ruleId && r.StateCode == stateCode);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PaRuleDocument>> ListAsync(
            string? tenantId = null, string? stateCode = null, CancellationToken ct = default)
        {
            var results = _store.AsEnumerable();
            if (tenantId != null) results = results.Where(r => r.TenantId == tenantId);
            if (stateCode != null) results = results.Where(r => r.StateCode == stateCode);
            return Task.FromResult<IReadOnlyList<PaRuleDocument>>(results.ToList());
        }
    }
}
