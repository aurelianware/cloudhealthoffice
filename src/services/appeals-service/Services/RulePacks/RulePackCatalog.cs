using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppealsService.Models;

namespace AppealsService.Services.RulePacks;

/// <summary>The rule packs this service knows, looked up by State, line of business and date.</summary>
public interface IRulePackCatalog
{
    IReadOnlyList<RulePack> All { get; }

    /// <summary>The pack in effect for <paramref name="state"/> on <paramref name="asOf"/>, or <c>null</c>.</summary>
    RulePack? Find(string state, LineOfBusiness lineOfBusiness, DateOnly asOf);
}

/// <summary>
/// Rule packs loaded from the JSON files embedded under <c>RulePacks/</c>.
/// Every pack is validated when the catalog is built
/// (<see cref="RulePackValidator"/>), and so is the catalog as a whole: two
/// versions of one State's pack may not overlap in time. Any problem fails
/// startup instead of silently applying a wrong clock, the same posture as
/// <see cref="ConfiguredAppealHolidayCalendarProvider"/>. Unknown JSON
/// properties are rejected, so a misspelled field cannot be ignored.
/// </summary>
public sealed class RulePackCatalog : IRulePackCatalog
{
    internal const string ResourcePrefix = "AppealsService.RulePacks.";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly List<RulePack> _packs;

    public RulePackCatalog(IEnumerable<RulePack> packs)
    {
        _packs = packs
            .OrderBy(p => p.State, StringComparer.Ordinal)
            .ThenBy(p => p.LineOfBusiness)
            .ThenBy(p => p.EffectiveFrom)
            .ToList();

        var errors = _packs.SelectMany(RulePackValidator.Validate).ToList();

        foreach (var duplicate in _packs.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            errors.Add($"{duplicate.Key}: id is used by more than one pack.");

        foreach (var series in _packs.GroupBy(p => (p.State, p.LineOfBusiness)))
        {
            var ordered = series.OrderBy(p => p.EffectiveFrom).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1];
                if (previous.EffectiveTo is null || previous.EffectiveTo >= ordered[i].EffectiveFrom)
                    errors.Add($"{ordered[i].Id}: overlaps {previous.Id} for {series.Key.State} {series.Key.LineOfBusiness}.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Appeal rule packs failed validation:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }
    }

    /// <summary>The packs embedded in this assembly.</summary>
    public static RulePackCatalog FromEmbeddedResources() => new(LoadEmbedded(typeof(RulePackCatalog).Assembly));

    public IReadOnlyList<RulePack> All => _packs;

    public RulePack? Find(string state, LineOfBusiness lineOfBusiness, DateOnly asOf) =>
        _packs.FirstOrDefault(p =>
            string.Equals(p.State, state, StringComparison.OrdinalIgnoreCase)
            && p.LineOfBusiness == lineOfBusiness
            && p.AppliesOn(asOf));

    internal static IEnumerable<RulePack> LoadEmbedded(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Rule pack resource '{name}' could not be opened.");
            yield return Parse(stream, name);
        }
    }

    internal static RulePack Parse(Stream json, string name)
    {
        try
        {
            return JsonSerializer.Deserialize<RulePack>(json, JsonOptions)
                ?? throw new InvalidOperationException($"Rule pack '{name}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Rule pack '{name}' is not valid: {ex.Message}", ex);
        }
    }
}
