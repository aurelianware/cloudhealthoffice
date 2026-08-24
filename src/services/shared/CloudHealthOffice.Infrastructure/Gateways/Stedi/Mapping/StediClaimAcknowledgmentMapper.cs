using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Models;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;

/// <summary>
/// Maps Stedi 277CA Report JSON onto the canonical acknowledgment model.
/// Does not interpret adjudication or payment.
/// </summary>
internal static class StediClaimAcknowledgmentMapper
{
    public static GatewayClaimAcknowledgment ToCanonical(
        Stedi277ReportDto? report,
        DateTimeOffset receivedAt,
        string? eventId)
    {
        var ack = new GatewayClaimAcknowledgment
        {
            Gateway = StediHealthcareGateway.GatewayName,
            ReceivedAt = receivedAt,
            EventId = eventId,
            ExternalTransactionId = report?.Meta?.TransactionId,
            AcknowledgmentId = report?.Meta?.TransactionId ?? eventId ?? string.Empty,
            RawSourceReference = report?.Meta?.TransactionId
        };

        if (report?.Transactions is null || report.Transactions.Count == 0)
        {
            ack.Status = ClaimAcknowledgmentStatus.Malformed;
            return ack;
        }

        var claimResults = new List<GatewayClaimAcknowledgmentClaimResult>();
        var lineResults = new List<GatewayClaimAcknowledgmentLineResult>();
        var allIssues = new List<GatewayClaimAcknowledgmentIssue>();
        var providerStatuses = new List<Stedi277StatusDto>();

        foreach (var tx in report.Transactions)
        {
            foreach (var payer in tx.Payers ?? Enumerable.Empty<Stedi277PayerDto>())
            {
                foreach (var cst in payer.ClaimStatusTransactions ??
                                   Enumerable.Empty<Stedi277ClaimStatusTransactionDto>())
                {
                    ack.OriginalSubmissionId ??= NullIfBlank(cst.ClaimTransactionBatchNumber);
                    CollectProviderStatuses(cst, providerStatuses);

                    foreach (var detail in cst.ClaimStatusDetails ??
                                           Enumerable.Empty<Stedi277ClaimStatusDetailDto>())
                    {
                        foreach (var providerStatus in detail.ServiceProviderClaimStatuses ??
                                                       Enumerable.Empty<Stedi277ProviderClaimStatusDto>())
                        {
                            providerStatuses.AddRange(providerStatus.ProviderStatuses ?? new());
                        }

                        foreach (var patient in detail.PatientClaimStatusDetails ??
                                                Enumerable.Empty<Stedi277PatientClaimStatusDto>())
                        {
                            foreach (var claim in patient.Claims ?? Enumerable.Empty<Stedi277ClaimDto>())
                            {
                                var mapped = MapClaim(claim, cst.ClaimTransactionBatchNumber);
                                claimResults.Add(mapped);
                                allIssues.AddRange(mapped.Errors);
                                allIssues.AddRange(mapped.Warnings);
                                ack.PatientControlNumber ??= mapped.PatientControlNumber;
                                ack.ClaimControlNumber ??= mapped.ClaimControlNumber;
                                ack.OriginalSubmissionId ??= mapped.OriginalSubmissionId;

                                foreach (var line in claim.ServiceLines ??
                                                     Enumerable.Empty<Stedi277ServiceLineDto>())
                                {
                                    lineResults.Add(MapLine(line));
                                }
                            }
                        }
                    }
                }
            }
        }

        ack.ClaimLevelResults = claimResults;
        ack.ServiceLineResults = lineResults;
        ack.Errors = allIssues.Where(i => IsRejection(i)).ToList();
        ack.Warnings = allIssues.Where(i => !IsRejection(i)).ToList();
        ack.Status = Rollup(
            claimResults.Select(c => c.Status).ToList(),
            providerStatuses,
            lineResults,
            hadStructure: claimResults.Count > 0 || providerStatuses.Count > 0);
        return ack;
    }

    private static GatewayClaimAcknowledgmentClaimResult MapClaim(
        Stedi277ClaimDto claim, string? batchNumber)
    {
        var status = claim.ClaimStatus;
        var issues = (status?.InformationClaimStatuses ?? new())
            .SelectMany(ics => (ics.InformationStatuses ?? new()).Select(s => MapIssue(s, ics.StatusInformationActionCode)))
            .ToList();

        var actionCodes = (status?.InformationClaimStatuses ?? new())
            .Select(ics => ics.StatusInformationActionCode)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        var result = new GatewayClaimAcknowledgmentClaimResult
        {
            PatientControlNumber = FirstNonBlank(
                status?.ReferencedTransactionTraceNumber, status?.PatientAccountNumber),
            ClaimControlNumber = NullIfBlank(status?.TradingPartnerClaimNumber),
            OriginalSubmissionId = FirstNonBlank(batchNumber, status?.ClearinghouseTraceNumber),
            Errors = issues.Where(IsRejection).ToList(),
            Warnings = issues.Where(i => !IsRejection(i)).ToList()
        };
        result.Status = StatusFrom(issues, actionCodes, fallbackRejected: result.Errors.Count > 0);
        return result;
    }

    private static GatewayClaimAcknowledgmentLineResult MapLine(Stedi277ServiceLineDto line)
    {
        var issues = (line.ServiceClaimStatuses ?? new())
            .SelectMany(s => s.ServiceStatuses ?? new())
            .Select(s => MapIssue(s, actionCode: null))
            .ToList();

        var lineStatus = issues.Count == 0
            ? ClaimAcknowledgmentLineStatus.LineAccepted
            : issues.Any(IsRejection)
                ? ClaimAcknowledgmentLineStatus.LineRejected
                : ClaimAcknowledgmentLineStatus.LineWarning;

        int? lineNumber = null;
        if (int.TryParse(line.LineItemControlNumber, out var parsed))
        {
            lineNumber = parsed;
        }

        return new GatewayClaimAcknowledgmentLineResult
        {
            Status = lineStatus,
            LineItemControlNumber = line.LineItemControlNumber,
            LineNumber = lineNumber,
            Errors = issues
        };
    }

    private static GatewayClaimAcknowledgmentIssue MapIssue(Stedi277StatusDto status, string? actionCode)
    {
        var description = FirstNonBlank(
            status.StatusCodeValue, status.HealthCareClaimStatusCategoryCodeValue);
        return new GatewayClaimAcknowledgmentIssue
        {
            CategoryCode = status.HealthCareClaimStatusCategoryCode,
            StatusCode = status.StatusCode,
            Description = description,
            EntityCode = status.EntityIdentifierCode,
            Category = Categorize(status, actionCode)
        };
    }

    internal static ClaimAcknowledgmentErrorCategory Categorize(
        Stedi277StatusDto status, string? actionCode)
    {
        _ = actionCode;
        var code = status.StatusCode?.Trim();
        var entity = status.EntityIdentifierCode?.Trim();

        if (string.Equals(code, "97", StringComparison.OrdinalIgnoreCase))
        {
            return ClaimAcknowledgmentErrorCategory.DuplicateClaim;
        }

        if (string.Equals(code, "21", StringComparison.OrdinalIgnoreCase))
        {
            return ClaimAcknowledgmentErrorCategory.MissingRequiredField;
        }

        if (string.Equals(code, "33", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity, "1P", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity, "85", StringComparison.OrdinalIgnoreCase))
        {
            return ClaimAcknowledgmentErrorCategory.InvalidProvider;
        }

        if (string.Equals(code, "164", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity, "IL", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entity, "QC", StringComparison.OrdinalIgnoreCase))
        {
            return ClaimAcknowledgmentErrorCategory.InvalidSubscriber;
        }

        if (string.Equals(entity, "PR", StringComparison.OrdinalIgnoreCase))
        {
            return ClaimAcknowledgmentErrorCategory.InvalidPayer;
        }

        return ClaimAcknowledgmentErrorCategory.Other;
    }

    internal static ClaimAcknowledgmentStatus Rollup(
        IReadOnlyList<ClaimAcknowledgmentStatus> claimStatuses,
        IReadOnlyList<Stedi277StatusDto> providerStatuses,
        IReadOnlyList<GatewayClaimAcknowledgmentLineResult> lines,
        bool hadStructure)
    {
        if (!hadStructure)
        {
            return ClaimAcknowledgmentStatus.Malformed;
        }

        var statuses = claimStatuses.Count > 0
            ? claimStatuses
            : new[] { StatusFrom(providerStatuses.Select(s => MapIssue(s, null)).ToList(), Array.Empty<string>(), false) };

        var accepted = statuses.Count(s =>
            s is ClaimAcknowledgmentStatus.Accepted or ClaimAcknowledgmentStatus.AcceptedWithWarnings);
        var rejected = statuses.Count(s => s == ClaimAcknowledgmentStatus.Rejected);

        if (accepted > 0 && rejected > 0)
        {
            return ClaimAcknowledgmentStatus.Partial;
        }

        if (rejected > 0 && accepted == 0)
        {
            return ClaimAcknowledgmentStatus.Rejected;
        }

        if (accepted > 0)
        {
            var warnings = statuses.Any(s => s == ClaimAcknowledgmentStatus.AcceptedWithWarnings) ||
                           lines.Any(l => l.Status == ClaimAcknowledgmentLineStatus.LineWarning);
            return warnings
                ? ClaimAcknowledgmentStatus.AcceptedWithWarnings
                : ClaimAcknowledgmentStatus.Accepted;
        }

        return StatusFrom(
            providerStatuses.Select(s => MapIssue(s, null)).ToList(),
            Array.Empty<string>(),
            fallbackRejected: false);
    }

    private static ClaimAcknowledgmentStatus StatusFrom(
        IReadOnlyList<GatewayClaimAcknowledgmentIssue> issues,
        IReadOnlyList<string?> actionCodes,
        bool fallbackRejected)
    {
        if (actionCodes.Any(a => string.Equals(a, "U", StringComparison.OrdinalIgnoreCase)))
        {
            return ClaimAcknowledgmentStatus.Rejected;
        }

        if (actionCodes.Any(a => string.Equals(a, "WQ", StringComparison.OrdinalIgnoreCase)))
        {
            return issues.Any(IsRejection)
                ? ClaimAcknowledgmentStatus.AcceptedWithWarnings
                : ClaimAcknowledgmentStatus.Accepted;
        }

        var categories = issues
            .Select(i => i.CategoryCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim().ToUpperInvariant())
            .ToList();

        if (categories.Count == 0)
        {
            return fallbackRejected ? ClaimAcknowledgmentStatus.Rejected : ClaimAcknowledgmentStatus.Accepted;
        }

        var rejected = categories.Any(IsRejectedCategory);
        var accepted = categories.Any(IsAcceptedCategory);
        if (accepted && rejected)
        {
            return ClaimAcknowledgmentStatus.Partial;
        }

        if (rejected)
        {
            return ClaimAcknowledgmentStatus.Rejected;
        }

        if (accepted)
        {
            return issues.Any(i => !IsRejection(i) && !string.IsNullOrWhiteSpace(i.Description)) &&
                   issues.Count > 1
                ? ClaimAcknowledgmentStatus.AcceptedWithWarnings
                : ClaimAcknowledgmentStatus.Accepted;
        }

        return fallbackRejected ? ClaimAcknowledgmentStatus.Rejected : ClaimAcknowledgmentStatus.Accepted;
    }

    private static bool IsRejection(GatewayClaimAcknowledgmentIssue issue) =>
        IsRejectedCategory(issue.CategoryCode);

    internal static bool IsAcceptedCategory(string? code)
    {
        var c = code?.Trim().ToUpperInvariant();
        return c is "A0" or "A1" or "A2";
    }

    internal static bool IsRejectedCategory(string? code)
    {
        var c = code?.Trim().ToUpperInvariant();
        return c is "A3" or "A4" or "A6" or "A7" or "A8";
    }

    private static void CollectProviderStatuses(
        Stedi277ClaimStatusTransactionDto cst, List<Stedi277StatusDto> sink)
    {
        foreach (var provider in cst.ProviderClaimStatuses ?? Enumerable.Empty<Stedi277ProviderClaimStatusDto>())
        {
            sink.AddRange(provider.ProviderStatuses ?? new());
        }
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
