using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Formulas;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Grouping;
using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using Xunit;
using Xunit.Abstractions;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Holds the formula engine to the answers CMS's own grouper gave.
///
/// <para>
/// This is the run everything else was building towards. The corpus and CMS's
/// answers are committed in <c>tools/cms-grouper-parity-oracle/corpus/</c>;
/// the engine needs a licensed CMS release's tables to import, so the test is
/// gated on <c>CMS_MSDRG_TABLES</c>. Every row the engine groups must agree
/// with CMS on the DRG, the MDC, the base DRG, the severity and the
/// medical/surgical partition — not on the DRG alone, because a right DRG
/// reached through the wrong MDC is a coincidence waiting to stop happening.
/// </para>
///
/// <para>
/// The corpus rows are chosen to distinguish a correct grouper from a
/// plausible wrong one: the three-way severity splits, CC exclusions, the
/// pre-MDC rules, the surgical hierarchy, the sex edit, and the
/// present-on-admission cases where a hospital-acquired condition moves the
/// stay to a lower-paying DRG.
/// </para>
/// </summary>
public class CmsFormulaParityTests(ITestOutputHelper output)
{
    private static ComplicationLevel Severity(string cms) => cms switch
    {
        "MCC" => ComplicationLevel.Mcc,
        "CC" => ComplicationLevel.Cc,
        _ => ComplicationLevel.None,
    };

    private static DrgTablePartition Partition(string cms) => cms switch
    {
        "MEDICAL" => DrgTablePartition.Medical,
        "SURGICAL" => DrgTablePartition.Surgical,
        _ => DrgTablePartition.NotApplicable,
    };

    /// <summary>
    /// The engine agrees with CMS on every row of the corpus.
    /// </summary>
    [SkippableFact]
    public void The_formula_engine_agrees_with_the_cms_grouper()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var grouper = new MsDrgFormulaGrouper(
            CmsJsonTableImporter.ImportRelease(tables!, version));

        var disagreements = new List<string>();
        var agreed = 0;

        foreach (var expected in ParityCorpus.Load())
        {
            // CMS was asked with the facility reporting POA, so CHO is too.
            var request = expected.Claim.ToRequest() with
            {
                PoaReporting = HospitalPoaReporting.NonExempt,
            };

            try
            {
                var result = grouper.Group(request);

                if (result.Grouping is null)
                {
                    disagreements.Add(
                        $"{expected.Claim.Id}: CMS said DRG {expected.Drg}; CHO refused "
                        + $"before grouping ({result.Refusal})");
                    continue;
                }

                var paid = result.Grouping.AfterHacPolicy;
                var differences = new List<string>();

                if (paid.Drg.ToString() != expected.Drg)
                {
                    differences.Add($"DRG {paid.Drg} not {expected.Drg}");
                }

                if (paid.Mdc.ToString() != expected.Mdc)
                {
                    differences.Add($"MDC {paid.Mdc} not {expected.Mdc}");
                }

                if (paid.BaseDrg.ToString() != expected.BaseDrg)
                {
                    differences.Add($"base DRG {paid.BaseDrg} not {expected.BaseDrg}");
                }

                if (paid.Severity != Severity(expected.Severity))
                {
                    differences.Add($"severity {paid.Severity} not {expected.Severity}");
                }

                if (paid.Partition != Partition(expected.MedSurg))
                {
                    differences.Add($"partition {paid.Partition} not {expected.MedSurg}");
                }

                if (differences.Count > 0)
                {
                    disagreements.Add(
                        $"{expected.Claim.Id} ({expected.Claim.Why}): "
                        + string.Join("; ", differences));
                }
                else
                {
                    agreed++;
                }
            }
            catch (Exception exception)
            {
                disagreements.Add(
                    $"{expected.Claim.Id}: CMS said DRG {expected.Drg}; CHO threw "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        output.WriteLine($"Agreed with CMS on {agreed} rows.");

        foreach (var disagreement in disagreements)
        {
            output.WriteLine(disagreement);
        }

        Assert.Empty(disagreements);
    }

    /// <summary>
    /// The one corpus row CMS refused, which the parity loader leaves out
    /// because a refusal is not a DRG: a principal diagnosis outside the code
    /// set groups to 999 with the <c>invalid_pdx</c> edit raised.
    ///
    /// <para>
    /// Asserted separately because it is the case that settled a design
    /// question. CHO used to refuse an unknown code before grouping, which
    /// looked like the careful thing and disagreed with CMS on three corpus
    /// rows — two that CMS groups regardless and this one, which CMS refuses
    /// through its own tables with a return code attached. The tables' answer
    /// is the one that gets paid.
    /// </para>
    /// </summary>
    [SkippableFact]
    public void An_unknown_principal_diagnosis_is_refused_through_cmss_own_edit()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var grouper = new MsDrgFormulaGrouper(
            CmsJsonTableImporter.ImportRelease(tables!, version));

        var result = grouper.Group(new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis("ZZZZZ", "Y"),
            DischargeDate = new DateOnly(2026, 3, 15),
            DischargeStatus = "01",
            Sex = "M",
            AgeYears = 65,
            PoaReporting = HospitalPoaReporting.NonExempt,
        });

        Assert.Contains("ZZZZZ", result.UnrecognisedCodes);
        Assert.NotNull(result.Grouping);

        var paid = result.Grouping.AfterHacPolicy;
        Assert.False(paid.Groupable);
        Assert.Equal(MsDrgGrouper.Ungroupable, paid.Drg);
        Assert.Equal(7, paid.ReturnCode);
    }
}
