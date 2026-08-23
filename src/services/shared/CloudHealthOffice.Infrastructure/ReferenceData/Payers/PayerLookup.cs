namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>Normalization helpers shared by store indexes and resolution.</summary>
internal static class PayerLookup
{
    public static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

    public static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    public static string IdentifierKey(string? system, string? type, string? value) =>
        $"{Normalize(system)}|{Normalize(type)}|{Normalize(value)}";

    public static string IdentifierValueKey(string? value) => Normalize(value);

    public static IEnumerable<string> Tokens(PayerReference payer)
    {
        if (!string.IsNullOrWhiteSpace(payer.Id))
        {
            yield return Normalize(payer.Id);
        }

        if (!string.IsNullOrWhiteSpace(payer.Name))
        {
            yield return Normalize(payer.Name);
        }

        foreach (var alias in payer.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                yield return Normalize(alias);
            }
        }

        foreach (var id in payer.ExternalIdentifiers)
        {
            if (!string.IsNullOrWhiteSpace(id.Value))
            {
                yield return Normalize(id.Value);
            }
        }
    }
}
