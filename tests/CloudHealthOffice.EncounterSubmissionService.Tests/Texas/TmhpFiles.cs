using System.Text.RegularExpressions;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Texas;

/// <summary>
/// Synthetic TMHP-shaped response files and structural checks for the 837s CHO sends
/// to TMHP. The shapes follow TMHP's public 277CA companion guide (July 2019): a
/// 2200B receipt STC of A1:19:PR, claim-level STC A2 (accept, PR001), A3 (reject,
/// Bx business edits) or R3 (warning), TRN02 = the patient control number, and no
/// 2200D REF*1K for encounters. Every identifier and value here is synthetic.
/// </summary>
internal static class TmhpFiles
{
    public const string TmhpSender = "617591011TEDT";

    private static string Isa(string receiver, string control) =>
        $"ISA*00*          *00*          *ZZ*{TmhpSender,-15}*ZZ*{receiver,-15}*261102*0900*^*00501*{control}*0*T*:~";

    /// <summary>A 999 accepting (A) or rejecting (R) the given ST02s of group <paramref name="gs06"/>.</summary>
    public static string Ack999(string submitter, string gs06, params (string St, string Code)[] sets)
    {
        var body = $"GS*FA*{TmhpSender}*{submitter}*20261102*0900*7001*X*005010X231A1~ST*999*0001*005010X231A1~" +
                   $"AK1*HC*{gs06}*005010X222A1~";
        var segments = 2;
        foreach (var (st, code) in sets)
        {
            body += $"AK2*837*{st}*005010X222A1~";
            segments++;
            if (code == "R")
            {
                body += "IK3*NM1*8*2010BA*8~";
                segments++;
            }
            body += $"IK5*{code}~";
            segments++;
        }
        var accepted = sets.Count(s => s.Code is "A" or "E");
        var group = accepted == sets.Length ? "A" : accepted == 0 ? "R" : "P";
        body += $"AK9*{group}*{sets.Length}*{sets.Length}*{accepted}~";
        segments += 2;
        return Isa(submitter, "000007001") + body + $"SE*{segments}*0001~GE*1*7001~IEA*1*000007001~";
    }

    /// <summary>
    /// A TMHP 277CA for encounters. Each claim is (CLM01, claim STCs, charge). No REF*1K:
    /// TMHP does not return the ICN for encounters.
    /// </summary>
    public static string Ack277Ca(string submitter, string bht03, params (string Pcn, string[] Stcs, decimal Charge)[] claims)
    {
        var body = $"GS*HN*{TmhpSender}*{submitter}*20261102*0900*7002*X*005010X214~ST*277*0001*005010X214~" +
                   "BHT*0085*08*TMHP277CA0001*20261102*0900*TH~" +
                   "HL*1**20*1~NM1*PR*2*TEXAS MEDICAID HEALTHCARE PARTNERSHIP*****46*617591011~" +
                   "TRN*1*TMHP277CA0001~DTP*050*D8*20261102~DTP*009*D8*20261102~" +
                   $"HL*2*1*21*1~NM1*41*2*SYNTHETIC STAR PLAN*****46*{submitter}~TRN*2*{bht03}~" +
                   $"STC*A1:19:PR*20261102*WQ*{claims.Sum(c => c.Charge):0.##}~" +
                   $"QTY*90*{claims.Count(c => !c.Stcs.Any(s => s.StartsWith("A3")))}~" +
                   $"QTY*AA*{claims.Count(c => c.Stcs.Any(s => s.StartsWith("A3")))}~" +
                   "HL*3*2*19*1~NM1*85*2*SYNTHETIC CLINIC PA*****XX*1234567893~";
        var hl = 4;
        foreach (var (pcn, stcs, charge) in claims)
        {
            body += $"HL*{hl++}*3*PT~NM1*QC*1*DOE*ALEX****MI*512345678~TRN*2*{pcn}~";
            foreach (var stc in stcs) body += $"STC*{stc}*20261102*{(stc.StartsWith("A3") ? "U" : "WQ")}*{charge:0.##}~";
            body += "DTP*472*D8*20261003~";
        }
        var count = body.Split('~', StringSplitOptions.RemoveEmptyEntries).Length - 1; // ST..last, less GS
        return Isa(submitter, "000007002") + body + $"SE*{count}*0001~GE*1*7002~IEA*1*000007002~";
    }

    /// <summary>
    /// Structural checks a TMHP front end applies before it reads a single claim:
    /// one ISA/GS, ≤ 5000 ST/SE, matching control numbers and segment counts, unique
    /// ST02, CLM01 ≤ 20 characters, the declared implementation guide.
    /// </summary>
    public static IReadOnlyList<string> Check837(string content, string version)
    {
        var problems = new List<string>();
        var seg = content.Split('~', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length == 0 || !seg[0].StartsWith("ISA*") || seg[0].Length != 105)
            problems.Add("ISA must be the first segment and 106 characters with its terminator");
        if (seg.Count(s => s.StartsWith("GS*")) != 1) problems.Add("exactly one GS-GE group per file");
        var isa13 = seg[0].Split('*')[13];
        if (seg[^1] != $"IEA*1*{isa13}") problems.Add("IEA must close the ISA with the same control number");

        var gs = seg.First(s => s.StartsWith("GS*")).Split('*');
        if (gs[8] != version) problems.Add($"GS08 {gs[8]} is not {version}");
        var ge = seg.First(s => s.StartsWith("GE*")).Split('*');

        var stIndexes = seg.Select((s, i) => (s, i)).Where(x => x.s.StartsWith("ST*")).Select(x => x.i).ToList();
        if (stIndexes.Count > 5000) problems.Add("more than 5000 transactions in one file");
        if (ge[1] != stIndexes.Count.ToString() || ge[2] != gs[6]) problems.Add("GE count or control number is wrong");

        var st02s = new HashSet<string>();
        foreach (var start in stIndexes)
        {
            var st = seg[start].Split('*');
            if (!st02s.Add(st[2])) problems.Add($"ST02 {st[2]} repeats");
            if (st[3] != version) problems.Add($"ST03 {st[3]} is not {version}");
            var end = Array.FindIndex(seg, start, s => s.StartsWith("SE*"));
            var se = seg[end].Split('*');
            if (se[1] != (end - start + 1).ToString() || se[2] != st[2])
                problems.Add($"SE for ST02 {st[2]} has the wrong count or control number");
            foreach (var clm in seg[start..end].Where(s => s.StartsWith("CLM*")))
            {
                var clm01 = clm.Split('*')[1];
                if (clm01.Length > 20) problems.Add($"CLM01 {clm01} is longer than TMHP echoes back (20)");
            }
            if (!seg[start..end].Any(s => s.StartsWith("BHT*0019*00*") && s.EndsWith("*RP")))
                problems.Add($"ST02 {st[2]} is not an encounter (BHT06 RP)");
        }
        if (Regex.IsMatch(content, @"[\r\n]")) problems.Add("line breaks inside the interchange");
        return problems;
    }
}
