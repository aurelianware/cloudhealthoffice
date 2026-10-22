using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Models;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Procedure clusters: combinations of codes that qualify a stay for something
/// no single code does, and the MDC suppression that stops the stay being
/// credited twice.
/// </summary>
public class ProcedureClusterTests
{
    private const string Version = "V440";

    private static readonly DateOnly Discharge = new(2026, 3, 15);

    /// <summary>
    /// A two-choice cluster: a removal paired with a replacement, restricted
    /// in MDC 8. This is the shape 4,069 of CMS's 4,111 V44 clusters have.
    /// </summary>
    private static MsDrgCluster Replacement { get; } = new(
        "Z@4690",
        [
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0SPD08Z" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0SRD0JZ" },
        ],
        new HashSet<int> { 8 });

    private static MsDrgMaskBuilder Builder() => new(
        new InMemoryCodeAttributeSource(
            Version,
            new Dictionary<string, DiagnosisCodeAttributes>
            {
                ["M1711"] = InMemoryCodeAttributeSource.Diagnosis(
                    8, ComplicationLevel.None, "dxc0801"),
            },
            new Dictionary<string, ProcedureCodeAttributes>
            {
                ["0SPD08Z"] = InMemoryCodeAttributeSource.Procedure("d477", "kneeremove"),
                ["0SRD0JZ"] = InMemoryCodeAttributeSource.Procedure("d477", "kneereplace"),
                ["0SRA00A"] = InMemoryCodeAttributeSource.Procedure("d477", "hipreplace"),
                ["Z@4690"] = InMemoryCodeAttributeSource.Procedure("d477", "kneerevision"),
            }),
        new InMemoryClusterSource([Replacement]));

    private static MsDrgGroupingRequest Stay(params string[] procedures) => new()
    {
        PrincipalDiagnosis = new GroupingDiagnosis("M1711"),
        Procedures = procedures.Select(code => new GroupingProcedure(code)).ToList(),
        DischargeDate = Discharge,
        DischargeStatus = "01",
        Sex = "F",
    };

    [Fact]
    public void A_cluster_needs_a_code_from_every_choice()
    {
        Assert.Empty(MsDrgClusterResolver.Resolve(
            new InMemoryClusterSource([Replacement]), ["0SPD08Z"]));

        Assert.Empty(MsDrgClusterResolver.Resolve(
            new InMemoryClusterSource([Replacement]), ["0SRD0JZ"]));

        var satisfied = MsDrgClusterResolver.Resolve(
            new InMemoryClusterSource([Replacement]), ["0SPD08Z", "0SRD0JZ"]);

        Assert.Single(satisfied);
        Assert.Equal("Z@4690", satisfied[0].Cluster.ClusterCode);
        Assert.Equal(
            new HashSet<string> { "0SPD08Z", "0SRD0JZ" },
            satisfied[0].ContributingCodes.ToHashSet());
    }

    /// <summary>
    /// A satisfied cluster's own code is looked up in the procedure table like
    /// any other procedure, so the stay gains operands that none of its own
    /// codes carry. That is the point of the mechanism.
    /// </summary>
    [Fact]
    public void A_satisfied_cluster_contributes_its_own_operands()
    {
        var mask = Builder().Build(Stay("0SPD08Z", "0SRD0JZ"), out var unrecognised);

        Assert.Empty(unrecognised);
        Assert.True(mask.Contains(
            new OperandName(OperandPosition.Unqualified, "kneerevision")));
    }

    [Fact]
    public void An_unsatisfied_cluster_contributes_nothing()
    {
        var mask = Builder().Build(Stay("0SPD08Z"), out _);

        Assert.False(mask.Contains(
            new OperandName(OperandPosition.Unqualified, "kneerevision")));
        Assert.True(mask.Contains(
            new OperandName(OperandPosition.Unqualified, "kneeremove")));
    }

    /// <summary>
    /// The suppression itself. In the cluster's restricted MDC the individual
    /// codes' operands stop counting, so the stay is credited for the
    /// combination and not also for its parts. Outside that MDC they count
    /// normally — the suppression is per MDC, not global.
    /// </summary>
    [Fact]
    public void A_contributing_codes_operands_stop_counting_in_the_restricted_mdc()
    {
        var mask = Builder().Build(Stay("0SPD08Z", "0SRD0JZ"), out _);

        var removal = new OperandName(OperandPosition.Unqualified, "kneeremove");

        // Present in the mask either way: suppression is about whether it
        // counts, not about whether it is recorded.
        Assert.True(mask.Contains(removal));

        Assert.False(mask.ContainsUnsuppressed(removal, mdc: 8));
        Assert.True(mask.ContainsUnsuppressed(removal, mdc: 5));
    }

    /// <summary>
    /// The cluster's own operands are never suppressed. Only the individual
    /// codes' are. Getting this the other way round would suppress exactly the
    /// thing the cluster exists to contribute.
    /// </summary>
    [Fact]
    public void The_clusters_own_operands_are_never_suppressed()
    {
        var mask = Builder().Build(Stay("0SPD08Z", "0SRD0JZ"), out _);

        Assert.True(mask.ContainsUnsuppressed(
            new OperandName(OperandPosition.Unqualified, "kneerevision"), mdc: 8));
    }

    /// <summary>
    /// A code outside the cluster is unaffected, even in the restricted MDC —
    /// suppression follows the codes that contributed, not the claim.
    /// </summary>
    [Fact]
    public void A_code_outside_the_cluster_still_counts()
    {
        var mask = Builder().Build(Stay("0SPD08Z", "0SRD0JZ", "0SRA00A"), out _);

        Assert.True(mask.ContainsUnsuppressed(
            new OperandName(OperandPosition.Unqualified, "hipreplace"), mdc: 8));
    }

    /// <summary>
    /// ORPROC carries the suppression of the operands it was derived from, so a
    /// stay whose only operating-room procedures were consumed by a cluster
    /// does not also count as having had one of its own in the restricted MDC.
    /// The cluster's own ORPROC is what carries it there.
    /// </summary>
    [Fact]
    public void Orproc_follows_the_suppression_of_the_code_it_came_from()
    {
        var builder = new MsDrgMaskBuilder(
            new InMemoryCodeAttributeSource(
                Version,
                new Dictionary<string, DiagnosisCodeAttributes>
                {
                    ["M1711"] = InMemoryCodeAttributeSource.Diagnosis(
                        8, ComplicationLevel.None, "dxc0801"),
                },
                new Dictionary<string, ProcedureCodeAttributes>
                {
                    ["0SPD08Z"] = InMemoryCodeAttributeSource.Procedure("d477"),
                    ["0SRD0JZ"] = InMemoryCodeAttributeSource.Procedure("d477"),
                    // The cluster code itself is not an OR procedure here, so
                    // nothing else can supply ORPROC in MDC 8.
                    ["Z@4690"] = InMemoryCodeAttributeSource.Procedure("kneerevision"),
                }),
            new InMemoryClusterSource([Replacement]));

        var mask = builder.Build(Stay("0SPD08Z", "0SRD0JZ"), out _);
        var orProc = new OperandName(OperandPosition.Unqualified, "ORPROC");

        Assert.True(mask.Contains(orProc));
        Assert.False(mask.ContainsUnsuppressed(orProc, mdc: 8));
        Assert.True(mask.ContainsUnsuppressed(orProc, mdc: 5));
    }

    /// <summary>
    /// Suppression is a mode of evaluation rather than a property of the
    /// operand: CMS evaluates the same formula text with it on for grouping and
    /// off elsewhere. So the same mask and the same formula give two answers,
    /// and which one is right depends on the caller.
    /// </summary>
    [Fact]
    public void Suppression_is_a_property_of_the_evaluation()
    {
        var mask = Builder().Build(Stay("0SPD08Z", "0SRD0JZ"), out _);
        var formula = DrgFormulaParser.Parse("kneeremove");

        Assert.False(formula.Evaluate(
            new DrgFormulaEvaluation(mask, Mdc: 8, ApplySuppression: true)));
        Assert.True(formula.Evaluate(
            new DrgFormulaEvaluation(mask, Mdc: 8, ApplySuppression: false)));
        Assert.True(formula.Evaluate(
            new DrgFormulaEvaluation(mask, Mdc: 5, ApplySuppression: true)));
    }

    /// <summary>
    /// A stay can satisfy one operand twice, once through a suppressed code and
    /// once through a code the cluster never touched. Then it counts: the test
    /// is "is there a matching entry that still counts", not "did the matching
    /// entry get suppressed".
    /// </summary>
    [Fact]
    public void One_unsuppressed_contribution_is_enough()
    {
        var builder = new MsDrgMaskBuilder(
            new InMemoryCodeAttributeSource(
                Version,
                new Dictionary<string, DiagnosisCodeAttributes>
                {
                    ["M1711"] = InMemoryCodeAttributeSource.Diagnosis(
                        8, ComplicationLevel.None, "dxc0801"),
                },
                new Dictionary<string, ProcedureCodeAttributes>
                {
                    ["0SPD08Z"] = InMemoryCodeAttributeSource.Procedure("shared"),
                    ["0SRD0JZ"] = InMemoryCodeAttributeSource.Procedure("shared"),
                    ["0SRA00A"] = InMemoryCodeAttributeSource.Procedure("shared"),
                    ["Z@4690"] = InMemoryCodeAttributeSource.Procedure("kneerevision"),
                }),
            new InMemoryClusterSource([Replacement]));

        var shared = new OperandName(OperandPosition.Unqualified, "shared");

        Assert.False(builder
            .Build(Stay("0SPD08Z", "0SRD0JZ"), out _)
            .ContainsUnsuppressed(shared, mdc: 8));

        Assert.True(builder
            .Build(Stay("0SPD08Z", "0SRD0JZ", "0SRA00A"), out _)
            .ContainsUnsuppressed(shared, mdc: 8));
    }

    [Fact]
    public void A_stay_with_no_procedures_needs_no_cluster_work()
    {
        Assert.Empty(MsDrgClusterResolver.Resolve(
            new InMemoryClusterSource([Replacement]), []));
    }

    [Fact]
    public void A_cluster_mentioned_by_two_of_the_claims_codes_is_still_one_cluster()
    {
        var satisfied = MsDrgClusterResolver.Resolve(
            new InMemoryClusterSource([Replacement]), ["0SPD08Z", "0SRD0JZ"]);

        Assert.Single(satisfied);
    }

    [Fact]
    public void The_mask_builder_declares_no_remaining_gaps()
    {
        Assert.Empty(MsDrgMaskBuilder.NotYetBuilt);
    }
}
