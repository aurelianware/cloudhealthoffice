using System.Globalization;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;

/// <summary>
/// Maps Stedi 835 ERA Report JSON onto the canonical remittance model.
/// Does not post payment or interpret CHO adjudication.
/// </summary>
internal static class StediRemittanceMapper
{
    public static GatewayRemittance ToCanonical(
        Stedi835ReportDto? report,
        DateTimeOffset receivedAt,
        string? eventId)
    {
        var remittance = new GatewayRemittance
        {
            Gateway = StediHealthcareGateway.GatewayName,
            ReceivedAt = receivedAt,
            EventId = eventId,
            ExternalTransactionId = report?.Meta?.TransactionId,
            RemittanceId = report?.Meta?.TransactionId ?? eventId ?? string.Empty,
            RawSourceReference = report?.Meta?.TransactionId
        };

        var tx = report?.Transactions?.FirstOrDefault();
        if (tx is null)
        {
            return remittance;
        }

        remittance.PayerName = FirstNonBlank(tx.Payer?.OrganizationName, tx.Payer?.Name);
        remittance.PayerIdentifier = tx.Payer?.PayerId;
        remittance.PayeeNpi = tx.Payee?.Npi;
        remittance.PaymentIdentifier = FirstNonBlank(
            tx.PaymentAndRemitReassociationDetails?.CheckOrEFTTraceNumber,
            tx.PaymentAndRemitReassociationDetails?.TraceNumber);
        remittance.PaymentMethodCode = tx.FinancialInformation?.PaymentMethodCode;
        remittance.PaymentDate = ParseDate(tx.FinancialInformation?.CheckIssueOrEFTEffectiveDate);
        remittance.PaymentAmount = ParseAmount(tx.FinancialInformation?.TotalActualProviderPaymentAmount) ?? 0;
        remittance.CreditDebitFlag = tx.FinancialInformation?.CreditOrDebitFlagCode;

        foreach (var detail in tx.DetailInfo ?? Enumerable.Empty<Stedi835DetailDto>())
        {
            foreach (var payment in detail.PaymentInfo ?? Enumerable.Empty<Stedi835PaymentInfoDto>())
            {
                remittance.Claims.Add(MapClaim(payment));
            }
        }

        return remittance;
    }

    private static RemittedClaim MapClaim(Stedi835PaymentInfoDto payment)
    {
        var info = payment.ClaimPaymentInfo;
        var adjustments = Flatten(payment.ClaimAdjustments).ToList();
        var charged = ParseAmount(info?.TotalClaimChargeAmount) ?? 0;
        var paid = ParseAmount(info?.ClaimPaymentAmount) ?? 0;
        var patient = ParseAmount(info?.PatientResponsibilityAmount)
                      ?? adjustments.Where(a => a.Kind is RemittanceAdjustmentKind.PatientResponsibility
                          or RemittanceAdjustmentKind.Deductible
                          or RemittanceAdjustmentKind.Coinsurance
                          or RemittanceAdjustmentKind.Copay)
                          .Sum(a => a.Amount);
        var contractual = adjustments
            .Where(a => a.Kind is RemittanceAdjustmentKind.Contractual or RemittanceAdjustmentKind.NonCovered)
            .Sum(a => a.Amount);

        return new RemittedClaim
        {
            PatientControlNumber = NullIfBlank(info?.PatientControlNumber),
            PayerClaimControlNumber = NullIfBlank(info?.PayerClaimControlNumber),
            ClaimStatusCode = NullIfBlank(info?.ClaimStatusCode),
            ChargedAmount = charged,
            PaidAmount = paid,
            PatientResponsibilityAmount = patient,
            AllowedAmount = charged > 0 ? charged - contractual : null,
            Adjustments = adjustments,
            ServiceLines = (payment.ServiceLines ?? new List<Stedi835ServiceLineDto>())
                .Select(MapLine)
                .ToList()
        };
    }

    private static RemittedServiceLine MapLine(Stedi835ServiceLineDto line)
    {
        var adjustments = Flatten(line.ServiceAdjustments).ToList();
        var charged = ParseAmount(line.LineItemChargeAmount) ?? 0;
        var paid = ParseAmount(line.LineItemProviderPaymentAmount) ?? 0;
        var contractual = adjustments
            .Where(a => a.Kind is RemittanceAdjustmentKind.Contractual or RemittanceAdjustmentKind.NonCovered)
            .Sum(a => a.Amount);
        return new RemittedServiceLine
        {
            LineIdentifier = NullIfBlank(line.LineItemControlNumber),
            LineNumber = ParseLine(line.LineItemControlNumber),
            ProcedureCode = FirstNonBlank(line.AdjudicatedProcedureCode, line.ProcedureCode),
            ProcedureQualifier = NullIfBlank(line.ServiceIdQualifier),
            ToothNumber = NullIfBlank(line.ToothCode),
            ChargedAmount = charged,
            PaidAmount = paid,
            AllowedAmount = charged > 0 ? charged - contractual : null,
            Adjustments = adjustments
        };
    }

    internal static IEnumerable<RemittanceAdjustment> Flatten(IEnumerable<Stedi835AdjustmentDto>? groups)
    {
        if (groups is null)
        {
            yield break;
        }

        foreach (var group in groups)
        {
            var groupCode = FirstNonBlank(group.ClaimAdjustmentGroupCode, group.AdjustmentGroupCode);
            foreach (var pair in Pairs(group))
            {
                yield return new RemittanceAdjustment
                {
                    GroupCode = groupCode,
                    ReasonCode = pair.Reason,
                    Amount = pair.Amount,
                    Kind = Classify(groupCode, pair.Reason)
                };
            }
        }
    }

    internal static RemittanceAdjustmentKind Classify(string? groupCode, string? reasonCode)
    {
        var group = (groupCode ?? string.Empty).Trim().ToUpperInvariant();
        var reason = (reasonCode ?? string.Empty).Trim();
        if (group == "PR")
        {
            return reason switch
            {
                "1" => RemittanceAdjustmentKind.Deductible,
                "2" => RemittanceAdjustmentKind.Coinsurance,
                "3" => RemittanceAdjustmentKind.Copay,
                _ => RemittanceAdjustmentKind.PatientResponsibility
            };
        }

        if (group == "CO")
        {
            return reason is "96" or "27" or "29"
                ? RemittanceAdjustmentKind.NonCovered
                : RemittanceAdjustmentKind.Contractual;
        }

        return RemittanceAdjustmentKind.Other;
    }

    private static IEnumerable<(string Reason, decimal Amount)> Pairs(Stedi835AdjustmentDto group)
    {
        if (HasPair(group.AdjustmentReasonCode1, group.AdjustmentAmount1, out var p1)) yield return p1;
        if (HasPair(group.AdjustmentReasonCode2, group.AdjustmentAmount2, out var p2)) yield return p2;
        if (HasPair(group.AdjustmentReasonCode3, group.AdjustmentAmount3, out var p3)) yield return p3;
        if (HasPair(group.AdjustmentReasonCode4, group.AdjustmentAmount4, out var p4)) yield return p4;
        if (HasPair(group.AdjustmentReasonCode5, group.AdjustmentAmount5, out var p5)) yield return p5;
        if (HasPair(group.AdjustmentReasonCode6, group.AdjustmentAmount6, out var p6)) yield return p6;
    }

    private static bool HasPair(string? reason, string? amount, out (string Reason, decimal Amount) pair)
    {
        pair = default;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        pair = (reason.Trim(), ParseAmount(amount) ?? 0);
        return true;
    }

    private static decimal? ParseAmount(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8)
        {
            return null;
        }

        return DateOnly.TryParseExact(
            value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static int? ParseLine(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
