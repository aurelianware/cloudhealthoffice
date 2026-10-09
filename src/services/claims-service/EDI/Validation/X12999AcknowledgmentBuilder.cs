using System.Globalization;
using System.Text;

namespace ClaimsService.EDI.Validation;

/// <summary>
/// Builds X12 999 Implementation Acknowledgments (005010X231A1) from a
/// <see cref="SnipValidationResult"/>.
///
/// <para>One outbound interchange per inbound interchange, addressed back to
/// that interchange's sender and echoing its ISA15 (P/T). Inside it, one 999
/// transaction set per acknowledged functional group: AK1 echoes GS01/GS06/GS08;
/// per 837 transaction set, AK2 + IK3 (segment id, position from ST, loop,
/// IK304) + CTX (CLM01) + IK4 (element position, data element reference,
/// IK403, copy of a bad code value) + IK5; AK9 closes the group.</para>
///
/// <para>The codes always match <see cref="SnipValidationResult.AcceptedTransactionSets"/>:
/// IK5 is R for every set the import will not submit (including sets in a
/// rejected group or interchange) and AK9 counts the same sets. Findings at a
/// "Warn" level give IK5 E (accepted with errors). A file that cannot be read
/// as X12 has no functional group to acknowledge, so no 999 is built (a TA1
/// would carry that; it is out of scope).</para>
/// </summary>
public static class X12999AcknowledgmentBuilder
{
    public const string ImplementationReference = "005010X231A1";

    /// <summary>Envelope values for the outbound 999. Unset ids are taken from each inbound ISA, reversed.</summary>
    public sealed record Options
    {
        public string? SenderQualifier { get; init; }
        public string? SenderId { get; init; }
        public string? ReceiverQualifier { get; init; }
        public string? ReceiverId { get; init; }

        /// <summary>ISA13 / GS06 of the first outbound interchange (1–9 digits); each further interchange adds 1.</summary>
        public long ControlNumber { get; init; } = 1;

        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>ISA15 override (P or T). Null echoes the inbound ISA15, defaulting to P.</summary>
        public string? UsageIndicator { get; init; }

        /// <summary>IK3 loops written per transaction set; findings beyond this are not listed (IK5 is unaffected).</summary>
        public int MaxSegmentErrorsPerTransactionSet { get; init; } = 1000;
    }

    /// <summary>The 999 text (one or more interchanges), or null when there is no functional group to acknowledge.</summary>
    public static string? Build(SnipValidationResult result, Options? options = null)
    {
        options ??= new Options();
        var interchanges = result.Interchanges.Where(i => i.FunctionalGroups.Count > 0).ToList();
        if (interchanges.Count == 0) return null;

        var now = options.Timestamp;
        var sb = new StringBuilder();
        var controlNumber = options.ControlNumber;

        foreach (var inbound in interchanges)
        {
            var isaControl = (controlNumber % 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
            var gsControl = (controlNumber % 1_000_000_000).ToString(CultureInfo.InvariantCulture);
            controlNumber++;

            var segments = new List<string>();

            // The acknowledgment goes back the way the 837 came: our id is its receiver.
            var senderQualifier = options.SenderQualifier ?? inbound.ReceiverQualifier ?? "ZZ";
            var senderId = options.SenderId ?? inbound.ReceiverId ?? "RECEIVER";
            var receiverQualifier = options.ReceiverQualifier ?? inbound.SenderQualifier ?? "ZZ";
            var receiverId = options.ReceiverId ?? inbound.SenderId ?? "SENDER";
            var usage = options.UsageIndicator ?? inbound.UsageIndicator ?? "P";

            segments.Add(string.Join('*',
                "ISA", "00", new string(' ', 10), "00", new string(' ', 10),
                Fixed(senderQualifier, 2), Fixed(senderId, 15),
                Fixed(receiverQualifier, 2), Fixed(receiverId, 15),
                now.ToString("yyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
                "^", "00501", isaControl, "0", usage == "T" ? "T" : "P", ":"));

            var firstGroup = inbound.FunctionalGroups[0];
            segments.Add(string.Join('*',
                "GS", "FA",
                Clean(firstGroup.ApplicationReceiverCode ?? senderId.Trim()),
                Clean(firstGroup.ApplicationSenderCode ?? receiverId.Trim()),
                now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
                gsControl, "X", ImplementationReference));

            var setNumber = 0;
            foreach (var group in inbound.FunctionalGroups)
            {
                setNumber++;
                var stControl = setNumber.ToString("D4", CultureInfo.InvariantCulture);
                var set = new List<string>
                {
                    $"ST*999*{stControl}*{ImplementationReference}",
                    Join("AK1", Clean(group.FunctionalIdentifier ?? "HC"), Clean(group.ControlNumber ?? "0"), Clean(group.VersionCode ?? string.Empty)),
                };

                foreach (var ts in group.TransactionSets)
                    AppendTransactionSet(set, ts, options.MaxSegmentErrorsPerTransactionSet);

                var received = group.TransactionSets.Count;
                var accepted = group.TransactionSets.Count(t => t.Accepted);
                var included = group.DeclaredTransactionSetCount ?? received;
                var ak9 = new List<string> { "AK9", group.AcknowledgmentCode, N(included), N(received), N(accepted) };
                ak9.AddRange(group.GroupErrorCodes.Concat(group.GroupWarningCodes).Distinct().Take(5));
                set.Add(string.Join('*', ak9));

                set.Add($"SE*{set.Count + 1}*{stControl}");
                segments.AddRange(set);
            }

            segments.Add($"GE*{setNumber}*{gsControl}");
            segments.Add($"IEA*1*{isaControl}");

            foreach (var segment in segments) sb.Append(segment).Append('~');
        }

        return sb.ToString();
    }

    private static void AppendTransactionSet(List<string> set, SnipTransactionSetOutcome ts, int maxSegmentErrors)
    {
        set.Add(Join("AK2", "837", Clean(ts.ControlNumber), Clean(ts.ImplementationReference ?? string.Empty)));

        var bySegment = ts.Issues
            .Where(i => i.SegmentPosition is not null && i.SegmentId is not null)
            .GroupBy(i => (i.SegmentPosition!.Value, i.SegmentId!))
            .OrderBy(g => g.Key.Value)
            .Take(Math.Max(maxSegmentErrors, 0));

        foreach (var segment in bySegment)
        {
            var first = segment.First();
            set.Add(string.Join('*', "IK3", X12837SnipValidator.SafeSegmentId(segment.Key.Item2), N(segment.Key.Value), Clean(first.Loop ?? string.Empty), first.SegmentErrorCode));

            if (segment.Select(i => i.ClaimId).FirstOrDefault(c => !string.IsNullOrEmpty(c)) is { } claimId)
                set.Add($"CTX*CLM01:{Clean(claimId)}");

            foreach (var issue in segment.Where(i => i.ElementPosition is not null).Take(99))
            {
                var position = issue.ComponentPosition is { } component
                    ? $"{N(issue.ElementPosition!.Value)}:{N(component)}"
                    : N(issue.ElementPosition!.Value);
                var ik4 = new List<string> { "IK4", position, Clean(issue.DataElementReference ?? string.Empty), issue.ElementErrorCode ?? "7" };
                if (!string.IsNullOrEmpty(issue.BadValue)) ik4.Add(Clean(issue.BadValue));
                set.Add(string.Join('*', ik4));
            }
        }

        var ik5 = new List<string> { "IK5", ts.AcknowledgmentCode };
        if (ts.AcknowledgmentCode != "A")
        {
            var codes = new List<string>(ts.TransactionSetErrorCodes);
            if (ts.Issues.Any(i => i.Level == SnipLevel.Syntax && i.SegmentPosition is not null)) codes.Add("5");   // one or more segments in error
            if (ts.Issues.Any(i => i.Level != SnipLevel.Syntax)) codes.Add("I5");  // implementation: one or more segments in error
            // Rejected only because its group or interchange was: no IK5 code
            // names that exactly; 5 is the closest required value.
            if (codes.Count == 0) codes.Add("5");
            ik5.AddRange(codes.Distinct().Take(5));
        }
        set.Add(string.Join('*', ik5));
    }

    /// <summary>Joins elements and drops trailing empty ones (no dangling '*').</summary>
    private static string Join(params string[] elements)
    {
        var last = elements.Length - 1;
        while (last > 0 && elements[last].Length == 0) last--;
        return string.Join('*', elements.Take(last + 1));
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Strips X12 delimiters so echoed values can never break the 999's own syntax.</summary>
    private static string Clean(string value)
    {
        var cleaned = new string(value.Select(c => c is '*' or '~' or ':' or '^' or '\r' or '\n' ? ' ' : c).ToArray()).Trim();
        return cleaned.Length > 99 ? cleaned[..99] : cleaned;
    }

    private static string Fixed(string value, int width)
    {
        var cleaned = Clean(value);
        return cleaned.Length >= width ? cleaned[..width] : cleaned.PadRight(width);
    }
}
