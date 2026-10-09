using System.Globalization;
using System.Runtime.CompilerServices;

namespace CloudHealthOffice.GoldenPath.Tests.Harness;

/// <summary>
/// Reads an X12 835 for the golden comparison: normalizes the values the
/// generator makes up at run time, checks the 835 balancing rules directly
/// (so a golden file can never bless an unbalanced remittance), and compares
/// against <c>Golden/{name}.835</c>.
/// </summary>
internal static class X12835
{
    public static List<string[]> Segments(string edi) =>
        edi.Split('~', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('\r', '\n').Split('*'))
            .ToList();

    /// <summary>
    /// One segment per line, with the run-time values replaced: ISA09/ISA10
    /// (interchange date/time), ISA13 and IEA02 (control number, from the
    /// clock), CLP07 (claim id assigned at submission), GS04/GS05 (group date/time) and DTM*405 (production date).
    /// Everything else — amounts, CARCs, check/trace number, BPR16 payment
    /// date, service dates — is compared as generated.
    /// </summary>
    public static string Normalize(string edi)
    {
        var lines = new List<string>();
        foreach (var seg in Segments(edi))
        {
            switch (seg[0])
            {
                case "ISA":
                    seg[9] = "YYMMDD";
                    seg[10] = "HHMM";
                    seg[13] = "#########";
                    break;
                case "GS":
                    seg[4] = "CCYYMMDD";
                    seg[5] = "HHMM";
                    break;
                case "DTM" when seg[1] == "405":
                    seg[2] = "CCYYMMDD";
                    break;
                case "IEA":
                    seg[2] = "#########";
                    break;
                case "CLP" when seg.Length > 7:
                    // CLP07 payer claim control number: the claim id claims-service
                    // assigns at submission (a GUID). Asserted in the test instead.
                    seg[7] = "{claim-id}";
                    break;
            }
            lines.Add(string.Join("*", seg) + "~");
        }
        return string.Join("\n", lines) + "\n";
    }

    private static decimal Amount(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);

    /// <summary>Sum of a CAS segment's adjustment amounts (CAS03, 06, 09, …).</summary>
    private static decimal CasTotal(string[] cas)
    {
        var total = 0m;
        for (var i = 3; i < cas.Length; i += 3)
            total += Amount(cas[i]);
        return total;
    }

    /// <summary>
    /// 005010X221A1 balancing, asserted from the 835 text itself:
    /// per service line SVC02 − ΣCAS = SVC03; per claim CLP03 − ΣCAS (claim
    /// and line) = CLP04 and ΣSVC03 = CLP04; per transaction
    /// BPR02 = ΣCLP04 − ΣPLB.
    /// </summary>
    public static void AssertBalanced(string edi)
    {
        var segments = Segments(edi);
        var clpTotal = 0m;
        var plbTotal = 0m;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg[0] == "PLB")
            {
                for (var k = 4; k < seg.Length; k += 2)
                    plbTotal += Amount(seg[k]);
            }
            if (seg[0] != "CLP") continue;

            var claimLoop = segments.Skip(i + 1).TakeWhile(s => s[0] is not ("CLP" or "PLB" or "SE")).ToList();
            var charge = Amount(seg[3]);
            var paid = Amount(seg[4]);
            clpTotal += paid;

            var claimCas = claimLoop.Where(s => s[0] == "CAS").Sum(CasTotal);
            Assert.True(charge - claimCas == paid,
                $"CLP {seg[1]}: CLP03 {charge:F2} − ΣCAS {claimCas:F2} = {charge - claimCas:F2}, but CLP04 is {paid:F2}");

            var svcPaid = 0m;
            for (var j = 0; j < claimLoop.Count; j++)
            {
                if (claimLoop[j][0] != "SVC") continue;
                var svc = claimLoop[j];
                var lineCas = claimLoop.Skip(j + 1).TakeWhile(s => s[0] != "SVC").Where(s => s[0] == "CAS").Sum(CasTotal);
                var svcCharge = Amount(svc[2]);
                var svcPayment = Amount(svc[3]);
                svcPaid += svcPayment;
                Assert.True(svcCharge - lineCas == svcPayment,
                    $"CLP {seg[1]} SVC {svc[1]}: SVC02 {svcCharge:F2} − ΣCAS {lineCas:F2} = {svcCharge - lineCas:F2}, but SVC03 is {svcPayment:F2}");
            }
            if (claimLoop.Any(s => s[0] == "SVC"))
                Assert.True(svcPaid == paid, $"CLP {seg[1]}: ΣSVC03 {svcPaid:F2} ≠ CLP04 {paid:F2}");
        }

        var bpr = segments.Single(s => s[0] == "BPR");
        Assert.True(Amount(bpr[2]) == clpTotal - plbTotal,
            $"BPR02 {bpr[2]} ≠ ΣCLP04 {clpTotal:F2} − ΣPLB {plbTotal:F2}");
    }

    /// <summary>
    /// Compares the normalized 835 with <c>Golden/{name}.835</c>. With
    /// <c>GOLDEN_UPDATE=1</c> the source golden file is (re)written instead —
    /// only after the balance assertions passed, and every rewrite must be
    /// hand-checked against the scenario's arithmetic before it is committed.
    /// </summary>
    public static void AssertMatchesGolden(string name, string edi, [CallerFilePath] string callerFile = "")
    {
        var actual = Normalize(edi);
        var sourcePath = Path.Combine(Path.GetDirectoryName(callerFile)!, "Golden", name + ".835");
        if (Environment.GetEnvironmentVariable("GOLDEN_UPDATE") == "1")
        {
            File.WriteAllText(sourcePath, actual);
            return;
        }

        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", name + ".835");
        Assert.True(File.Exists(goldenPath), $"golden file {name}.835 missing; generated:\n{actual}");
        Assert.Equal(File.ReadAllText(goldenPath).ReplaceLineEndings("\n"), actual);
    }
}
