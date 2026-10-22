using System.Text.Json;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Mask;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using Xunit;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// The whole engine over a real release: tables in, DRG out.
///
/// <para>
/// Skipped unless <c>CMS_MSDRG_TABLES</c> names a directory. This is not
/// parity — nothing here knows what CMS would have said — but it is the run
/// that exercises every part of the chain against real data at scale, and the
/// failure it is here to catch is a chain that works on hand-built tables and
/// falls over on CMS's own.
/// </para>
/// </summary>
public class MsDrgReleaseEndToEndTests
{
    private static MsDrgGroupingRequest Stay(string principal, params string[] secondaries) =>
        new()
        {
            PrincipalDiagnosis = new GroupingDiagnosis(principal),
            SecondaryDiagnoses = [.. secondaries.Select(
                code => new GroupingDiagnosis(code, "Y"))],
            DischargeDate = new DateOnly(2026, 1, 15),
            DischargeStatus = "01",
            Sex = "F",
            AgeYears = 57,
            PoaReporting = HospitalPoaReporting.NonExempt,
        };

    private static IReadOnlyList<string> SampleDiagnoses(string directory, int every)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(directory, "diagnoses.json")));

        var sample = new List<string>();
        var index = 0;

        foreach (var code in document.RootElement.GetProperty("dxMap").EnumerateObject())
        {
            if (index++ % every == 0)
            {
                sample.Add(code.Name);
            }
        }

        return sample;
    }

    /// <summary>
    /// Every stay in a broad sample of the release's own diagnosis codes
    /// reaches an answer: a DRG, or a refusal with a return code. Neither an
    /// exception nor a silent zero is an answer.
    /// </summary>
    [SkippableFact]
    public void A_broad_sample_of_the_releases_codes_all_reach_an_answer()
    {
        var tables = Environment.GetEnvironmentVariable("CMS_MSDRG_TABLES");
        Skip.If(
            string.IsNullOrWhiteSpace(tables),
            "Set CMS_MSDRG_TABLES to a CMS MS-DRG release's table directory to run this.");

        var version = Environment.GetEnvironmentVariable("CMS_MSDRG_VERSION") ?? "V440";
        var grouper = new MsDrgFormulaGrouper(
            CmsJsonTableImporter.ImportRelease(tables!, version));

        var sample = SampleDiagnoses(tables!, every: 40);
        Assert.True(sample.Count > 1_000, $"Only {sample.Count} codes sampled.");

        var grouped = 0;
        var refused = 0;
        var notInThisVersion = 0;
        var failures = new List<string>();

        foreach (var code in sample)
        {
            try
            {
                var result = grouper.Group(Stay(code));

                if (result.UnrecognisedCodes.Count > 0)
                {
                    // The sample is every code the file holds and the file
                    // holds several versions, so some have no row for this
                    // one. CMS groups those anyway and refuses them through
                    // its own invalid_pdx edit, so they land in the refused
                    // count below; what matters here is that the result says
                    // which code it did not know.
                    Assert.Contains(code, result.UnrecognisedCodes);
                    notInThisVersion++;
                }

                if (result.PaidDrg is int drg)
                {
                    Assert.InRange(drg, 1, 998);
                    grouped++;
                }
                else
                {
                    // A refusal has to say why. A null DRG with no return code
                    // would be the engine losing a claim quietly.
                    Assert.NotNull(result.Grouping?.AfterHacPolicy.ReturnCode);
                    refused++;
                }

                // Every stay reaches one of the two.
                Assert.NotNull(result.Grouping);
            }
            catch (Exception exception)
            {
                failures.Add($"{code}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Assert.Empty(failures.Take(20));

        // Most principal diagnoses group. The rest are codes CMS will not
        // accept as a principal, which refuse with a return code rather than
        // failing. On V44 this sample grouped 1,684 of 1,876 and refused 192,
        // with nothing thrown; the bound is loose because a new fiscal year
        // moves codes, and the assertion that matters is the one above.
        Assert.True(
            grouped > (sample.Count - notInThisVersion) * 3 / 4,
            $"Only {grouped} of {sample.Count} stays grouped, {refused} refused, "
            + $"{notInThisVersion} not in {version}.");
    }
}
