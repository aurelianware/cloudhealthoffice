namespace EncounterSubmissionService.StateRoutes;

/// <summary>An X12 interchange split into segments with the delimiters its own ISA declares.</summary>
public sealed class X12Segments
{
    public IReadOnlyList<string[]> Segments { get; }
    public char ComponentSeparator { get; }

    private X12Segments(IReadOnlyList<string[]> segments, char component)
    {
        Segments = segments;
        ComponentSeparator = component;
    }

    public static X12Segments Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("The response file is empty.");
        var text = content.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!text.StartsWith("ISA", StringComparison.Ordinal) || text.Length < 106)
            throw new FormatException("The response file does not start with an ISA segment.");

        var element = text[3];
        var component = text[104];
        var terminator = text[105];

        var segments = text.Split(terminator)
            .Select(s => s.Trim('\r', '\n', ' ', '\t'))
            .Where(s => s.Length > 0)
            .Select(s => s.Split(element))
            .ToList();
        return new X12Segments(segments, component);
    }

    public string TransactionType =>
        Segments.FirstOrDefault(s => s[0] == "ST") is { Length: > 1 } st ? st[1] : string.Empty;
}

public static class SegmentExtensions
{
    public static string At(this string[] segment, int index) =>
        index < segment.Length ? segment[index] : string.Empty;
}

// ── 999 ──────────────────────────────────────────────────────────────

/// <summary>Per-transaction-set result from a 999 (AK2 / IK5), with its IK3 / IK4 errors.</summary>
public sealed record AckTransactionResult(string TransactionSetControlNumber, string Code, IReadOnlyList<string> Errors);

public sealed record Ack999(
    string GroupControlNumber,
    string GroupCode,
    IReadOnlyList<AckTransactionResult> Transactions);

public static class Ack999Parser
{
    public static IReadOnlyList<Ack999> Parse(X12Segments x12)
    {
        var groups = new List<Ack999>();
        string? groupControl = null;
        var transactions = new List<AckTransactionResult>();
        string? currentSt = null;
        var currentErrors = new List<string>();

        void CloseTransaction(string code)
        {
            if (currentSt is null) return;
            transactions.Add(new AckTransactionResult(currentSt, code, currentErrors.ToList()));
            currentSt = null;
            currentErrors.Clear();
        }

        foreach (var s in x12.Segments)
        {
            switch (s[0])
            {
                case "AK1":
                    groupControl = s.At(2);
                    transactions = new List<AckTransactionResult>();
                    break;
                case "AK2":
                    CloseTransaction("A");
                    currentSt = s.At(2);
                    break;
                case "IK3":
                    currentErrors.Add($"segment {s.At(1)} at position {s.At(2)}{(s.At(3).Length > 0 ? $" (loop {s.At(3)})" : "")}: error {s.At(4)}");
                    break;
                case "IK4":
                    var position = s.At(1).Split(x12.ComponentSeparator)[0];
                    currentErrors.Add($"element {position}: error {s.At(3)}{(s.At(4).Length > 0 ? $" (value '{s.At(4)}')" : "")}");
                    break;
                case "IK5":
                    var code = s.At(1);
                    for (var i = 2; i < s.Length; i++)
                        if (s[i].Length > 0) currentErrors.Add($"transaction error {s[i]}");
                    CloseTransaction(code);
                    break;
                case "AK9":
                    CloseTransaction("A");
                    if (groupControl is not null)
                        groups.Add(new Ack999(groupControl, s.At(1), transactions.ToList()));
                    groupControl = null;
                    break;
            }
        }
        return groups;
    }
}

// ── 277CA / 277 ──────────────────────────────────────────────────────

/// <summary>
/// One claim-level status from a 277CA: the CLM01 we sent (TRN02), the status
/// categories (STC01-1 and any STC10 / STC11), and the state's claim
/// identifier (REF*1K) when it assigned one.
/// </summary>
public sealed record ClaimAcknowledgment(
    string PatientControlNumber,
    IReadOnlyList<string> StatusCodes,
    string? StateClaimReference,
    decimal? ChargeAmount)
{
    /// <summary>A3, A4, A6, A7, A8 and the R category are rejections; A1 and A2 accept for processing.</summary>
    public bool IsRejected => StatusCodes.Any(c => CategoryOf(c) is "A3" or "A4" or "A6" or "A7" or "A8" || CategoryOf(c).StartsWith('R'));

    public bool IsAccepted => !IsRejected && StatusCodes.Any(c => CategoryOf(c) is "A1" or "A2");

    private static string CategoryOf(string composite) => composite.Split(':')[0];
}

public static class Ack277Parser
{
    public static IReadOnlyList<ClaimAcknowledgment> Parse(X12Segments x12)
    {
        var results = new List<ClaimAcknowledgment>();
        var inPatientLevel = false;
        var inServiceLine = false;
        string? pcn = null;
        var statuses = new List<string>();
        string? stateRef = null;
        decimal? charge = null;

        void Close()
        {
            if (pcn is not null)
                results.Add(new ClaimAcknowledgment(pcn, statuses.ToList(), stateRef, charge));
            pcn = null;
            statuses.Clear();
            stateRef = null;
            charge = null;
        }

        string Normalize(string composite) =>
            string.Join(':', composite.Split(x12.ComponentSeparator));

        foreach (var s in x12.Segments)
        {
            switch (s[0])
            {
                case "HL":
                    Close();
                    inPatientLevel = s.At(3) == "PT";
                    inServiceLine = false;
                    break;
                case "TRN" when inPatientLevel:
                    Close();
                    inServiceLine = false;
                    pcn = s.At(2);
                    break;
                case "SVC" when inPatientLevel:
                    // 2220D service line status; the claim's own status is what we reconcile.
                    inServiceLine = true;
                    break;
                case "STC" when inPatientLevel && pcn is not null && !inServiceLine:
                    statuses.Add(Normalize(s.At(1)));
                    if (s.At(4).Length > 0 && decimal.TryParse(s.At(4), System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out var amount))
                        charge ??= amount;
                    if (s.At(10).Length > 0) statuses.Add(Normalize(s.At(10)));
                    if (s.At(11).Length > 0) statuses.Add(Normalize(s.At(11)));
                    break;
                case "REF" when inPatientLevel && pcn is not null && !inServiceLine && s.At(1) == "1K":
                    stateRef = s.At(2);
                    break;
                case "SE":
                    Close();
                    inPatientLevel = false;
                    break;
            }
        }
        Close();
        return results;
    }
}
