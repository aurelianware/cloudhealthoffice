using System.Reflection;
using System.Text;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace FhirService.Services.Dtr;

/// <summary>
/// The DTR content CHO ships: the questionnaires, CQL Libraries and ValueSets
/// in <c>src/services/fhir-service/DtrContent/</c>, embedded in fhir-service.dll
/// under the logical-name prefix <c>DtrContent.</c>.
///
/// <para>
/// Content, not an engine (decision Q-6): CHO never runs this CQL. The
/// provider's DTR client runs it against the EHR's data. CHO's job is to hand a
/// client everything it needs in one <c>$questionnaire-package</c> response:
/// the Questionnaire, every Library it names (with both CQL and ELM, DTR 2.2.0
/// oper-13) and every ValueSet those depend on, expanded (oper-15).
/// </para>
///
/// <para>
/// Each <c>Library-Name.json</c> holds the Library's metadata only. Its two
/// content attachments are built here from <c>Name.cql</c> and
/// <c>Name.elm.json</c>, so the CQL lives in exactly one place and the ELM is
/// whatever <c>scripts/dtr/compile-cql.sh</c> produced from it.
/// </para>
///
/// Parse failures throw, so fhir-service does not start with broken content.
/// </summary>
public sealed class DtrContentLibrary
{
    internal const string ResourcePrefix = "DtrContent.";

    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = false });

    private static readonly Lazy<DtrContentLibrary> Shipped =
        new(() => new DtrContentLibrary(typeof(DtrContentLibrary).Assembly));

    /// <summary>The content embedded in this build of fhir-service.</summary>
    public static DtrContentLibrary Default => Shipped.Value;

    private readonly List<Questionnaire> _questionnaires = new();
    private readonly List<Library> _libraries = new();
    private readonly List<ValueSet> _valueSets = new();

    /// <summary>Loads every <c>DtrContent.*</c> resource in <paramref name="assembly"/>.</summary>
    internal DtrContentLibrary(Assembly assembly)
    {
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in names.Where(n => n.EndsWith(".json", StringComparison.Ordinal)
                                              && !n.EndsWith(".elm.json", StringComparison.Ordinal))
                                  .OrderBy(n => n, StringComparer.Ordinal))
        {
            Resource resource;
            try
            {
                resource = Parser.Parse<Resource>(Read(assembly, name));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse DTR content {name}: {ex.Message}", ex);
            }

            switch (resource)
            {
                case Questionnaire q:
                    _questionnaires.Add(q);
                    break;
                case Library library:
                    AttachLogic(assembly, names, library);
                    _libraries.Add(library);
                    break;
                case ValueSet vs:
                    _valueSets.Add(vs);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"DTR content {name} is a {resource.TypeName}; only Questionnaire, Library and ValueSet are shipped.");
            }
        }
    }

    /// <summary>Questionnaires seeded into the read-only default library.</summary>
    public IReadOnlyList<Questionnaire> Questionnaires => _questionnaires.Select(Copy).ToList();

    public IReadOnlyList<Library> Libraries => _libraries.Select(Copy).ToList();

    public IReadOnlyList<ValueSet> ValueSets => _valueSets.Select(Copy).ToList();

    /// <summary>The Library with this canonical (<c>url</c> or <c>url|version</c>), or null.</summary>
    public Library? ResolveLibrary(string canonical)
        => Resolve(_libraries, canonical, l => l.Url, l => l.Version) is { } l ? Copy(l) : null;

    /// <summary>The ValueSet with this canonical (<c>url</c> or <c>url|version</c>), or null.</summary>
    public ValueSet? ResolveValueSet(string canonical)
        => Resolve(_valueSets, canonical, v => v.Url, v => v.Version) is { } v ? Copy(v) : null;

    /// <summary>Splits <c>url|version</c>. The version is null when absent.</summary>
    public static (string Url, string? Version) SplitCanonical(string canonical)
    {
        var bar = canonical.IndexOf('|');
        return bar < 0 ? (canonical, null) : (canonical[..bar], canonical[(bar + 1)..]);
    }

    /// <summary>
    /// The resource with this url and, when given, exactly this version. Without a
    /// version, the highest version wins (oper-6: "current version if no version
    /// is indicated").
    /// </summary>
    private static T? Resolve<T>(IEnumerable<T> items, string canonical, Func<T, string?> url, Func<T, string?> version)
        where T : class
    {
        var (u, v) = SplitCanonical(canonical);
        var matches = items.Where(i => string.Equals(url(i), u, StringComparison.Ordinal));
        return v is null
            ? matches.OrderByDescending(i => version(i), VersionComparer.Instance).FirstOrDefault()
            : matches.FirstOrDefault(i => string.Equals(version(i), v, StringComparison.Ordinal));
    }

    private static void AttachLogic(Assembly assembly, HashSet<string> names, Library library)
    {
        var cqlName = $"{ResourcePrefix}{library.Name}.cql";
        var elmName = $"{ResourcePrefix}{library.Name}.elm.json";
        if (!names.Contains(cqlName) || !names.Contains(elmName))
        {
            throw new InvalidOperationException(
                $"DTR Library {library.Name} needs both {library.Name}.cql and {library.Name}.elm.json " +
                "(run scripts/dtr/compile-cql.sh).");
        }

        library.Content = new List<Attachment>
        {
            new() { ContentType = "text/cql", Data = Encoding.UTF8.GetBytes(Read(assembly, cqlName)) },
            new() { ContentType = "application/elm+json", Data = Encoding.UTF8.GetBytes(Read(assembly, elmName)) },
        };
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Failed to open embedded DTR content {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static T Copy<T>(T resource) where T : Resource => (T)resource.DeepCopy();

    /// <summary>
    /// Orders dotted numeric versions numerically (1.10.0 after 1.9.0) and falls
    /// back to ordinal comparison for anything else.
    /// </summary>
    internal sealed class VersionComparer : IComparer<string?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x == y) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (Version.TryParse(Pad(x), out var vx) && Version.TryParse(Pad(y), out var vy))
                return vx.CompareTo(vy);
            return string.CompareOrdinal(x, y);
        }

        private static string Pad(string v) => v.Contains('.') ? v : v + ".0";
    }
}
