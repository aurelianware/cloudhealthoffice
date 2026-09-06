using FhirService.Services.PayerToPayer.Ingestion;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace FhirService.Services.Clinical;

/// <summary>
/// Turns a stored clinical row into the FHIR resource Cloud Health Office
/// serves. Four things change between the store and the wire, and nothing else:
/// the clinical content the source payer sent is preserved exactly as it
/// arrived.
///
/// 1. IDENTITY. <c>Resource.id</c> becomes CHO's logical id, not the source
///    payer's. See <see cref="ClinicalResourceIdentity"/> for why the payer's
///    own id cannot be served.
///
/// 2. MEMBER BINDING. The subject/patient element is set to
///    <c>Patient/{member}</c> from the row's TRUSTED binding — the tenant and
///    member the exchange established — replacing whatever the payload named.
///    This is the whole point of the rule that imported <c>subject</c> is never
///    authorization authority: a package whose Observation claims another
///    member is filed, and served, under the member CHO resolved. The reader
///    therefore never sees a subject that disagrees with the record they are
///    authorized for.
///
/// 3. REFERENCES. Ingestion rewrote intra-package references to the local
///    identity <c>PayerToPayerImport/{id}</c> so they would stop pointing at the
///    other payer's server. Here they become real, resolvable FHIR references —
///    <c>{Type}/{id}</c> — but ONLY where the target is a clinical type CHO
///    genuinely serves and belongs to the same member. A reference CHO cannot
///    honour is left as the opaque local identity rather than dressed up as a
///    resolvable one, and a reference the payer left pointing outside the
///    package is untouched. CHO does not invent links.
///
/// 4. PROVENANCE. <c>meta.source</c> says where the resource came from and what
///    it was called there; <c>meta.lastUpdated</c> and <c>meta.versionId</c> say
///    which version this is. Imported data is therefore never indistinguishable
///    from CHO-authored data at the point a reader consumes it.
///
/// NO PROFILE IS CLAIMED. <c>meta.profile</c> is deliberately left alone. CHO
/// serves these as valid FHIR R4; it does not re-shape a prior payer's
/// Observation to satisfy US Core invariants, so stamping a US Core profile URL
/// on it would be a label, not conformance. See docs/architecture/clinical-fhir.md.
/// </summary>
public sealed class ClinicalResourceProjector
{
    /// <summary>URN scheme for <c>meta.source</c>. Opaque payer id — never an endpoint URL.</summary>
    public const string ImportedSourceScheme = "urn:cho:clinical:imported";

    /// <summary><c>meta.source</c> for data CHO authored itself.</summary>
    public const string NativeSource = "urn:cho:clinical:native";

    private static readonly FhirJsonParser Parser = new(new ParserSettings
    {
        // Stored JSON was produced by this service's own serializer from an
        // already-validated resource. Permissive parsing would let a row that
        // somehow became malformed be served as a half-resource; failing instead
        // surfaces it as an error the read path turns into a 404 plus an audit
        // line.
        PermissiveParsing = false,
        AcceptUnknownMembers = false,
    });

    /// <summary>
    /// Projects one stored row. Returns null when the stored payload cannot be
    /// read as the type the row claims — the caller treats that as "no resource"
    /// and audits it, rather than serving something partial.
    /// </summary>
    public Resource? Project(
        StoredClinicalResource stored,
        IReadOnlyDictionary<string, string>? referenceTypes = null)
    {
        Resource resource;
        try
        {
            resource = Parser.Parse<Resource>(stored.ResourceJson);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException
                                      or InvalidOperationException)
        {
            return null;
        }

        // The stored type is what the row is indexed and authorized as; a payload
        // that disagrees with it is not servable under that identity.
        if (!string.Equals(resource.TypeName, stored.ResourceType, StringComparison.Ordinal))
            return null;

        var entry = ClinicalResourceInventory.Find(stored.ResourceType);
        if (entry is null)
            return null;

        resource.Id = stored.ClinicalId;
        entry.BindSubject(resource, new ResourceReference($"Patient/{stored.MemberId}"));
        RewriteLocalReferences(resource, referenceTypes ?? new Dictionary<string, string>());
        resource.Meta = BuildMeta(stored);

        return resource;
    }

    /// <summary>
    /// Every local identity a resource still points at, so the caller can resolve
    /// them all in one store round trip instead of one per reference.
    /// </summary>
    public static IReadOnlyCollection<string> LocalReferenceIds(string resourceJson)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var marker = PayerToPayerReferenceNormalizer.ImportedPrefix + "/";
        var at = 0;

        while (true)
        {
            var found = resourceJson.IndexOf(marker, at, StringComparison.Ordinal);
            if (found < 0) break;

            var start = found + marker.Length;
            var end = start;
            while (end < resourceJson.Length && IsIdChar(resourceJson[end])) end++;
            if (end > start) ids.Add(resourceJson[start..end]);

            at = end > found ? end : found + marker.Length;
        }

        return ids;
    }

    private static bool IsIdChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '-' or '.';

    /// <summary>
    /// <c>PayerToPayerImport/{id}</c> becomes <c>{Type}/{id}</c> when the target
    /// is a clinical resource this member actually has. Everything else is left
    /// exactly as stored — including the subject element, which
    /// <see cref="Project"/> has already replaced with the trusted binding.
    /// </summary>
    private static void RewriteLocalReferences(
        Resource resource,
        IReadOnlyDictionary<string, string> referenceTypes)
    {
        if (referenceTypes.Count == 0) return;

        var marker = PayerToPayerReferenceNormalizer.ImportedPrefix + "/";

        foreach (var reference in AllReferences(resource))
        {
            var value = reference.Reference;
            if (value is null || !value.StartsWith(marker, StringComparison.Ordinal)) continue;

            var localId = value[marker.Length..];
            if (!referenceTypes.TryGetValue(localId, out var targetType)) continue;

            // Only a type CHO serves clinically: rewriting to a type with no read
            // path would produce a reference that looks resolvable and is not.
            if (!ClinicalResourceInventory.IsClinical(targetType)) continue;

            reference.Reference = $"{targetType}/{localId}";
        }
    }

    /// <summary>
    /// Provenance a reader can act on, with nothing that identifies the member or
    /// describes the content.
    /// </summary>
    private static Meta BuildMeta(StoredClinicalResource stored) => new()
    {
        Source = SourceUri(stored),
        LastUpdated = new DateTimeOffset(DateTime.SpecifyKind(stored.LastUpdatedUtc, DateTimeKind.Utc)),

        // The content hash is the version: it changes exactly when the resource's
        // stored content changes, and not when an unrelated exchange re-commits
        // an identical copy. That is the property FHIR asks versionId for.
        VersionId = stored.ContentHash.Length >= 12 ? stored.ContentHash[..12] : stored.ContentHash,
    };

    /// <summary>
    /// <c>urn:cho:clinical:imported:{payer}:{source id}</c> — origin and source
    /// identity in one standard element, so "who sent this, and what did they
    /// call it?" is answerable from the resource itself. Components are escaped,
    /// so a payer id containing a separator cannot forge a different origin.
    /// </summary>
    private static string SourceUri(StoredClinicalResource stored)
    {
        if (stored.Origin == ClinicalResourceOrigin.ChoNative)
            return NativeSource;

        return string.Join(':',
            ImportedSourceScheme,
            Uri.EscapeDataString(stored.SourcePayerId ?? "unknown"),
            Uri.EscapeDataString(stored.SourceResourceId ?? "unknown"));
    }

    /// <summary>Every ResourceReference in the resource, walked over the FHIR model.</summary>
    private static IEnumerable<ResourceReference> AllReferences(Base element)
    {
        if (element is ResourceReference reference) yield return reference;

        foreach (var child in element.Children)
            foreach (var nested in AllReferences(child))
                yield return nested;
    }
}
