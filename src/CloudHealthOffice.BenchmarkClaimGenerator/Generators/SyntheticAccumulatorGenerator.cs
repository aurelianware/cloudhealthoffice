using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Generators;

/// <summary>
/// Generates synthetic accumulator balances (deductible/OOP tracking) for members.
/// Most Texas Medicaid programs have zero cost-sharing, so 70% of accumulators are at $0.
/// </summary>
public class SyntheticAccumulatorGenerator
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyntheticAccumulatorGenerator"/> class.
    /// </summary>
    public SyntheticAccumulatorGenerator(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Generate accumulators for all active members based on their plan assignments.
    /// </summary>
    /// <param name="members">All subscriber members (with dependents).</param>
    /// <param name="plans">Available benefit plans for lookup.</param>
    /// <param name="seed">Random seed for deterministic generation.</param>
    /// <param name="planYear">Plan year (e.g., "2024").</param>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <returns>List of accumulator records.</returns>
    public List<SyntheticAccumulator> Generate(
        List<SyntheticMember> members,
        List<SyntheticBenefitPlan> plans,
        int seed = 42,
        string planYear = "2024",
        string tenantId = "mcc-benchmark")
    {
        var random = new Random(seed);
        var planLookup = plans.ToDictionary(p => p.PlanId);
        var accumulators = new List<SyntheticAccumulator>();
        int processed = 0;

        foreach (var subscriber in members)
        {
            if (subscriber.EnrollmentStatus != "Active")
                continue;

            // Generate individual accumulator for subscriber
            if (planLookup.TryGetValue(subscriber.PlanId, out var plan))
            {
                accumulators.Add(GenerateAccumulator(
                    subscriber.MemberId, subscriber.SubscriberId, plan, random, planYear, tenantId, "Individual"));
            }

            // Generate individual accumulators for each dependent
            foreach (var dep in subscriber.Dependents)
            {
                if (dep.EnrollmentStatus != "Active")
                    continue;

                var depPlanId = dep.Coverages.FirstOrDefault()?.PlanId ?? subscriber.PlanId;
                if (planLookup.TryGetValue(depPlanId, out var depPlan))
                {
                    accumulators.Add(GenerateAccumulator(
                        dep.MemberId, subscriber.SubscriberId, depPlan, random, planYear, tenantId, "Individual"));
                }
            }

            // Generate family-level accumulator if subscriber has dependents
            if (subscriber.Dependents.Count > 0 && planLookup.TryGetValue(subscriber.PlanId, out var familyPlan))
            {
                accumulators.Add(GenerateAccumulator(
                    subscriber.SubscriberId, subscriber.SubscriberId, familyPlan, random, planYear, tenantId, "Family"));
            }

            processed++;
            if (processed % 10_000 == 0)
            {
                _logger.LogInformation("Generated accumulators for {Count:N0} / {Total:N0} subscribers",
                    processed, members.Count);
            }
        }

        _logger.LogInformation("Accumulator generation complete: {Count:N0} records", accumulators.Count);
        return accumulators;
    }

    private static SyntheticAccumulator GenerateAccumulator(
        string ownerId,
        string subscriberId,
        SyntheticBenefitPlan plan,
        Random random,
        string planYear,
        string tenantId,
        string scope)
    {
        var deductibleLimit = scope == "Family" ? plan.FamilyDeductible : plan.IndividualDeductible;
        var oopLimit = scope == "Family" ? plan.FamilyOopMax : plan.IndividualOopMax;

        // Distribution: 70% at $0 (Medicaid — no cost sharing), 20% partial, 10% near/at max
        decimal deductibleSpent = 0m;
        decimal oopSpent = 0m;

        if (deductibleLimit > 0 || oopLimit > 0)
        {
            var roll = random.NextDouble();
            if (roll < 0.70)
            {
                // Zero balance
                deductibleSpent = 0m;
                oopSpent = 0m;
            }
            else if (roll < 0.90)
            {
                // Partial balance
                deductibleSpent = deductibleLimit > 0
                    ? Math.Round(deductibleLimit * (decimal)random.NextDouble() * 0.7m, 2)
                    : 0m;
                oopSpent = oopLimit > 0
                    ? Math.Round(oopLimit * (decimal)random.NextDouble() * 0.5m, 2)
                    : 0m;
            }
            else
            {
                // Near or at max
                deductibleSpent = deductibleLimit > 0
                    ? Math.Round(deductibleLimit * (decimal)(0.85 + random.NextDouble() * 0.15), 2)
                    : 0m;
                oopSpent = oopLimit > 0
                    ? Math.Round(oopLimit * (decimal)(0.80 + random.NextDouble() * 0.20), 2)
                    : 0m;
            }

            // Clamp to limits
            deductibleSpent = Math.Min(deductibleSpent, deductibleLimit);
            oopSpent = Math.Min(oopSpent, oopLimit);
        }

        return new SyntheticAccumulator
        {
            Id = $"{tenantId}:{scope}:{ownerId}:{plan.PlanId}:{planYear}",
            TenantId = tenantId,
            MemberId = ownerId,
            SubscriberId = subscriberId,
            BenefitPlanId = plan.PlanId,
            PlanYear = planYear,
            Scope = scope,
            IndividualDeductibleLimit = scope == "Individual" ? plan.IndividualDeductible : 0m,
            IndividualDeductibleSpent = scope == "Individual" ? deductibleSpent : 0m,
            FamilyDeductibleLimit = scope == "Family" ? plan.FamilyDeductible : 0m,
            FamilyDeductibleSpent = scope == "Family" ? deductibleSpent : 0m,
            IndividualOopMaxLimit = scope == "Individual" ? plan.IndividualOopMax : 0m,
            IndividualOopSpent = scope == "Individual" ? oopSpent : 0m,
            FamilyOopMaxLimit = scope == "Family" ? plan.FamilyOopMax : 0m,
            FamilyOopSpent = scope == "Family" ? oopSpent : 0m,
            NetworkTier = "InNetwork",
            LastUpdated = DateTime.UtcNow,
        };
    }
}
