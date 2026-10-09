namespace CloudHealthOffice.ClaimsService.Tests.EDI.Snip;

/// <summary>
/// Synthetic, SNIP-clean 837P / 837I files (no real member, provider or
/// claim data; the NPIs are check-digit-valid test numbers). Tests mutate one
/// segment at a time to produce a file that fails exactly one rule.
/// <see cref="Wrap"/> computes SE01 and the envelope so a mutation does not
/// accidentally break a control count.
/// </summary>
internal static class Snip837Samples
{
    public const string ProfessionalVersion = "005010X222A1";
    public const string InstitutionalVersion = "005010X223A2";

    /// <summary>Body of a clean 837P transaction set (everything between ST and SE).</summary>
    public static List<string> ProfessionalBody(string claimId = "PCN0001") =>
    [
        "BHT*0019*00*BATCH0001*20260115*1200*CH",
        "NM1*41*2*ACME BILLING*****46*SUB001",
        "PER*IC*BILLING DESK*TE*5555550100",
        "NM1*40*2*CHO PAYER*****46*CHO",
        "HL*1**20*1",
        "NM1*85*2*ACME MEDICAL GROUP*****XX*1234567893",
        "N3*100 MAIN ST",
        "N4*SPRINGFIELD*IL*62701",
        "REF*EI*123456789",
        "HL*2*1*22*0",
        "SBR*P*18*GRP001******CI",
        "NM1*IL*1*TESTPATIENT*ALEX****MI*MEM0001",
        "DMG*D8*19800101*F",
        "NM1*PR*2*CHO PAYER*****PI*CHO",
        $"CLM*{claimId}*150.00***11:B:1*Y*A*Y*Y",
        "HI*ABK:J069*ABF:R05",
        "LX*1",
        "SV1*HC:99213*100.00*UN*1***1:2",
        "DTP*472*D8*20260110",
        "LX*2",
        "SV1*HC:87880*50.00*UN*1***1",
        "DTP*472*D8*20260110",
    ];

    /// <summary>Body of a clean 837I outpatient (type of bill 13x) transaction set.</summary>
    public static List<string> InstitutionalBody(string claimId = "PCN0002") =>
    [
        "BHT*0019*00*BATCH0002*20260115*1200*CH",
        "NM1*41*2*ACME BILLING*****46*SUB001",
        "PER*IC*BILLING DESK*TE*5555550100",
        "NM1*40*2*CHO PAYER*****46*CHO",
        "HL*1**20*1",
        "NM1*85*2*GENERAL HOSPITAL*****XX*1003000126",
        "N3*200 HOSPITAL DR",
        "N4*SPRINGFIELD*IL*62701",
        "REF*EI*987654321",
        "HL*2*1*22*0",
        "SBR*P*18*******MC",
        "NM1*IL*1*TESTPATIENT*SAM****MI*MEM0002",
        "DMG*D8*19700101*M",
        "NM1*PR*2*CHO PAYER*****PI*CHO",
        $"CLM*{claimId}*300.00***13:A:1**A*Y*Y",
        "DTP*434*RD8*20260105-20260106",
        "CL1*1*7*01",
        "HI*ABK:R0789",
        "LX*1",
        "SV2*0450*HC:99283*200.00*UN*1",
        "DTP*472*D8*20260105",
        "LX*2",
        "SV2*0300*HC:80053*100.00*UN*1",
        "DTP*472*D8*20260106",
    ];

    /// <summary>Wraps one or more transaction-set bodies in ST/SE, GS/GE and ISA/IEA with correct counts.</summary>
    public static string Wrap(string version, params List<string>[] bodies)
    {
        var segments = new List<string>
        {
            "ISA*00*          *00*          *ZZ*SUB001         *ZZ*CHO            *260115*1200*^*00501*000000101*0*T*:",
            $"GS*HC*SUB001*CHO*20260115*1200*101*X*{version}",
        };

        for (var i = 0; i < bodies.Length; i++)
        {
            var control = (i + 1).ToString("D4");
            segments.Add($"ST*837*{control}*{version}");
            segments.AddRange(bodies[i]);
            segments.Add($"SE*{bodies[i].Count + 2}*{control}");
        }

        segments.Add($"GE*{bodies.Length}*101");
        segments.Add("IEA*1*000000101");
        return string.Join("~", segments) + "~";
    }

    public static string Professional(Action<List<string>>? mutate = null)
    {
        var body = ProfessionalBody();
        mutate?.Invoke(body);
        return Wrap(ProfessionalVersion, body);
    }

    public static string Institutional(Action<List<string>>? mutate = null)
    {
        var body = InstitutionalBody();
        mutate?.Invoke(body);
        return Wrap(InstitutionalVersion, body);
    }

    /// <summary>Replace the first segment starting with <paramref name="prefix"/>.</summary>
    public static void Replace(this List<string> body, string prefix, string replacement)
    {
        var index = body.FindIndex(s => s.StartsWith(prefix, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"No segment starts with '{prefix}'.");
        body[index] = replacement;
    }

    public static void RemoveSegment(this List<string> body, string prefix)
    {
        var index = body.FindIndex(s => s.StartsWith(prefix, StringComparison.Ordinal));
        if (index < 0) throw new InvalidOperationException($"No segment starts with '{prefix}'.");
        body.RemoveAt(index);
    }
}
