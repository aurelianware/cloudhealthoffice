using System.Text.Json;

namespace PremiumBillingService.Rating;

/// <summary>
/// Age rating factors as data. The federal default curve ships as
/// <c>Rating/AgeCurves/federal-default.json</c>; a state curve (or a later
/// federal one) is supplied on the rate table or loaded with <see cref="FromJson"/>.
/// </summary>
public sealed class AgeCurve
{
    private static readonly Lazy<AgeCurve> Federal = new(() => LoadEmbedded("federal-default.json"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public string Name { get; set; } = string.Empty;

    public string? Source { get; set; }

    public List<AgeBandFactor> Bands { get; set; } = new();

    /// <summary>The CMS federal default age curve (plan years 2018 onward).</summary>
    public static AgeCurve FederalDefault => Federal.Value;

    public static AgeCurve FromJson(string json)
    {
        var curve = JsonSerializer.Deserialize<AgeCurve>(json, JsonOptions)
                    ?? throw new FormatException("Age curve JSON is empty");
        var problems = curve.Problems().ToList();
        if (problems.Count > 0)
            throw new FormatException($"Age curve '{curve.Name}' is invalid: {string.Join("; ", problems)}");
        return curve;
    }

    /// <summary>The factor for a member of this age.</summary>
    public decimal FactorFor(int age)
    {
        if (age < 0)
            throw new ArgumentOutOfRangeException(nameof(age), age, "Age cannot be negative");
        foreach (var band in Bands)
        {
            if (age >= band.MinAge && (band.MaxAge == null || age <= band.MaxAge.Value))
                return band.Factor;
        }
        throw new InvalidOperationException($"Age curve '{Name}' has no factor for age {age}");
    }

    /// <summary>Bands must start at 0, be contiguous, ascend, end open-ended and have positive factors.</summary>
    public IEnumerable<string> Problems()
    {
        if (Bands.Count == 0)
        {
            yield return "an age curve needs at least one band";
            yield break;
        }

        var ordered = Bands.OrderBy(b => b.MinAge).ToList();
        if (ordered[0].MinAge != 0)
            yield return "the first band must start at age 0";
        for (var i = 0; i < ordered.Count; i++)
        {
            var band = ordered[i];
            if (band.Factor <= 0)
                yield return $"band starting at {band.MinAge} has a non-positive factor";
            if (band.MaxAge.HasValue && band.MaxAge < band.MinAge)
                yield return $"band starting at {band.MinAge} ends before it starts";
            if (i < ordered.Count - 1)
            {
                if (band.MaxAge == null)
                    yield return $"band starting at {band.MinAge} is open-ended but is not the last band";
                else if (ordered[i + 1].MinAge != band.MaxAge + 1)
                    yield return $"gap or overlap after age {band.MaxAge}";
            }
            else if (band.MaxAge != null)
            {
                yield return "the last band must be open-ended (maxAge null)";
            }
        }

        // ACA adult ratio: for ages 21 and over, the highest factor may be at
        // most 3 times the lowest (45 CFR 147.102(a)(1)(iii)).
        var adult = ordered.Where(b => b.Factor > 0 && (b.MaxAge == null || b.MaxAge >= AdultAge)).Select(b => b.Factor).ToList();
        if (adult.Count > 0 && adult.Max() > MaxAdultRatio * adult.Min())
            yield return $"adult factors (ages {AdultAge}+) range {adult.Min()}–{adult.Max()}, more than {MaxAdultRatio}:1";
    }

    /// <summary>Youngest age the 3:1 adult ratio applies to.</summary>
    public const int AdultAge = 21;

    /// <summary>The ACA limit on the oldest-to-youngest adult factor.</summary>
    public const decimal MaxAdultRatio = 3.0m;

    private static AgeCurve LoadEmbedded(string fileName)
    {
        var resource = $"PremiumBillingService.Rating.AgeCurves.{fileName}";
        using var stream = typeof(AgeCurve).Assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Embedded age curve {resource} is missing");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }
}

public sealed class AgeBandFactor
{
    public int MinAge { get; set; }

    /// <summary>Inclusive upper age; null for the last, open-ended band.</summary>
    public int? MaxAge { get; set; }

    public decimal Factor { get; set; }
}
