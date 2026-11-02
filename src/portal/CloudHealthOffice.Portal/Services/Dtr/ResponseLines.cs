using System.Globalization;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Services.Dtr;

/// <summary>One line of a completed form, for reading it back.</summary>
public sealed record ResponseLine(int Depth, string Label, string? Answer);

/// <summary>
/// A stored QuestionnaireResponse as question/answer lines, labelled from the
/// response's own item text or, failing that, from the Questionnaire (a
/// response from an EHR's DTR client may carry linkIds only).
/// </summary>
public static class ResponseLines
{
    public static IReadOnlyList<ResponseLine> From(QuestionnaireResponse response, Questionnaire? questionnaire)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        Index(questionnaire?.Item ?? [], labels);
        var lines = new List<ResponseLine>();
        Walk(response.Item ?? [], 0, labels, lines);
        return lines;
    }

    private static void Index(IEnumerable<Questionnaire.ItemComponent> items, Dictionary<string, string> labels)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.LinkId) && !string.IsNullOrWhiteSpace(item.Text))
                labels.TryAdd(item.LinkId, item.Text);
            Index(item.Item ?? [], labels);
        }
    }

    private static void Walk(
        IEnumerable<QuestionnaireResponse.ItemComponent> items, int depth, Dictionary<string, string> labels, List<ResponseLine> lines)
    {
        foreach (var item in items)
        {
            var label = !string.IsNullOrWhiteSpace(item.Text) ? item.Text
                : labels.TryGetValue(item.LinkId ?? string.Empty, out var known) ? known
                : item.LinkId ?? "(unlabelled)";
            var answers = (item.Answer ?? []).Select(a => Display(a.Value)).Where(a => a is not null).ToList();
            lines.Add(new ResponseLine(depth, label, answers.Count == 0 ? null : string.Join("; ", answers)));

            Walk(item.Item ?? [], depth + 1, labels, lines);
            foreach (var answer in item.Answer ?? [])
                Walk(answer.Item ?? [], depth + 1, labels, lines);
        }
    }

    public static string? Display(DataType? value) => value switch
    {
        null => null,
        FhirBoolean b => b.Value switch { true => "Yes", false => "No", null => null },
        Coding c => c.Display ?? c.Code,
        FhirDecimal d => d.Value?.ToString(CultureInfo.InvariantCulture),
        Integer i => i.Value?.ToString(CultureInfo.InvariantCulture),
        Date d => d.Value,
        FhirDateTime dt => dt.Value,
        FhirString s => s.Value,
        FhirUri u => u.Value,
        Quantity q => $"{q.Value} {q.Unit ?? q.Code}".Trim(),
        Attachment a => a.Title ?? "(attachment)",
        ResourceReference r => r.Display ?? r.Reference,
        _ => value.ToString(),
    };
}
