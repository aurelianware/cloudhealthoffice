using System.Globalization;
using System.Text;

namespace ClaimsService.EDI.Validation;

/// <summary>
/// Builds an X12 999 Implementation Acknowledgment (005010X231A1) from a
/// <see cref="SnipValidationResult"/>.
///
/// <para>One 999 transaction set per acknowledged functional group:
/// AK1 names the group; per 837 transaction set, AK2 + IK3 (segment in error:
/// id, position from ST, loop, IK304 code) + optional CTX (CLM01 of the claim)
/// + IK4 (element position, data element reference, IK403 code, copy of a bad
/// code value) + IK5 (A / E / R); AK9 closes the group (A / E / P / R).</para>
///
/// <para>Issues at a "Warn" level are reported in IK3/IK4 under IK5 E
/// (accepted with errors); "Reject" levels give IK5 R. A file that cannot be
/// read as X12 at all has no functional group to acknowledge, so no 999 is
/// built (a TA1 would carry that; it is out of scope here).</para>
/// </summary>
public static class X12999AcknowledgmentBuilder
{
    public const string ImplementationReference = "005010X231A1";

    /// <summary>Envelope values for the outbound 999. Unset ids are taken from the inbound ISA, reversed.</summary>
    public sealed record Options
    {
        public string? SenderQualifier { get; init; }
        public string? SenderId { get; init; }
        public string? ReceiverQualifier { get; init; }
        public string? ReceiverId { get; init; }

        /// <summary>ISA13 / GS06 of the 999 (1–9 digits).</summary>
        public long ControlNumber { get; init; } = 1;

        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>ISA15: P (production) or T (test).</summary>
        public string UsageIndicator { get; init; } = "P";
    }

    /// <summary>The 999 text, or null when the input had no functional group to acknowledge.</summary>
    public static string? Build(SnipValidationResult result, Options? options = null)
    {
        if (result.FunctionalGroups.Count == 0) return null;
        options ??= new Options();

        var control = options.ControlNumber.ToString("D9", CultureInfo.InvariantCulture);
        var now = options.Timestamp;
        var segments = new List<string>();

        // The acknowledgment goes back the way the 837 came: our id is the 837's receiver.
        var senderQualifier = options.SenderQualifier ?? result.InterchangeReceiverQualifier ?? "ZZ";
        var senderId = options.SenderId ?? result.InterchangeReceiverId ?? "RECEIVER";
        var receiverQualifier = options.ReceiverQualifier ?? result.InterchangeSenderQualifier ?? "ZZ";
        var receiverId = options.ReceiverId ?? result.InterchangeSenderId ?? "SENDER";

        segments.Add(string.Join('*',
            "ISA", "00", new string(' ', 10), "00", new string(' ', 10),
            Fixed(senderQualifier, 2), Fixed(senderId, 15),
            Fixed(receiverQualifier, 2), Fixed(receiverId, 15),
            now.ToString("yyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
            "^", "00501", control, "0", options.UsageIndicator == "T" ? "T" : "P", ":"));

        var firstGroup = result.FunctionalGroups[0];
        segments.Add(string.Join('*',
            "GS", "FA",
            Clean(firstGroup.ApplicationReceiverCode ?? senderId.Trim()),
            Clean(firstGroup.ApplicationSenderCode ?? receiverId.Trim()),
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), now.ToString("HHmm", CultureInfo.InvariantCulture),
            options.ControlNumber.ToString(CultureInfo.InvariantCulture), "X", ImplementationReference));

        var setNumber = 0;
        foreach (var group in result.FunctionalGroups)
        {
            setNumber++;
            var stControl = setNumber.ToString("D4", CultureInfo.InvariantCulture);
            var set = new List<string>
            {
                $"ST*999*{stControl}*{ImplementationReference}",
                $"AK1*HC*{Clean(group.ControlNumber ?? "0")}*{Clean(group.VersionCode ?? string.Empty)}",
            };

            foreach (var ts in group.TransactionSets)
                AppendTransactionSet(set, ts);

            var received = group.TransactionSets.Count;
            var accepted = group.TransactionSets.Count(t => t.Accepted);
            var included = group.DeclaredTransactionSetCount ?? received;
            var ak9 = new List<string> { "AK9", group.AcknowledgmentCode, N(included), N(received), N(group.AcknowledgmentCode == "R" ? 0 : accepted) };
            ak9.AddRange(group.GroupErrorCodes.Distinct().Take(5));
            set.Add(string.Join('*', ak9));

            set.Add($"SE*{set.Count + 1}*{stControl}");
            segments.AddRange(set);
        }

        segments.Add($"GE*{setNumber}*{options.ControlNumber.ToString(CultureInfo.InvariantCulture)}");
        segments.Add($"IEA*1*{control}");

        var sb = new StringBuilder();
        foreach (var segment in segments) sb.Append(segment).Append('~');
        return sb.ToString();
    }

    private static void AppendTransactionSet(List<string> set, SnipTransactionSetOutcome ts)
    {
        set.Add($"AK2*837*{Clean(ts.ControlNumber)}*{Clean(ts.ImplementationReference ?? string.Empty)}".TrimEnd('*'));

        var bySegment = ts.Issues
            .Where(i => i.SegmentPosition is not null && i.SegmentId is not null)
            .GroupBy(i => (i.SegmentPosition!.Value, i.SegmentId!))
            .OrderBy(g => g.Key.Value);

        foreach (var segment in bySegment)
        {
            var first = segment.First();
            var ik3 = new List<string> { "IK3", Clean(segment.Key.Item2), N(segment.Key.Value), Clean(first.Loop ?? string.Empty), first.SegmentErrorCode };
            set.Add(string.Join('*', ik3));

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
            if (ts.Issues.Any(i => i.Level == SnipLevel.Syntax)) codes.Add("5");   // one or more segments in error
            if (ts.Issues.Any(i => i.Level != SnipLevel.Syntax)) codes.Add("I5");  // implementation: one or more segments in error
            ik5.AddRange(codes.Distinct().Take(5));
        }
        set.Add(string.Join('*', ik5));
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
