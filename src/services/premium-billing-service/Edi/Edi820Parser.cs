using System.Globalization;
using PremiumBillingService.Models;

namespace PremiumBillingService.Edi;

/// <summary>
/// Parses X12 005010X218 820 (Payroll Deducted and Other Group Premium
/// Payment for Insurance Products) into one <see cref="RemittanceAdvice"/>
/// per ST/SE transaction set.
///
/// Read per transaction set:
/// <list type="bullet">
///   <item>BPR: handling code (01), amount (02), credit/debit (03), method (04), payment date (16).</item>
///   <item>TRN: trace number (02) and originating company id (03).</item>
///   <item>N1*PR (loop 1000B): the premium payer; N104 is the payer id when TRN03 is absent.</item>
///   <item>ENT (loops 2000A organization summary / 2000B individual): ENT02 2J is an individual,
///     anything else is the organization level.</item>
///   <item>NM1*IL (2100B): member name and id (NM109).</item>
///   <item>RMR (2300A/B): reference qualifier (01), reference (02), amount paid (04), billed (05).</item>
///   <item>DTM*582 after an RMR: coverage period (RD8 or D8).</item>
///   <item>ADX (2320A/B): adjustment amount and reason, attached to the preceding RMR.</item>
/// </list>
/// Unknown segments are skipped. Missing BPR or TRN, or an unreadable amount
/// or date, is a <see cref="FormatException"/>: nothing from that file is posted.
/// </summary>
public static class Edi820Parser
{
    public static IReadOnlyList<RemittanceAdvice> Parse(string content, string? sourceFileName = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new FormatException("The 820 is empty");

        var text = content.TrimStart('﻿', ' ', '\r', '\n', '\t');
        char elementSeparator = '*', segmentTerminator = '~';
        if (text.StartsWith("ISA", StringComparison.Ordinal))
        {
            if (text.Length < 106)
                throw new FormatException("The ISA segment is shorter than 106 characters");
            elementSeparator = text[3];
            segmentTerminator = text[105];
        }

        var segments = text.Split(segmentTerminator)
            .Select(s => s.Trim('\r', '\n', ' ', '\t'))
            .Where(s => s.Length > 0)
            .Select(s => s.Split(elementSeparator))
            .ToList();

        if (!segments.Any(s => s[0] == "ST"))
            throw new FormatException("The file has no ST transaction set");

        var advices = new List<RemittanceAdvice>();
        TransactionBuilder? current = null;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            switch (seg[0])
            {
                case "ST":
                    if (current != null)
                        throw new FormatException($"Segment {i + 1}: ST before the previous transaction set ended (SE)");
                    if (El(seg, 1) != "820")
                        throw new FormatException($"Segment {i + 1}: transaction set {El(seg, 1)} is not an 820");
                    current = new TransactionBuilder(El(seg, 2), sourceFileName);
                    current.SegmentCount = 1;
                    break;
                case "SE":
                    if (current == null)
                        throw new FormatException($"Segment {i + 1}: SE without ST");
                    current.SegmentCount++;
                    if (int.TryParse(El(seg, 1), out var declared) && declared != current.SegmentCount)
                        current.Advice.Warnings.Add($"SE01 declares {declared} segments, the transaction set has {current.SegmentCount}");
                    advices.Add(current.Build());
                    current = null;
                    break;
                default:
                    if (current == null)
                        continue; // ISA, GS, GE, IEA and anything outside a transaction set
                    current.SegmentCount++;
                    current.Read(seg, i + 1);
                    break;
            }
        }

        if (current != null)
            throw new FormatException($"Transaction set {current.ControlNumber} has no SE");

        return advices;
    }

    private static string El(string[] segment, int index) =>
        index < segment.Length ? segment[index].Trim() : string.Empty;

    private sealed class TransactionBuilder
    {
        public string ControlNumber { get; }
        public int SegmentCount { get; set; }
        public RemittanceAdvice Advice { get; }

        private bool _sawBpr, _sawTrn;
        private string? _originatorId, _payerN1Id;
        private RemittanceLevel _level = RemittanceLevel.Organization;
        private string? _entityNumber, _entityQualifier, _entityId, _memberId, _memberName;
        private RemittanceItem? _lastItem;
        private string? _lastSegmentId;

        public TransactionBuilder(string controlNumber, string? sourceFileName)
        {
            ControlNumber = controlNumber;
            Advice = new RemittanceAdvice { Source = RemittanceSource.X12820, SourceFileName = sourceFileName };
        }

        public void Read(string[] seg, int position)
        {
            switch (seg[0])
            {
                case "BPR":
                    _sawBpr = true;
                    Advice.TransactionHandlingCode = El(seg, 1);
                    Advice.PaymentAmount = Amount(El(seg, 2), "BPR02", position);
                    Advice.CreditDebitFlag = NullIfEmpty(El(seg, 3));
                    Advice.PaymentMethod = NullIfEmpty(El(seg, 4));
                    var date = El(seg, 16);
                    if (date.Length > 0)
                        Advice.PaymentDate = Date(date, "BPR16", position);
                    break;
                case "TRN":
                    _sawTrn = true;
                    Advice.TraceNumber = El(seg, 2);
                    _originatorId = NullIfEmpty(El(seg, 3));
                    break;
                case "REF":
                    // Header REF*38: the master policy (group) number the payer is paying for.
                    if (El(seg, 1) == "38" && _entityNumber == null && Advice.Items.Count == 0)
                        Advice.GroupReference = NullIfEmpty(El(seg, 2));
                    break;
                case "N1":
                    if (El(seg, 1) == "PR")
                    {
                        Advice.PayerName = NullIfEmpty(El(seg, 2));
                        _payerN1Id = NullIfEmpty(El(seg, 4));
                    }
                    break;
                case "ENT":
                    _level = El(seg, 2) == "2J" ? RemittanceLevel.Individual : RemittanceLevel.Organization;
                    _entityNumber = NullIfEmpty(El(seg, 1));
                    _entityQualifier = NullIfEmpty(El(seg, 3));
                    _entityId = NullIfEmpty(El(seg, 4));
                    _memberId = null;
                    _memberName = null;
                    _lastItem = null;
                    break;
                case "NM1":
                    if (El(seg, 1) == "IL")
                    {
                        _memberId = NullIfEmpty(El(seg, 9));
                        var name = string.Join(" ", new[] { El(seg, 4), El(seg, 3) }.Where(p => p.Length > 0));
                        _memberName = NullIfEmpty(name);
                    }
                    break;
                case "RMR":
                    var billed = El(seg, 5);
                    _lastItem = new RemittanceItem
                    {
                        LineNumber = Advice.Items.Count + 1,
                        Level = _level,
                        EntityNumber = _entityNumber,
                        EntityIdQualifier = _entityQualifier,
                        EntityId = _entityId,
                        MemberId = _memberId ?? (_level == RemittanceLevel.Individual ? _entityId : null),
                        MemberName = _memberName,
                        ReferenceQualifier = NullIfEmpty(El(seg, 1)),
                        Reference = NullIfEmpty(El(seg, 2)),
                        Amount = Amount(El(seg, 4), "RMR04", position),
                        BilledAmount = billed.Length > 0 ? Amount(billed, "RMR05", position) : null
                    };
                    Advice.Items.Add(_lastItem);
                    break;
                case "DTM":
                    if (_lastItem != null && El(seg, 1) == "582" && _lastSegmentId is "RMR" or "REF" or "IT1" or "DTM")
                        ReadCoveragePeriod(seg, _lastItem, position);
                    break;
                case "ADX":
                    if (_lastItem != null)
                        _lastItem.Adjustments.Add(new RemittanceAdjustmentDetail
                        {
                            Amount = Amount(El(seg, 1), "ADX01", position),
                            ReasonCode = NullIfEmpty(El(seg, 2))
                        });
                    else
                        Advice.Warnings.Add($"Segment {position}: ADX without a preceding RMR was ignored");
                    break;
            }
            _lastSegmentId = seg[0];
        }

        public RemittanceAdvice Build()
        {
            if (!_sawBpr)
                throw new FormatException($"Transaction set {ControlNumber} has no BPR segment");
            if (!_sawTrn || string.IsNullOrWhiteSpace(Advice.TraceNumber))
                throw new FormatException($"Transaction set {ControlNumber} has no TRN trace number");
            Advice.PayerId = _originatorId ?? _payerN1Id ?? string.Empty;
            // For a check payment TRN02 is the check number (ties an 820 to the same check in a lockbox file).
            if (string.Equals(Advice.PaymentMethod, "CHK", StringComparison.OrdinalIgnoreCase))
                Advice.CheckNumber = Advice.TraceNumber;
            if (string.IsNullOrWhiteSpace(Advice.PayerId))
                throw new FormatException($"Transaction set {ControlNumber} names no payer (TRN03 or N1*PR N104)");
            return Advice;
        }

        private static void ReadCoveragePeriod(string[] seg, RemittanceItem item, int position)
        {
            var format = El(seg, 5);
            var value = El(seg, 6);
            if (format == "RD8" && value.Contains('-'))
            {
                var parts = value.Split('-');
                item.CoveragePeriodStart = Date(parts[0], "DTM06", position);
                item.CoveragePeriodEnd = Date(parts[1], "DTM06", position);
            }
            else if (format == "D8" && value.Length == 8)
            {
                item.CoveragePeriodStart = Date(value, "DTM06", position);
            }
            else if (El(seg, 2).Length == 8)
            {
                item.CoveragePeriodStart = Date(El(seg, 2), "DTM02", position);
            }
        }

        private static decimal Amount(string value, string element, int position)
        {
            if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var amount))
                throw new FormatException($"Segment {position}: {element} '{value}' is not an amount");
            return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        }

        private static DateTime Date(string value, string element, int position)
        {
            if (!DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
                throw new FormatException($"Segment {position}: {element} '{value}' is not a CCYYMMDD date");
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        }

        private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    }
}
