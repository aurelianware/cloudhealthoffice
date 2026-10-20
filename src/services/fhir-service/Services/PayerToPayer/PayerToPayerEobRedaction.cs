using System.Text.Json.Nodes;

namespace FhirService.Services.PayerToPayer;

/// <summary>
/// Removes what CMS-0057-F keeps out of Payer-to-Payer claims data: provider
/// remittances and enrollee cost-sharing (ADR 020). An ExplanationOfBenefit
/// leaves without its money and its adjudication: totals, the payment and payee,
/// benefit balances, process notes, and on every item, detail and sub-detail the
/// adjudication (amounts and CARC/RARC reasons), unit price and net. Added items
/// exist only to carry adjudication and are removed whole. What remains is the
/// claim itself: who, when, where, what services, which diagnoses.
/// </summary>
public static class PayerToPayerEobRedaction
{
    private static readonly string[] HeaderFinancial =
        ["total", "payment", "payee", "adjudication", "benefitBalance", "processNote", "addItem"];

    private static readonly string[] LineFinancial = ["adjudication", "unitPrice", "net"];

    /// <summary>A redacted copy of <paramref name="eob"/>; the original is untouched.</summary>
    public static JsonObject Redact(JsonObject eob)
    {
        var copy = eob.DeepClone().AsObject();
        foreach (var key in HeaderFinancial) copy.Remove(key);
        RedactLines(copy["item"], "detail", "subDetail");
        return copy;
    }

    private static void RedactLines(JsonNode? lines, params string[] childLevels)
    {
        if (lines is not JsonArray array) return;
        foreach (var line in array.OfType<JsonObject>())
        {
            foreach (var key in LineFinancial) line.Remove(key);
            if (childLevels.Length > 0) RedactLines(line[childLevels[0]], childLevels[1..]);
        }
    }
}
