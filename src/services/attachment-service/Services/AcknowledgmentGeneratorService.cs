using AttachmentService.Models;

namespace AttachmentService.Services;

/// <summary>
/// Pure EDI generation for 999 and 824 transactions.
/// No infrastructure dependencies — testable without Cosmos.
/// </summary>
public class AcknowledgmentGeneratorService
{
    private readonly ILogger<AcknowledgmentGeneratorService> _logger;

    public AcknowledgmentGeneratorService(ILogger<AcknowledgmentGeneratorService> logger)
    {
        _logger = logger;
    }

    public string Generate999(Attachment attachment, TradingPartner tradingPartner)
    {
        var now = DateTime.UtcNow;
        var controlNumber = MakeControlNumber();

        var isa = BuildIsa(tradingPartner, now, controlNumber);
        var gs  = $"GS*FA*{tradingPartner.ApplicationSenderId ?? "SENDER"}*{tradingPartner.ApplicationReceiverId ?? "RECEIVER"}*{now:yyyyMMdd}*{now:HHmm}*1*X*005010~";
        var st  = "ST*999*0001*005010~";
        var ak1 = "AK1*HS*1~";
        var ak2 = $"AK2*275*{attachment.Id[..Math.Min(9, attachment.Id.Length)]}~";
        var ak5 = "AK5*A~";
        var ak9 = "AK9*A*1*1*1~";
        var se  = "SE*6*0001~"; // ST AK1 AK2 AK5 AK9 SE = 6
        var ge  = "GE*1*1~";
        var iea = $"IEA*1*{controlNumber}~";

        return $"{isa}{gs}{st}{ak1}{ak2}{ak5}{ak9}{se}{ge}{iea}";
    }

    /// <summary>
    /// Generate 824 Application Advice (005010X186A1).
    ///
    /// Segments written:
    ///   ST  — transaction set header
    ///   BGN — beginning segment (reference number = attachment ID)
    ///   OTI — original transaction identification (TA / TR / TP)
    ///   REF*D9 — claim number (when attachment is linked to a claim)
    ///   REF*EJ — attachment control number / RFAI reference (solicited only)
    ///   MSG — human-readable status message
    ///   SE  — transaction set trailer (count is computed dynamically)
    /// </summary>
    public string Generate824(Attachment attachment, TradingPartner tradingPartner)
    {
        var now = DateTime.UtcNow;
        var controlNumber = MakeControlNumber();

        var isa = BuildIsa(tradingPartner, now, controlNumber);
        var gs  = $"GS*AG*{tradingPartner.ApplicationSenderId ?? "SENDER"}*{tradingPartner.ApplicationReceiverId ?? "RECEIVER"}*{now:yyyyMMdd}*{now:HHmm}*1*X*005010~";

        var acceptanceCode = attachment.Status switch
        {
            "Linked"    => "TA",
            "Validated" => "TA",
            "Failed"    => "TR",
            _           => "TP"
        };

        var segments = new List<string>
        {
            "ST*824*0001*005010~",
            $"BGN*11*{attachment.Id}*{now:yyyyMMdd}*{now:HHmmss}~",
            $"OTI*{acceptanceCode}*TN*{attachment.Id}~",
        };

        // REF*D9 — Claim number reference (submitter's patient control number)
        if (!string.IsNullOrWhiteSpace(attachment.ClaimId))
            segments.Add($"REF*D9*{attachment.ClaimId}~");

        // REF*EJ — Attachment control number / RFAI reference (solicited attachments only)
        if (!string.IsNullOrWhiteSpace(attachment.RFAIReference))
            segments.Add($"REF*EJ*{attachment.RFAIReference}~");

        // TED — Transaction Error Data (rejections only)
        // TED01 = X12 Error Type Code, TED02 = free-form description
        if (acceptanceCode == "TR" || acceptanceCode == "TP")
        {
            var ted01 = AttachmentRejectionCode.ToTed01ErrorTypeCode(attachment.RejectionCode);
            if (ted01 is not null)
            {
                var ted02 = !string.IsNullOrWhiteSpace(attachment.Notes)
                    ? attachment.Notes
                    : AttachmentRejectionCode.DefaultDescription(attachment.RejectionCode);
                segments.Add($"TED*{ted01}*{ted02}~");
            }
        }

        var msgText = attachment.Status switch
        {
            "Linked"    => $"Attachment accepted and linked to {GetParentType(attachment)} {GetParentId(attachment)}",
            "Validated" => "Attachment accepted and validated",
            "Failed"    => $"Attachment rejected: {(!string.IsNullOrWhiteSpace(attachment.Notes) ? attachment.Notes : AttachmentRejectionCode.DefaultDescription(attachment.RejectionCode))}",
            _           => "Attachment received and pending validation"
        };
        segments.Add($"MSG*{msgText}~");

        // SE01 = count of segments from ST to SE inclusive
        segments.Add($"SE*{segments.Count + 1}*0001~");

        var ge  = "GE*1*1~";
        var iea = $"IEA*1*{controlNumber}~";

        return $"{isa}{gs}{string.Join(string.Empty, segments)}{ge}{iea}";
    }

    private static string BuildIsa(TradingPartner tp, DateTime now, string controlNumber)
    {
        var senderId   = (tp.InterchangeSenderId   ?? "SENDER").PadRight(15);
        var receiverId = (tp.InterchangeReceiverId ?? "RECEIVER").PadRight(15);
        return $"ISA*00*          *00*          *ZZ*{senderId}*ZZ*{receiverId}*{now:yyMMdd}*{now:HHmm}*^*00501*{controlNumber}*0*P*:~";
    }

    private static string MakeControlNumber() =>
        DateTime.UtcNow.Ticks.ToString()[9..18];

    private static string GetParentType(Attachment a)
    {
        if (!string.IsNullOrWhiteSpace(a.ClaimId))          return "Claim";
        if (!string.IsNullOrWhiteSpace(a.AuthorizationId))  return "Authorization";
        if (!string.IsNullOrWhiteSpace(a.AppealId))         return "Appeal";
        return "Unknown";
    }

    private static string GetParentId(Attachment a) =>
        a.ClaimId ?? a.AuthorizationId ?? a.AppealId ?? "Unknown";
}
