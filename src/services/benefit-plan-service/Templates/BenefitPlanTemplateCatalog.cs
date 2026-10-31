using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Templates;

public interface IBenefitPlanTemplateCatalog
{
    IReadOnlyList<BenefitPlanTemplate> List(string? state = null, string? program = null);
    BenefitPlanTemplate? Get(string templateId);
}

/// <summary>
/// The state program templates shipped with the service, read once from
/// <c>{ContentRoot}/schemas/benefit-plan-templates/**/*.json</c>
/// (<c>BenefitPlanTemplates:Directory</c> overrides the path). A file that
/// does not parse, or two files with one template id, stops startup: a
/// silently missing template would leave a plan to be built by hand.
/// </summary>
public sealed class BenefitPlanTemplateCatalog : IBenefitPlanTemplateCatalog
{
    public const string DefaultDirectory = "schemas/benefit-plan-templates";

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, BenefitPlanTemplate> _byId;

    public BenefitPlanTemplateCatalog(IEnumerable<BenefitPlanTemplate> templates)
    {
        _byId = new Dictionary<string, BenefitPlanTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in templates)
        {
            if (string.IsNullOrWhiteSpace(t.TemplateId))
                throw new InvalidOperationException("A benefit plan template has no templateId.");
            if (!_byId.TryAdd(t.TemplateId, t))
                throw new InvalidOperationException($"Benefit plan template id '{t.TemplateId}' is defined twice.");
        }
    }

    public static BenefitPlanTemplateCatalog LoadFrom(string directory, ILogger? logger = null)
    {
        if (!Directory.Exists(directory))
        {
            logger?.LogWarning("Benefit plan template directory {Directory} not found; no templates loaded", directory);
            return new BenefitPlanTemplateCatalog([]);
        }

        var templates = new List<BenefitPlanTemplate>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var t = JsonSerializer.Deserialize<BenefitPlanTemplate>(File.ReadAllText(file), JsonOptions)
                        ?? throw new InvalidOperationException("empty file");
                templates.Add(t);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException($"Benefit plan template '{file}' could not be read: {ex.Message}", ex);
            }
        }

        var catalog = new BenefitPlanTemplateCatalog(templates);
        logger?.LogInformation("Loaded {Count} benefit plan templates from {Directory}", templates.Count, directory);
        return catalog;
    }

    public IReadOnlyList<BenefitPlanTemplate> List(string? state = null, string? program = null) =>
        _byId.Values
            .Where(t => string.IsNullOrWhiteSpace(state) || string.Equals(t.State, state.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(program) || string.Equals(t.Program, program.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.State).ThenBy(t => t.TemplateId, StringComparer.Ordinal)
            .ToList();

    public BenefitPlanTemplate? Get(string templateId) =>
        _byId.TryGetValue(templateId, out var t) ? t : null;
}
