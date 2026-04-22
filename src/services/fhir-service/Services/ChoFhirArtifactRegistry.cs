using System.Reflection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace FhirService.Services;

/// <summary>
/// Loads CHO-authored FHIR conformance artifacts from embedded resources
/// (under logical name prefix <c>FhirArtifacts.</c>) at construction time.
/// Parse failures throw — fhir-service fails to start if any shipped
/// artifact is invalid, so bad profiles cannot reach production silently.
/// </summary>
public sealed class ChoFhirArtifactRegistry : IChoFhirArtifactRegistry
{
    private const string ResourcePrefix = "FhirArtifacts.";

    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = false });

    private readonly Dictionary<string, StructureDefinition>  _structureDefinitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeSystem>           _codeSystems          = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ValueSet>             _valueSets            = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationDefinition>  _operationDefinitions = new(StringComparer.Ordinal);

    public ChoFhirArtifactRegistry(ILogger<ChoFhirArtifactRegistry> logger)
    {
        var assembly = typeof(ChoFhirArtifactRegistry).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                     && n.EndsWith(".json", StringComparison.Ordinal))
            .ToArray();

        if (resourceNames.Length == 0)
        {
            logger.LogWarning(
                "ChoFhirArtifactRegistry found no embedded FHIR artifacts. " +
                "Build system may not have run the CopyFhirArtifacts target.");
        }

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Failed to open embedded resource {name}");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            Resource parsed;
            try
            {
                parsed = Parser.Parse<Resource>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse CHO FHIR artifact {name}: {ex.Message}", ex);
            }

            switch (parsed)
            {
                case StructureDefinition sd when !string.IsNullOrEmpty(sd.Id):
                    _structureDefinitions[sd.Id] = sd;
                    break;
                case CodeSystem cs when !string.IsNullOrEmpty(cs.Id):
                    _codeSystems[cs.Id] = cs;
                    break;
                case ValueSet vs when !string.IsNullOrEmpty(vs.Id):
                    _valueSets[vs.Id] = vs;
                    break;
                case OperationDefinition od when !string.IsNullOrEmpty(od.Id):
                    _operationDefinitions[od.Id] = od;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"CHO FHIR artifact {name} has unsupported resource type " +
                        $"{parsed.TypeName} or missing id");
            }
        }

        logger.LogInformation(
            "ChoFhirArtifactRegistry loaded {Sd} StructureDefinitions, " +
            "{Cs} CodeSystems, {Vs} ValueSets, {Od} OperationDefinitions",
            _structureDefinitions.Count,
            _codeSystems.Count,
            _valueSets.Count,
            _operationDefinitions.Count);
    }

    public StructureDefinition? GetStructureDefinition(string id)
        => _structureDefinitions.TryGetValue(id, out var sd) ? sd : null;

    public CodeSystem? GetCodeSystem(string id)
        => _codeSystems.TryGetValue(id, out var cs) ? cs : null;

    public ValueSet? GetValueSet(string id)
        => _valueSets.TryGetValue(id, out var vs) ? vs : null;

    public OperationDefinition? GetOperationDefinition(string id)
        => _operationDefinitions.TryGetValue(id, out var od) ? od : null;

    public IReadOnlyList<StructureDefinition> AllStructureDefinitions
        => _structureDefinitions.Values.OrderBy(sd => sd.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<CodeSystem> AllCodeSystems
        => _codeSystems.Values.OrderBy(cs => cs.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<ValueSet> AllValueSets
        => _valueSets.Values.OrderBy(vs => vs.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<OperationDefinition> AllOperationDefinitions
        => _operationDefinitions.Values.OrderBy(od => od.Id, StringComparer.Ordinal).ToList();
}
