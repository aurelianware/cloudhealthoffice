namespace CloudHealthOffice.Infrastructure.Edi.Interchange;

/// <summary>Which way a TA1 travelled.</summary>
public static class Ta1Direction
{
    /// <summary>A TA1 CHO produced for an interchange it received.</summary>
    public const string Generated = "Generated";

    /// <summary>A TA1 a trading partner sent for an interchange CHO sent.</summary>
    public const string Received = "Received";
}

/// <summary>
/// A stored TA1, generated or received. Holds envelope identifiers only: the
/// TA1 text carries no transaction content (no member or claim data).
/// </summary>
public sealed class InterchangeAcknowledgmentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    /// <summary><see cref="Ta1Direction"/>.</summary>
    public string Direction { get; set; } = Ta1Direction.Generated;

    /// <summary>For a generated TA1, the transaction the intake path expected (837, 834, 270, ...).</summary>
    public string? TransactionType { get; set; }

    public string? FileName { get; set; }

    /// <summary>TA101: ISA13 of the interchange acknowledged.</summary>
    public string AcknowledgedControlNumber { get; set; } = string.Empty;

    /// <summary>ISA05/ISA06 of the interchange acknowledged (its sender), trimmed.</summary>
    public string? SenderQualifier { get; set; }
    public string? SenderId { get; set; }

    /// <summary>ISA07/ISA08 of the interchange acknowledged (its receiver), trimmed.</summary>
    public string? ReceiverQualifier { get; set; }
    public string? ReceiverId { get; set; }

    /// <summary>TA104: A, E or R.</summary>
    public string AckCode { get; set; } = Ta1AckCodes.Accepted;

    /// <summary>TA105.</summary>
    public string NoteCode { get; set; } = Ta1NoteCodes.NoError;

    public string? NoteDescription { get; set; }

    /// <summary>Every envelope finding (generated TA1s), the first of which is TA105.</summary>
    public List<InterchangeFindingRecord> Findings { get; set; } = [];

    /// <summary>ISA13 of the TA1 interchange itself.</summary>
    public string? Ta1ControlNumber { get; set; }

    /// <summary>The TA1 interchange text.</summary>
    public string? Ta1Content { get; set; }

    /// <summary>For a received TA1: the outbound interchange it matched, if any.</summary>
    public string? OutboundInterchangeId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class InterchangeFindingRecord
{
    public string NoteCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Acknowledgment status of an interchange CHO sent.</summary>
public static class OutboundAckStatus
{
    public const string Pending = "Pending";
    public const string Accepted = "Accepted";
    public const string AcceptedWithErrors = "AcceptedWithErrors";
    public const string Rejected = "Rejected";

    public static string FromAckCode(string ackCode) => ackCode switch
    {
        Ta1AckCodes.Accepted => Accepted,
        Ta1AckCodes.AcceptedWithErrors => AcceptedWithErrors,
        _ => Rejected,
    };
}

/// <summary>
/// An interchange CHO sent (835, 277CA, 271, ...): its envelope identifiers
/// and the partner's TA1 outcome. The interchange text is not kept here: the
/// owning service already stores it where it needs to (e.g. ERA envelopes),
/// and this record must stay free of PHI.
/// </summary>
public sealed class OutboundInterchangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TenantId { get; set; } = string.Empty;

    /// <summary>ST01 of the content (835, 277, 271, ...) or the caller's label (e.g. 277CA).</summary>
    public string TransactionType { get; set; } = string.Empty;

    /// <summary>ISA13.</summary>
    public string ControlNumber { get; set; } = string.Empty;

    public string SenderQualifier { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string ReceiverQualifier { get; set; } = string.Empty;
    public string ReceiverId { get; set; } = string.Empty;

    /// <summary>ISA14 as sent: whether a TA1 was asked for.</summary>
    public bool AckRequested { get; set; }

    /// <summary>The owning record (ERA envelope id, claim id, inquiry id, ...).</summary>
    public string? SourceReference { get; set; }

    public DateTime SentAt { get; set; } = DateTime.UtcNow;

    /// <summary><see cref="OutboundAckStatus"/>.</summary>
    public string AckStatus { get; set; } = OutboundAckStatus.Pending;

    public string? AckNoteCode { get; set; }
    public string? AckNoteDescription { get; set; }
    public DateTime? AckReceivedAt { get; set; }

    /// <summary>The stored received TA1 (<see cref="InterchangeAcknowledgmentRecord.Id"/>).</summary>
    public string? AcknowledgmentId { get; set; }
}

/// <summary>Filter for listing stored TA1s.</summary>
public sealed class InterchangeAcknowledgmentQuery
{
    public string? Direction { get; init; }
    public string? AckCode { get; init; }
    public string? AcknowledgedControlNumber { get; init; }
    public string? SenderId { get; init; }
    public int Limit { get; init; } = 100;
}
