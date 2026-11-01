namespace ClaimsService.Services.Adjudication.Tpl;

/// <summary>
/// A state's services Medicaid pays without billing other insurance first
/// (pay and chase), on top of the federal pediatric preventive rule.
/// Configured under <c>Adjudication:ThirdPartyLiability:States:{XX}</c>;
/// codes there are added to the built-in list unless
/// <see cref="ReplaceBuiltIn"/> is set.
/// </summary>
public sealed class TplStateRules
{
    /// <summary>Procedure codes paid and chased at any age.</summary>
    public List<string> PayAndChaseProcedureCodes { get; set; } = new();

    /// <summary>Procedure codes paid and chased for members under the
    /// pediatric age limit only.</summary>
    public List<string> PayAndChaseUnderAgeProcedureCodes { get; set; } = new();

    /// <summary>Use only the configured lists, ignoring CHO's built-in ones.</summary>
    public bool ReplaceBuiltIn { get; set; }

    /// <summary>Where the list comes from, repeated in the claim's pay-and-chase reason.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Built-in lists. Texas from TMPPM vol. 1 §8.3 (May 2026): THSteps
    /// medical and dental providers, Case Management for Children and
    /// Pregnant Women and ECI need not bill other insurance first. SHARS is
    /// carved out of managed care, so it is not listed. The codes are CHO's
    /// mapping of those programs and should be checked against the THSteps and
    /// ECI handbooks. California adds nothing beyond federal law by default:
    /// APL 21-002 lets providers bill Medi-Cal directly for services on a
    /// DHCS list it does not reproduce, so plans configure that list.
    /// </summary>
    public static IReadOnlyDictionary<string, TplStateRules> BuiltIn { get; } =
        new Dictionary<string, TplStateRules>(StringComparer.OrdinalIgnoreCase)
        {
            ["TX"] = new()
            {
                Authority = "TMPPM 8.3",
                PayAndChaseProcedureCodes =
                [
                    "G9012", // Case Management for Children and Pregnant Women
                    "T1017", // ECI targeted case management
                    "H2014", // ECI specialized skills training
                ],
                PayAndChaseUnderAgeProcedureCodes =
                [
                    // THSteps dental checkups and preventive services
                    "D0120", "D0145", "D0150", "D1110", "D1120", "D1206", "D1208", "D1351", "D1354",
                ],
            },
            ["CA"] = new() { Authority = "APL 21-002" },
        };

    /// <summary>The effective rules for <paramref name="state"/>: built-in plus configured.</summary>
    public static TplStateRules For(string? state, IReadOnlyDictionary<string, TplStateRules>? configured)
    {
        if (string.IsNullOrWhiteSpace(state)) return new TplStateRules();
        BuiltIn.TryGetValue(state, out var builtIn);
        TplStateRules? custom = null;
        if (configured is not null)
            custom = configured.FirstOrDefault(kv => string.Equals(kv.Key, state, StringComparison.OrdinalIgnoreCase)).Value;

        if (custom is null) return builtIn ?? new TplStateRules();
        if (custom.ReplaceBuiltIn || builtIn is null) return custom;
        return new TplStateRules
        {
            Authority = string.IsNullOrWhiteSpace(custom.Authority) ? builtIn.Authority : custom.Authority,
            PayAndChaseProcedureCodes = builtIn.PayAndChaseProcedureCodes.Concat(custom.PayAndChaseProcedureCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PayAndChaseUnderAgeProcedureCodes = builtIn.PayAndChaseUnderAgeProcedureCodes.Concat(custom.PayAndChaseUnderAgeProcedureCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>True when the state pays this line and recovers later.</summary>
    public bool PaysAndChases(string? procedureCode, bool underAge)
    {
        var code = (procedureCode ?? string.Empty).Trim();
        if (code.Length == 0) return false;
        return PayAndChaseProcedureCodes.Contains(code, StringComparer.OrdinalIgnoreCase)
               || (underAge && PayAndChaseUnderAgeProcedureCodes.Contains(code, StringComparer.OrdinalIgnoreCase));
    }
}
