using Hl7.Fhir.Model;

namespace FhirService.Services.PayerToPayer.Ingestion;

/// <summary>
/// Rewrites the references INSIDE an imported package so they still resolve once
/// the package is stored as individual CHO records.
///
/// A peer's Bundle mixes reference forms — relative (<c>Claim/123</c>), absolute
/// (<c>https://peer.example/fhir/Claim/123</c>), and urn:uuid entries. Stored
/// verbatim, the absolute ones would keep pointing at the other payer's server
/// and the relative ones would silently collide with CHO ids.
///
/// The rule is narrow on purpose: a reference is rewritten ONLY when it resolves
/// to another resource in the SAME package, and only to that resource's imported
/// identity. Anything else — a reference to a resource the peer did not send, an
/// external URL, a contained (<c>#…</c>) reference — is left exactly as it
/// arrived. CHO does not invent links that the source payer did not assert, and
/// never rewrites a reference blindly.
/// </summary>
public static class PayerToPayerReferenceNormalizer
{
    /// <summary>Prefix marking a reference as pointing at CHO's imported copy of a peer resource.</summary>
    public const string ImportedPrefix = "PayerToPayerImport";

    /// <summary>
    /// How a package's references were rewritten: the total, the source-to-local
    /// map, and which resources were actually touched (keyed <c>Type/id</c>) so a
    /// per-resource flag can be recorded truthfully.
    /// </summary>
    public sealed record NormalizationOutcome(
        int Rewritten,
        IReadOnlyDictionary<string, string> Map,
        IReadOnlySet<string> RewrittenResources);

    /// <summary>
    /// Rewrites in place every intra-package reference of <paramref name="bundle"/>
    /// to <c>PayerToPayerImport/{importKey}</c>, using the caller's key function
    /// (which binds tenant, member, and source payer). Returns how many were
    /// rewritten and the source-to-local map, so the mapping can be asserted and
    /// audited.
    /// </summary>
    public static NormalizationOutcome Normalize(
        Bundle bundle, Func<string, string, string> importKeyFor)
    {
        // What the package actually contains: "Type/id" for every entry.
        var present = new Dictionary<string, (string Type, string Id)>(StringComparer.Ordinal);
        foreach (var resource in bundle.Entry?.Select(e => e.Resource).OfType<Resource>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(resource.Id)) continue;
            present[$"{resource.TypeName}/{resource.Id}"] = (resource.TypeName, resource.Id);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var rewrittenResources = new HashSet<string>(StringComparer.Ordinal);
        var rewritten = 0;

        foreach (var resource in bundle.Entry?.Select(e => e.Resource).OfType<Resource>() ?? [])
        {
            foreach (var reference in References(resource))
            {
                var original = reference.Reference;
                if (string.IsNullOrWhiteSpace(original)) continue;

                // A contained reference is local to its own resource; rewriting it
                // would break the containment it describes.
                if (original.StartsWith('#')) continue;

                var relative = ToRelative(original);
                if (relative is null || !present.TryGetValue(relative, out var target)) continue;

                var local = $"{ImportedPrefix}/{importKeyFor(target.Type, target.Id)}";
                reference.Reference = local;
                map[original] = local;
                rewrittenResources.Add($"{resource.TypeName}/{resource.Id}");
                rewritten++;
            }
        }

        return new NormalizationOutcome(rewritten, map, rewrittenResources);
    }

    /// <summary>
    /// The <c>Type/id</c> form of a reference, whether it arrived relative or as
    /// an absolute URL. Returns null for anything that is not a plain resource
    /// reference (a urn:uuid, a query, a bare id).
    /// </summary>
    internal static string? ToRelative(string reference)
    {
        var value = reference.Trim();
        if (value.Length == 0) return null;

        // Drop a version suffix: Type/id/_history/2 identifies the same resource.
        var historyAt = value.IndexOf("/_history/", StringComparison.Ordinal);
        if (historyAt >= 0) value = value[..historyAt];

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        var id = segments[^1];
        var type = segments[^2];

        // A FHIR resource type is upper-camel; this also rejects "…/fhir/r4" tails
        // and urn:uuid forms, which are not resource references.
        if (type.Length == 0 || !char.IsUpper(type[0]) || id.Length == 0) return null;
        if (value.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) return null;

        return $"{type}/{id}";
    }

    /// <summary>Every ResourceReference inside a resource, walked over the FHIR model.</summary>
    private static IEnumerable<ResourceReference> References(Base element)
    {
        if (element is ResourceReference reference) yield return reference;

        foreach (var child in element.Children)
        {
            foreach (var nested in References(child))
                yield return nested;
        }
    }
}
