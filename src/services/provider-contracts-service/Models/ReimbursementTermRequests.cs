using System.ComponentModel.DataAnnotations;
using EngineModels = CloudHealthOffice.FeeScheduleEngine.Models;

namespace ProviderContractsService.Models;

/// <summary>
/// Replaces a contract's whole rate sheet. The list is ordered and the first
/// matching term prices a claim line, so the order is the contract, not a
/// detail of the request: a carve-out after the default term it carves out of
/// would never be reached.
/// </summary>
public class ReimbursementTermsRequest
{
    /// <summary>
    /// The terms, in evaluation order. An empty list clears the contract's
    /// rates, which leaves it priced by whatever the engine's own contract store
    /// still holds for the provider.
    /// </summary>
    public List<EngineModels.ReimbursementTerm> Terms { get; set; } = new();
}

/// <summary>
/// The fee-for-service rate configuration for a contract, in the shape the
/// portal's FFS editor has always sent (<c>FfsRateConfig</c> in ffs-service),
/// posted here to be appended to the contract as a reimbursement term.
///
/// <para>
/// This is the ffs-service fold of ADR 016 phase 2. A rate config never exists
/// without its contract, and ffs-service never grew a controller or a
/// repository, so rather than giving a second service an API over a second
/// document, the same fields become one <see cref="EngineModels.ReimbursementTerm"/>
/// on the contract — where the pricing engine already reads them.
/// </para>
/// </summary>
public class FfsRateConfigTermRequest
{
    /// <summary>
    /// The fee schedule the provider is paid from, as the engine knows it
    /// (<c>FeeSchedule.Id</c>).
    /// </summary>
    [Required]
    [StringLength(200)]
    public string FeeScheduleId { get; set; } = string.Empty;

    /// <summary>
    /// Percentage of that fee schedule, as <c>FfsRateConfig.FeeSchedulePercentage</c>:
    /// 1.0 = the schedule's own rate, 1.10 = 110% of it.
    /// </summary>
    [Range(0.0001, 100.0)]
    public decimal FeeSchedulePercentage { get; set; } = 1.0m;

    /// <summary>Whether the CMS multiple procedure reduction applies to these lines.</summary>
    public bool MultipleProcedureReduction { get; set; } = true;

    /// <summary>Whether the bilateral (modifier 50) adjustment applies to these lines.</summary>
    public bool BilateralAdjustment { get; set; } = true;

    /// <summary>
    /// Procedure-code range this rate covers (inclusive). Both null = the
    /// contract's default rate, which is how a single FFS rate config reads.
    /// </summary>
    [StringLength(10)]
    public string? ProcedureCodeFrom { get; set; }

    /// <summary>Procedure-code range end (inclusive).</summary>
    [StringLength(10)]
    public string? ProcedureCodeTo { get; set; }

    /// <summary>First service date this rate prices. Null = the contract's effective date.</summary>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>Last service date this rate prices, inclusive. Null = open-ended.</summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>What this rate is, for the portal and the pricing audit trail.</summary>
    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// The same rate as a reimbursement term. <c>FeeSchedulePercentage</c> is the
    /// term's multiplier and the two toggles are its modifier rules, which is
    /// exactly how the engine reads them.
    /// </summary>
    public EngineModels.ReimbursementTerm ToReimbursementTerm() => new()
    {
        Description = Description,
        Method = EngineModels.ReimbursementMethod.FeeScheduleRef,
        FeeScheduleId = FeeScheduleId,
        Multiplier = FeeSchedulePercentage,
        EffectiveDate = EffectiveDate,
        TermDate = TerminationDate,
        Scope = new EngineModels.ReimbursementTermScope
        {
            ProcedureCodeFrom = ProcedureCodeFrom,
            ProcedureCodeTo = ProcedureCodeTo,
        },
        ModifierRules = new EngineModels.TermModifierRules
        {
            MultipleProcedureReduction = MultipleProcedureReduction,
            BilateralAdjustment = BilateralAdjustment,
        },
    };
}

/// <summary>
/// Whether a contract's rate sheet is one the pricing engine can honour. Each
/// term validates itself (<see cref="EngineModels.ReimbursementTerm.Validate"/>);
/// these are the rules that only hold for the list as a whole.
/// </summary>
public static class ReimbursementTermSheet
{
    /// <summary>
    /// The reasons this rate sheet cannot be stored, empty when it can. Every
    /// message names the term by its 1-based position, because that position is
    /// what decides which term prices a line.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<EngineModels.ReimbursementTerm> terms)
    {
        var errors = new List<string>();

        for (var i = 0; i < terms.Count; i++)
        {
            foreach (var error in terms[i].Validate())
                errors.Add($"term {i + 1}: {error}");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < terms.Count; i++)
        {
            if (!string.IsNullOrEmpty(terms[i].Id) && !seen.Add(terms[i].Id))
                errors.Add($"term {i + 1}: id {terms[i].Id} is already used by an earlier term");
        }

        // A term no claim line can reach is a rate somebody meant to apply.
        // Saying so at the write is the only chance to catch it: at pricing time
        // it is invisible, because an earlier term simply answers first.
        for (var i = 0; i < terms.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (terms[j].Scope.IsUnconditional && Covers(terms[j], terms[i]))
                {
                    errors.Add(
                        $"term {i + 1} is unreachable: term {j + 1} covers every claim line for the whole " +
                        $"of term {i + 1}'s effective dates, so it always prices first");
                    break;
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Whether <paramref name="earlier"/>'s effective dates cover the whole of
    /// <paramref name="later"/>'s, so that no service date can reach the later
    /// term. Null is open: no effective date is the beginning of time, no term
    /// date is the end of it.
    /// </summary>
    private static bool Covers(
        EngineModels.ReimbursementTerm earlier, EngineModels.ReimbursementTerm later)
        => (earlier.EffectiveDate ?? DateTime.MinValue) <= (later.EffectiveDate ?? DateTime.MinValue)
           && (earlier.TermDate ?? DateTime.MaxValue) >= (later.TermDate ?? DateTime.MaxValue);
}
