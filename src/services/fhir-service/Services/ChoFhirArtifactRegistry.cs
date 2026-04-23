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

        // Fail fast on zero artifacts: the service advertises CHO profiles via
        // CapabilityStatement and serves them via conformance endpoints; a DLL
        // built without the embedded `FhirArtifacts.*.json` resources is
        // misconfigured and must not start silently.
        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException(
                "ChoFhirArtifactRegistry found no embedded FHIR artifacts " +
                $"(resource prefix '{ResourcePrefix}'). Verify that " +
                "fhir-service.csproj's <EmbeddedResource Include=\"...docs/fhir/profiles/*.json\"> " +
                "item group resolves and that docs/fhir/profiles/ is present in the build tree.");
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
                    AddUnique(_structureDefinitions, sd.Id, sd, name, "StructureDefinition");
                    break;
                case CodeSystem cs when !string.IsNullOrEmpty(cs.Id):
                    AddUnique(_codeSystems, cs.Id, cs, name, "CodeSystem");
                    break;
                case ValueSet vs when !string.IsNullOrEmpty(vs.Id):
                    AddUnique(_valueSets, vs.Id, vs, name, "ValueSet");
                    break;
                case OperationDefinition od when !string.IsNullOrEmpty(od.Id):
                    AddUnique(_operationDefinitions, od.Id, od, name, "OperationDefinition");
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

    // Canonical URLs and resource IDs are load-bearing: once a FHIR resource
    // claims conformance via meta.profile, the URL cannot change without
    // invalidating persisted data. Treat duplicate IDs as a fatal build error
    // rather than last-write-wins.
    private static void AddUnique<T>(
        Dictionary<string, T> bucket, string id, T resource,
        string resourceName, string resourceType) where T : Resource
    {
        if (bucket.ContainsKey(id))
        {
            throw new InvalidOperationException(
                $"Duplicate {resourceType} id '{id}' detected while loading " +
                $"'{resourceName}'. Each CHO FHIR artifact id must be unique " +
                $"because canonical URLs are permanent once resources claim " +
                $"conformance via meta.profile.");
        }

        bucket.Add(id, resource);
    }

    public IReadOnlyList<StructureDefinition> AllStructureDefinitions
        => _structureDefinitions.Values.OrderBy(sd => sd.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<CodeSystem> AllCodeSystems
        => _codeSystems.Values.OrderBy(cs => cs.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<ValueSet> AllValueSets
        => _valueSets.Values.OrderBy(vs => vs.Id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<OperationDefinition> AllOperationDefinitions
        => _operationDefinitions.Values.OrderBy(od => od.Id, StringComparer.Ordinal).ToList();
}
