using System.Text.RegularExpressions;

namespace CloudHealthOffice.PlanEdits.Contracts;

/// <summary>
/// The rules a response must meet. CHO accepts a response only when it meets
/// all of them; a response that breaks one is treated as the plan service
/// being unavailable (the claim pends, or passes, as the plan configured), so
/// a bug in a plan service is never read as "no edits". A plan service can
/// run the same checks on its own output in its tests.
/// </summary>
public static partial class PlanEditResponseValidator
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleIdPattern();

    /// <summary>CARCs are one to three upper-case letters or digits: 96, 204, B7, P14.</summary>
    [GeneratedRegex("^[A-Z0-9]{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CarcPattern();

    /// <summary>RARCs are one or two letters and up to four digits: N130, M15, MA130.</summary>
    [GeneratedRegex("^[A-Z]{1,2}[0-9]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex RarcPattern();

    /// <summary>
    /// The problems with <paramref name="response"/> for a claim with
    /// <paramref name="claimLineNumbers"/>; empty when it is valid. Each
    /// problem names the field (<c>edits[2].lineNumbers</c>) and never repeats
    /// the plan's message text, so the problems can be logged.
    /// </summary>
    public static IReadOnlyList<string> Validate(PlanEditResponse? response, IReadOnlyCollection<int> claimLineNumbers)
    {
        var problems = new List<string>();
        if (response is null)
        {
            problems.Add("response: body is empty or not a JSON object");
            return problems;
        }

        if (!string.Equals(response.ContractVersion, PlanEditContract.Version, StringComparison.Ordinal))
            problems.Add($"contractVersion: must be \"{PlanEditContract.Version}\"");

        if (response.Edits is null)
        {
            problems.Add("edits: required (an empty array when no edit fired)");
            return problems;
        }

        if (response.Edits.Count > PlanEditContract.MaxEdits)
            problems.Add($"edits: at most {PlanEditContract.MaxEdits}");

        var lines = claimLineNumbers as ISet<int> ?? claimLineNumbers.ToHashSet();
        for (var i = 0; i < response.Edits.Count; i++)
        {
            var edit = response.Edits[i];
            var at = $"edits[{i}]";
            if (edit is null)
            {
                problems.Add($"{at}: must be an object");
                continue;
            }

            if (string.IsNullOrEmpty(edit.RuleId))
                problems.Add($"{at}.ruleId: required");
            else if (edit.RuleId.Length > PlanEditContract.MaxRuleIdLength || !RuleIdPattern().IsMatch(edit.RuleId))
                problems.Add($"{at}.ruleId: letters, digits, '.', '_' and '-', starting with a letter or digit, at most {PlanEditContract.MaxRuleIdLength} characters");

            if (string.IsNullOrWhiteSpace(edit.Message))
                problems.Add($"{at}.message: required");
            else if (edit.Message.Length > PlanEditContract.MaxMessageLength)
                problems.Add($"{at}.message: at most {PlanEditContract.MaxMessageLength} characters");

            var editLines = edit.LineNumbers ?? [];
            foreach (var line in editLines.Distinct())
            {
                if (!lines.Contains(line))
                    problems.Add($"{at}.lineNumbers: {line} is not a line on the claim");
            }

            if (edit.SuggestedCarc is not null)
            {
                if (!CarcPattern().IsMatch(edit.SuggestedCarc))
                    problems.Add($"{at}.suggestedCarc: one to three upper-case letters or digits");
                if (editLines.Count == 0)
                    problems.Add($"{at}.suggestedCarc: a denial edit must name its lineNumbers");
            }

            if (edit.SuggestedRarc is not null)
            {
                if (!RarcPattern().IsMatch(edit.SuggestedRarc))
                    problems.Add($"{at}.suggestedRarc: one or two upper-case letters and up to four digits");
                if (edit.SuggestedCarc is null)
                    problems.Add($"{at}.suggestedRarc: needs a suggestedCarc");
            }
        }

        return problems;
    }
}
