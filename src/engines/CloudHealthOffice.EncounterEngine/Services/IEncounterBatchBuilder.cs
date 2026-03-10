using CloudHealthOffice.EncounterEngine.Domain;

namespace CloudHealthOffice.EncounterEngine.Services;

/// <summary>
/// Wraps one or more encounter records in an X12 ISA/GS/GE/IEA envelope
/// to produce a submission-ready batch file.
/// </summary>
public interface IEncounterBatchBuilder
{
    /// <summary>
    /// Builds a single ISA/GS batch containing all supplied encounter records.
    /// All records must belong to the same tenant.
    /// </summary>
    EncounterBatch Build(IReadOnlyList<EncounterRecord> encounters, BatchEnvelope envelope);
}

/// <summary>
/// ISA/GS envelope parameters supplied by the encounter-service at batch time.
/// </summary>
public record BatchEnvelope
{
    /// <summary>ISA06 — submitter ID (the managed care plan).</summary>
    public string SenderId { get; init; } = default!;

    /// <summary>ISA08 — receiver ID (CMS or state Medicaid).</summary>
    public string ReceiverId { get; init; } = default!;

    /// <summary>GS02 — application sender ID.</summary>
    public string ApplicationSenderId { get; init; } = default!;

    /// <summary>GS03 — application receiver ID.</summary>
    public string ApplicationReceiverId { get; init; } = default!;

    /// <summary>Tenant identifier — stamped on the batch record.</summary>
    public string TenantId { get; init; } = default!;

    /// <summary>
    /// ISA13 / GS06 control numbers for the interchange and group.
    /// Caller is responsible for uniqueness across submissions.
    /// </summary>
    public string InterchangeControlNumber { get; init; } = default!;
    public string GroupControlNumber { get; init; } = default!;
}
