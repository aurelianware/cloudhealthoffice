namespace CloudHealthOffice.Infrastructure.Programs;

/// <summary>
/// The one line-of-business type for the platform (docs/architecture/line-of-business.md).
/// A member's line of business comes from their program in tenant-service
/// (coverage.PlanId → BenefitPlan.ProgramId → PlanProgram.LineOfBusiness); a service that
/// stores this value keeps a cached copy of that.
/// </summary>
/// <remarks>
/// The numbers are the ones 15 services already store in Mongo and send as JSON
/// (Commercial=1 … VA=6), so swapping a service's own enum for this one changes no data.
/// Never renumber. Part D is not a line of business (it is a Medicare benefit type), and
/// STAR, CHIP and other Medicaid sub-programs are programs, not lines of business.
/// </remarks>
public enum LineOfBusiness
{
    Unknown = 0,
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,

    /// <summary>ACA individual and small-group exchange (QHP) plans. Older code calls this Marketplace.</summary>
    Exchange = 4,

    TRICARE = 5,
    VA = 6,
}

/// <summary>Reading and writing <see cref="LineOfBusiness"/> as text, including the legacy spellings still stored and in flight.</summary>
public static class LineOfBusinessNames
{
    private static readonly LineOfBusiness[] Known =
        [LineOfBusiness.Commercial, LineOfBusiness.Medicare, LineOfBusiness.Medicaid, LineOfBusiness.Exchange, LineOfBusiness.TRICARE, LineOfBusiness.VA];

    /// <summary>
    /// Spellings other services and stored documents use for the same line of business:
    /// appeals and payment-service say Marketplace, appeals says MedicarePartD (a Medicare
    /// benefit type, see <see cref="IsPartD"/>).
    /// </summary>
    private static readonly Dictionary<string, LineOfBusiness> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Marketplace"] = LineOfBusiness.Exchange,
        ["QHP"] = LineOfBusiness.Exchange,
        ["MedicarePartD"] = LineOfBusiness.Medicare,
        ["PartD"] = LineOfBusiness.Medicare,
        ["Medicare Part D"] = LineOfBusiness.Medicare,
        ["Tricare"] = LineOfBusiness.TRICARE,
    };

    /// <summary>The six real lines of business, in number order.</summary>
    public static IReadOnlyList<LineOfBusiness> All => Known;

    /// <summary>The canonical names (Commercial, Medicare, Medicaid, Exchange, TRICARE, VA).</summary>
    public static IReadOnlyList<string> AllNames { get; } = Known.Select(l => l.ToString()).ToArray();

    /// <summary>
    /// Reads a canonical name, a legacy spelling (Marketplace, MedicarePartD), or a number
    /// 1-6 as the 1-6 services send it. Case and surrounding spaces are ignored. Unknown,
    /// 0 and anything else are false.
    /// </summary>
    public static bool TryParse(string? value, out LineOfBusiness lineOfBusiness)
    {
        lineOfBusiness = LineOfBusiness.Unknown;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();

        if (int.TryParse(text, out var n))
        {
            if (n < 1 || n > Known.Length) return false;
            lineOfBusiness = (LineOfBusiness)n;
            return true;
        }
        if (Aliases.TryGetValue(text, out var alias))
        {
            lineOfBusiness = alias;
            return true;
        }
        foreach (var known in Known)
        {
            if (string.Equals(known.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                lineOfBusiness = known;
                return true;
            }
        }
        return false;
    }

    /// <summary>The line of business for <paramref name="value"/>, or Unknown when it is not one.</summary>
    public static LineOfBusiness Parse(string? value) => TryParse(value, out var lob) ? lob : LineOfBusiness.Unknown;

    /// <summary>The canonical name for <paramref name="value"/>; null when it is not a line of business.</summary>
    public static string? Normalize(string? value) => TryParse(value, out var lob) ? lob.ToString() : null;

    /// <summary>
    /// True for the legacy spellings that meant a Medicare Part D (drug) case. The line of
    /// business is Medicare; callers that need Part D rules keep this as a benefit type.
    /// </summary>
    public static bool IsPartD(string? value)
        => value is not null
           && Aliases.TryGetValue(value.Trim(), out var lob)
           && lob == LineOfBusiness.Medicare;
}
