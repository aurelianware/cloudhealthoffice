using System.Text.RegularExpressions;

namespace FhirService.Services.Clinical;

/// <summary>
/// The logical id Cloud Health Office serves a clinical resource under.
///
/// WHY NOT THE SOURCE PAYER'S ID. A prior payer's <c>Observation/123</c> is
/// unique only inside that payer. Serving it verbatim would collide with the
/// same id from a second payer, with a CHO-native resource, and with another
/// tenant's record — three different ways to hand a reader someone else's data.
///
/// WHAT CHO SERVES INSTEAD is the deterministic import identity the
/// Payer-to-Payer ingestion already computes:
/// <c>SHA-256(tenant ∥ member ∥ source payer ∥ resource type ∥ source id)</c>
/// (see <c>PayerToPayerImportPolicy.ImportKey</c>). That gives all four
/// properties a served id needs, without a second identifier to keep in step:
///
///   * DETERMINISTIC — a replayed exchange resolves to the same id, so a
///     re-import updates the resource a reader already fetched rather than
///     creating a second one at a new URL;
///   * COLLISION-FREE — tenant, member, payer and type are all inside the hash,
///     so two payers' <c>Observation/123</c> are two different CHO resources and
///     neither is reachable from the other tenant;
///   * OPAQUE — it is a hash, so the URL leaks no member id, no payer name and
///     no clinical detail, and it is not a database row number or an offset a
///     caller could walk;
///   * STABLE — it is derived, not allocated, so it survives a rebuild of the
///     store and the migration that backfills already-imported history.
///
/// Knowing an id is NOT authority to read it: every read is scoped to the
/// authorized member in the storage query (see <c>IClinicalResourceStore</c>),
/// so a guessed or leaked id resolves to nothing outside its own member.
/// </summary>
public static class ClinicalResourceIdentity
{
    /// <summary>
    /// A served clinical id: 64 lowercase hex characters. Inside FHIR's
    /// <c>[A-Za-z0-9\-\.]{1,64}</c> id rule, so it is a legal resource id and can
    /// be round-tripped through a URL unescaped.
    /// </summary>
    private static readonly Regex Shape = new("^[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The id CHO serves an imported resource under: the import key itself.
    /// Identity is derived, never allocated, so nothing has to be stored to keep
    /// two ids in agreement.
    /// </summary>
    public static string ForImported(string importKey) => importKey;

    /// <summary>
    /// Whether a caller-supplied id could possibly be one CHO issued. A read for
    /// anything else is answered as not found WITHOUT touching the store — it
    /// cannot name a CHO resource, so there is nothing to look up.
    /// </summary>
    public static bool IsWellFormed(string? id)
        => !string.IsNullOrEmpty(id) && Shape.IsMatch(id);
}
