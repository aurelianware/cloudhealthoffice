using CloudHealthOffice.EncounterEngine.Domain;

namespace CloudHealthOffice.EncounterEngine.Services;

/// <summary>
/// Produces an X12 005010X222A2 (837P) or 005010X223A3 (837I) transaction
/// from an adjudicated claim.
///
/// Segment reference:
///   BHT — transaction purpose / control number
///   NM1*41 — submitter (plan)
///   NM1*40 — receiver
///   NM1*85 — billing provider
///   NM1*82 — rendering provider (when different from billing)
///   NM1*IL — subscriber (member)
///   CLM    — claim level totals + frequency code
///   REF*F8 — original encounter control number (corrected/void only)
///   DTP*472 — date of service
///   HI     — diagnosis codes (ICD-10-CM)
///   SV1/SV2 — service line (professional / institutional)
///   SV2 + revenue code for 837I lines
///   AMT*D  — billed per line (837I)
///   QTY*CA — covered days (837I inpatient)
///   OI     — other insurance (COB payer hierarchy)
///   NM1*TT — other payer name (COB)
///   MOA    — Medicare outpatient adjudication (MA01-MA18 + amounts)
///   MIA    — Medicare inpatient adjudication (837I)
/// </summary>
public class EncounterTransformer : IEncounterTransformer
{
    private static readonly string Eol = "\n";

    public EncounterRecord Transform(EncounterInput input)
    {
        var ecn = GenerateControlNumber(input.ClaimId);
        var now = DateTime.UtcNow;
        var dateStamp = now.ToString("yyyyMMdd");
        var timeStamp = now.ToString("HHmm");

        var seg = new List<string>();

        // ── Transaction header ───────────────────────────────────────────────
        var transactionSetId = input.FormType == ClaimFormType.Institutional ? "837" : "837";
        var implementationId  = input.FormType == ClaimFormType.Institutional
            ? "005010X223A3" : "005010X222A2";

        seg.Add($"ST*{transactionSetId}*{ecn:D4}*{implementationId}");
        seg.Add($"BHT*0019*{FrequencyPurpose(input.FrequencyCode)}*{ecn}*{dateStamp}*{timeStamp}*CH");

        // ── Loop 1000A — Submitter (Plan) ────────────────────────────────────
        seg.Add($"NM1*41*2*{EscapeX12(input.PlanName)}*****XX*{input.PlanSubmitterId}");
        seg.Add($"PER*IC*EDI CONTACT*TE*0000000000");

        // ── Loop 1000B — Receiver ────────────────────────────────────────────
        seg.Add($"NM1*40*2*RECEIVER*****46*{input.ReceiverSubmitterId}");

        // ── Loop 2000A — Billing Provider ────────────────────────────────────
        seg.Add($"NM1*85*2*{EscapeX12(input.BillingProviderName)}*****XX*{input.BillingNpi}");
        seg.Add($"REF*EI*{input.BillingTaxId}");

        // ── Loop 2000B — Subscriber (Member as subscriber) ───────────────────
        // SBR01: payer responsibility (P=primary for encounter, but encounter represents
        //        the plan's submission to the regulatory receiver, so always P here)
        seg.Add($"SBR*P*18*{input.SubscriberId}***HM**MB");
        seg.Add($"NM1*IL*1*{EscapeX12(input.MemberLastName)}*{EscapeX12(input.MemberFirstName)}****MI*{input.MemberId}");
        seg.Add($"DMG*D8*{input.MemberDateOfBirth:yyyyMMdd}*{input.MemberGender}");

        // ── Loop 2000B — Payer (the regulatory receiver acts as "payer" in 2010BB) ─
        seg.Add($"NM1*PR*2*{EscapeX12(input.PlanName)}*****PI*{input.PlanPayerId}");

        // ── Loop 2300 — Claim ─────────────────────────────────────────────────
        var totalBilled = input.Lines.Sum(l => l.BilledAmount);
        var totalPlanPaid = input.Lines.Sum(l => l.PlanPaidAmount);
        var totalMemberResp = input.Lines.Sum(l => l.MemberResponsibility);

        // CLM05-1 = POS, CLM05-2 = "", CLM05-3 = frequency code
        var clmPos = input.FormType == ClaimFormType.Institutional ? "21" : input.PlaceOfService;
        seg.Add($"CLM*{input.ClaimId}*{totalBilled:0.00}***{clmPos}::B*Y*A*Y*I");

        // REF*F8 — original encounter control number (corrected/void)
        if (input.FrequencyCode != ClaimFrequencyCode.Original &&
            !string.IsNullOrWhiteSpace(input.OriginalEncounterControlNumber))
        {
            seg.Add($"REF*F8*{input.OriginalEncounterControlNumber}");
        }

        // DTP — date of service
        seg.Add($"DTP*472*D8*{input.ServiceDate:yyyyMMdd}");

        // Institutional: admit and discharge dates
        if (input.FormType == ClaimFormType.Institutional)
        {
            if (input.AdmitDate.HasValue)
                seg.Add($"DTP*435*D8*{input.AdmitDate.Value:yyyyMMdd}");
            if (input.DischargeDate.HasValue)
                seg.Add($"DTP*096*D8*{input.DischargeDate.Value:yyyyMMdd}");
        }

        // HI — diagnosis codes (ICD-10 qualifier ABK = principal, ABF = additional)
        if (input.DiagnosisCodes.Count > 0)
        {
            var hiParts = new List<string> { $"ABK:{EscapeX12(input.DiagnosisCodes[0])}" };
            for (int i = 1; i < input.DiagnosisCodes.Count; i++)
                hiParts.Add($"ABF:{EscapeX12(input.DiagnosisCodes[i])}");
            seg.Add("HI*" + string.Join("*", hiParts));
        }

        // DRG (institutional)
        if (!string.IsNullOrWhiteSpace(input.DrgCode))
            seg.Add($"HI*BG:{input.DrgCode}");

        // AMT — total claim-level amounts for encounter reporting
        seg.Add($"AMT*AU*{totalPlanPaid:0.00}");    // AU = covered amount (plan paid)
        seg.Add($"AMT*NE*{totalMemberResp:0.00}");  // NE = non-covered / member responsibility

        // ── COB — Other Insurance (OI loop) ──────────────────────────────────
        if (input.Cob is not null)
        {
            // OI01=blank OI02=blank OI03=Y(benefits assigned) OI04=blank OI05=blank OI06=Y(release of info)
            seg.Add($"OI***Y**Y*Y");
            seg.Add($"NM1*TT*2*{EscapeX12(input.Cob.OtherPayerName)}*****PI*{input.Cob.OtherPayerId}");
            seg.Add($"AMT*D*{input.Cob.OtherPayerPaidAmount:0.00}"); // D = payer prior payment
        }

        // MOA / MIA — adjudication summary for encounter receivers that require it
        // MOA (outpatient/professional) — up to 5 remark codes + reimbursement amount
        if (input.FormType == ClaimFormType.Professional)
        {
            // MOA01-05: remark codes (blank); MOA06: reimbursement amount; MOA07-09: blank
            seg.Add($"MOA***{totalPlanPaid:0.00}");
        }
        else
        {
            // MIA (institutional inpatient)
            // MIA01: covered days; MIA02: blank; MIA03: DRG; MIA04: reimbursement amount
            var coveredDays = input.AdmitDate.HasValue && input.DischargeDate.HasValue
                ? (input.DischargeDate.Value.DayNumber - input.AdmitDate.Value.DayNumber)
                : 0;
            seg.Add($"MIA*{coveredDays}**{input.DrgCode ?? ""}*{totalPlanPaid:0.00}");
        }

        // Rendering provider (2310B) — when different from billing
        if (!string.IsNullOrWhiteSpace(input.RenderingNpi) &&
            input.RenderingNpi != input.BillingNpi)
        {
            seg.Add($"NM1*82*1*{EscapeX12(input.RenderingProviderLastName ?? "")}*{EscapeX12(input.RenderingProviderFirstName ?? "")}***XX*{input.RenderingNpi}");
        }

        // ── Loop 2400 — Service Lines ─────────────────────────────────────────
        foreach (var line in input.Lines)
        {
            seg.Add($"LX*{line.LineNumber}");

            if (input.FormType == ClaimFormType.Professional)
            {
                // SV1*HC:code[:mod1[:mod2]]*billed*UN*units**diag-ptrs
                var procWithMods = BuildProcWithModifiers(line.CodeType ?? "HC", line.ProcedureCode, line.Modifiers);
                var diagPtrs = string.Join(":", line.DiagnosisPointers);
                seg.Add($"SV1*{procWithMods}*{line.BilledAmount:0.00}*UN*{line.Units:0.##}**{diagPtrs}");
            }
            else
            {
                // SV2*revenue*HC:code*billed*UN*units
                var procWithMods = BuildProcWithModifiers(line.CodeType ?? "HC", line.ProcedureCode, line.Modifiers);
                seg.Add($"SV2*{line.RevenueCode ?? "0001"}*{procWithMods}*{line.BilledAmount:0.00}*UN*{line.Units:0.##}");
            }

            seg.Add($"DTP*472*D8*{input.ServiceDate:yyyyMMdd}");

            // AMT — adjudicated amounts per line
            seg.Add($"AMT*AU*{line.PlanPaidAmount:0.00}");     // plan paid
            seg.Add($"AMT*B6*{line.AllowedAmount:0.00}");      // allowed

            if (line.DeductibleAmount > 0)
                seg.Add($"AMT*A8*{line.DeductibleAmount:0.00}");
            if (line.CoinsuranceAmount > 0)
                seg.Add($"AMT*A1*{line.CoinsuranceAmount:0.00}");
            if (line.CopayAmount > 0)
                seg.Add($"AMT*F4*{line.CopayAmount:0.00}");

            // COB per-line amounts
            if (line.PrimaryPayerPayment > 0)
                seg.Add($"AMT*D*{line.PrimaryPayerPayment:0.00}");
        }

        // ── Transaction trailer ───────────────────────────────────────────────
        var segmentCount = seg.Count + 1; // +1 for SE itself
        seg.Add($"SE*{segmentCount}*{ecn:D4}");

        var rawX12 = string.Join(Eol, seg) + Eol;

        return new EncounterRecord
        {
            EncounterControlNumber = ecn,
            ClaimId        = input.ClaimId,
            TenantId       = input.TenantId,
            Status         = EncounterStatus.Pending,
            FormType       = input.FormType,
            ServiceDate    = input.ServiceDate,
            RawX12         = rawX12,
            TotalBilled    = totalBilled,
            TotalPlanPaid  = totalPlanPaid,
            TotalMemberResponsibility = totalMemberResp
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// BHT02 purpose code:
    ///   00 = original; 18 = reissue (corrected); 22 = duplicate (void).
    /// </summary>
    private static string FrequencyPurpose(ClaimFrequencyCode code) => code switch
    {
        ClaimFrequencyCode.Original   => "00",
        ClaimFrequencyCode.Corrected  => "18",
        ClaimFrequencyCode.Void       => "22",
        _ => "00"
    };

    /// <summary>
    /// Produces a deterministic encounter control number from the claim ID.
    /// Real implementations would use a sequence generator; this uses a stable hash
    /// so tests are repeatable without external state.
    /// </summary>
    internal static string GenerateControlNumber(string claimId)
    {
        // 9-digit numeric ECN derived from absolute hash (avoids negative values)
        var hash = Math.Abs(claimId.GetHashCode()) % 1_000_000_000;
        return hash.ToString("D9");
    }

    private static string BuildProcWithModifiers(string codeType, string code, IReadOnlyList<string> modifiers)
    {
        // SV1 format: HC:PROC:MOD1:MOD2:MOD3:MOD4
        var parts = new List<string> { codeType, code };
        parts.AddRange(modifiers.Take(4));
        return string.Join(":", parts);
    }

    private static string EscapeX12(string value) =>
        value.Replace("*", "").Replace("~", "").Replace("\n", "").Replace("\r", "");
}
