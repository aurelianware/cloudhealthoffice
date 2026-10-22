namespace CloudHealthOffice.Infrastructure.Edi.Files;

/// <summary>Which way a file travelled.</summary>
public static class EdiFileDirection
{
    public const string Inbound = "Inbound";
    public const string Outbound = "Outbound";
}

/// <summary>How a file reached CHO (or left it).</summary>
public static class EdiFileSource
{
    /// <summary>Uploaded by a user through an API or the portal.</summary>
    public const string Upload = "Upload";

    /// <summary>Picked up from a trading partner's SFTP folder (the Argo ingest workflows).</summary>
    public const string Sftp = "Sftp";

    /// <summary>Through the clearinghouse gateway.</summary>
    public const string Gateway = "Gateway";

    /// <summary>Another API call (real-time or service-to-service).</summary>
    public const string Api = "Api";

    public static readonly IReadOnlyList<string> All = [Upload, Sftp, Gateway, Api];

    /// <summary>The canonical spelling of <paramref name="value"/>, or <see cref="Upload"/> when it is not one of <see cref="All"/>.</summary>
    public static string Normalize(string? value) =>
        All.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Upload;
}

/// <summary>
/// Where a file is in its lifecycle (docs/architecture/edi-operations.md §3).
/// Received → EnvelopeChecked → Acknowledged → Parsed → Applied; or Rejected,
/// or Duplicate (an ISA13 already received from this sender).
/// </summary>
public static class EdiFileStatus
{
    public const string Received = "Received";
    public const string EnvelopeChecked = "EnvelopeChecked";
    public const string Acknowledged = "Acknowledged";
    public const string Parsed = "Parsed";
    public const string Applied = "Applied";
    public const string Rejected = "Rejected";
    public const string Duplicate = "Duplicate";
}

/// <summary>
/// One X12 file in or out of CHO, whichever path it took. Holds envelope
/// identifiers, counts and file-level errors only: no member or claim data,
/// so it can be listed with the service's read permission. Record-level
/// outcomes stay on the records (claim import transactions, enrollment
/// transactions), which point back here through <c>EdiFileId</c>.
/// </summary>
public sealed class EdiFile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;

    /// <summary><see cref="EdiFileDirection"/>.</summary>
    public string Direction { get; set; } = EdiFileDirection.Inbound;

    /// <summary>The transaction the intake path expected (837, 834, ...).</summary>
    public string? TransactionType { get; set; }

    /// <summary>ST01 values found in the file (e.g. 837, 834).</summary>
    public List<string> TransactionSetIds { get; set; } = new();

    /// <summary>X12 version of the first interchange (ISA12), e.g. 00501.</summary>
    public string? Version { get; set; }

    /// <summary><see cref="EdiFileSource"/>.</summary>
    public string Source { get; set; } = EdiFileSource.Upload;

    public string? FileName { get; set; }

    /// <summary>The SFTP path (or other source location) the file came from.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Who handed the file in: the user, or the service client for SFTP.</summary>
    public string? ReceivedBy { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file as received (hex, lower case).</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// The raw file in the document catalog. Not stored yet: the catalog needs
    /// an EDI-file subject and the edi:raw permission (edi-operations.md §9
    /// step 4). Until then <see cref="Sha256"/> identifies the file.
    /// </summary>
    public string? RawDocumentId { get; set; }

    /// <summary>ISA05/ISA06 and ISA07/ISA08 of the first interchange, trimmed.</summary>
    public string? SenderQualifier { get; set; }
    public string? SenderId { get; set; }
    public string? ReceiverQualifier { get; set; }
    public string? ReceiverId { get; set; }

    /// <summary>ISA15: P (production) or T (test).</summary>
    public string? UsageIndicator { get; set; }

    /// <summary>Every ISA13 in the file.</summary>
    public List<string> InterchangeControlNumbers { get; set; } = new();

    /// <summary>Every GS06 in the file.</summary>
    public List<string> GroupControlNumbers { get; set; } = new();

    /// <summary>Every ST02 in the file.</summary>
    public List<string> TransactionSetControlNumbers { get; set; } = new();

    /// <summary><see cref="EdiFileStatus"/>.</summary>
    public string Status { get; set; } = EdiFileStatus.Received;

    /// <summary>When each status was reached, in order.</summary>
    public List<EdiFileStatusChange> History { get; set; } = new();

    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>TA104 of the TA1 we sent (A, E, R), when one was due.</summary>
    public string? Ta1AckCode { get; set; }
    public string? Ta1ControlNumber { get; set; }

    /// <summary>The 999 we sent: AK901 (A, E, P, R) and its ISA13.</summary>
    public string? Ack999Code { get; set; }
    public string? Ack999ControlNumber { get; set; }

    /// <summary>Records applied, and records rejected (claims, members).</summary>
    public int AcceptedRecords { get; set; }
    public int RejectedRecords { get; set; }

    /// <summary>File-level errors: envelope, syntax, unreadable. Record errors stay on the records.</summary>
    public List<string> Errors { get; set; } = new();
}

public sealed class EdiFileStatusChange
{
    public string Status { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Filters for listing files. Unset filters match everything.</summary>
public sealed class EdiFileQuery
{
    public string? Direction { get; set; }
    public string? TransactionType { get; set; }
    public string? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    /// <summary>Matches any ISA13, GS06 or ST02 in the file.</summary>
    public string? ControlNumber { get; set; }

    public int Limit { get; set; } = 100;
}
