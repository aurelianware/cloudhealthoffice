using System.Globalization;
using CloudHealthOffice.GrouperEngine.Data;
using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace CloudHealthOffice.GrouperEngine.Tests;

/// <summary>
/// Holds CHO's grouper to the answers CMS's own grouper gave.
///
/// <para>
/// The corpus and the expected answers live in
/// <c>tools/cms-grouper-parity-oracle/corpus/</c> and were produced by running
/// CMS's V44 jars over the corpus (ADR 024 §5 step 3). The jars are not in this
/// repository and are not needed to run these tests: the oracle's answers are
/// committed, so parity is checked on every build.
/// </para>
///
/// <para>
/// CHO's table set is still the four-family seed slice, so most of the corpus
/// is out of its reach. That is the point. Every row CHO cannot yet group is
/// reported as <i>uncovered</i> rather than passed over in silence, and
/// <see cref="Coverage_against_the_cms_corpus_is_reported"/> prints the count
/// so step 4's progress is visible in the test output. The hard assertion is
/// the one that matters: where CHO does produce a DRG, it must be CMS's DRG.
/// </para>
/// </summary>
public class CmsParityTests
{
    private readonly ITestOutputHelper output;

    public CmsParityTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// Rows where CHO reaches a confident wrong answer because its table set is
    /// a four-family slice rather than the CMS table set, each with the reason.
    ///
    /// <para>
    /// These are listed rather than skipped because a wrong DRG is worse than
    /// no DRG, and that is the design finding this harness produced on its first
    /// run: a grouper is only safe with a complete table set. CHO's engine
    /// cannot tell "this code is genuinely not a complication" from "this code
    /// is not in my 40-code slice", so it guesses, and guesses low. Step 4
    /// imports the real tables and every line below must then disappear — not by
    /// being deleted, but by starting to pass.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KnownTableGaps =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["premdc-tracheostomy"] =
                "the seed slice has one pre-MDC family (heart transplant); DRGs 3-4, "
                + "tracheostomy with ventilation, are not in it",
            ["premdc-ecmo"] =
                "the seed slice has one pre-MDC family (heart transplant); DRG 3, "
                + "ECMO, is not in it",
        };

    /// <summary>
    /// Within the seed slice, CHO must not disagree with CMS. A row CHO cannot
    /// group is not a failure yet; a row it groups differently is, unless it is
    /// a table gap already written down in <see cref="KnownTableGaps"/>.
    /// </summary>
    [Fact]
    public async Task Cho_never_contradicts_the_cms_grouper()
    {
        var grouper = new TableDrivenMsDrgGrouper(
            new InMemoryMsDrgTableProvider(MsDrgSeedTables.Build()),
            NullLogger<TableDrivenMsDrgGrouper>.Instance);

        var disagreements = new List<string>();

        foreach (var expected in ParityCorpus.Load())
        {
            var actual = await grouper.GroupAsync(expected.Claim.ToRequest());

            if (actual.Drg is null)
            {
                continue;
            }

            if (DrgMatches(actual.Drg, expected.Drg))
            {
                // A gap that has closed is good news, but leaving it on the list
                // would let the next real gap hide behind it.
                Assert.False(
                    KnownTableGaps.ContainsKey(expected.Claim.Id),
                    $"{expected.Claim.Id} now agrees with CMS; remove it from KnownTableGaps");
                continue;
            }

            if (KnownTableGaps.ContainsKey(expected.Claim.Id))
            {
                continue;
            }

            disagreements.Add(
                $"{expected.Claim.Id}: CMS said {expected.Drg}, CHO said {actual.Drg} " +
                $"({expected.Claim.Why})");
        }

        Assert.True(
            disagreements.Count == 0,
            "CHO disagrees with the CMS grouper on:" + Environment.NewLine +
            string.Join(Environment.NewLine, disagreements));
    }

    /// <summary>
    /// Reports how much of the corpus CHO can group at all. This does not fail
    /// on low coverage — the seed slice is four families out of about 330 — but
    /// it does fail if coverage goes <i>down</i>, which would mean a change
    /// quietly took a family away.
    /// </summary>
    [Fact]
    public async Task Coverage_against_the_cms_corpus_is_reported()
    {
        var grouper = new TableDrivenMsDrgGrouper(
            new InMemoryMsDrgTableProvider(MsDrgSeedTables.Build()),
            NullLogger<TableDrivenMsDrgGrouper>.Instance);

        var corpus = ParityCorpus.Load();
        var covered = new List<string>();
        var uncovered = new List<string>();

        foreach (var expected in corpus)
        {
            var actual = await grouper.GroupAsync(expected.Claim.ToRequest());
            (actual.Drg is null ? uncovered : covered).Add(expected.Claim.Id);
        }

        output.WriteLine($"CMS parity corpus: {corpus.Count} stays.");
        output.WriteLine($"CHO groups {covered.Count}: {string.Join(", ", covered)}");
        output.WriteLine($"CHO cannot yet group {uncovered.Count}: {string.Join(", ", uncovered)}");

        // The seed slice covers the four families the corpus exercises most
        // heavily. Anything below that means a regression, not slow progress.
        Assert.True(
            covered.Count >= 13,
            $"coverage fell to {covered.Count} rows; the seed slice should reach at least 13");
    }

    /// <summary>
    /// CMS reports a leading-zero-free DRG as an integer; CHO carries it as a
    /// three-character string. Compare them as numbers.
    /// </summary>
    private static bool DrgMatches(string choDrg, string cmsDrg) =>
        int.TryParse(choDrg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cho)
        && int.TryParse(cmsDrg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cms)
        && cho == cms;
}
