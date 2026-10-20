namespace CloudHealthOffice.PlanEdits.Contracts;

/// <summary>
/// Fixed values of the plan edit service contract, version 1 (ADR 019).
/// A breaking change ships as a new version; within a version, fields are
/// only ever added, and both sides ignore fields they do not know.
/// </summary>
public static class PlanEditContract
{
    /// <summary>The contract version this library implements, sent and expected as <c>contractVersion</c>.</summary>
    public const string Version = "1";

    /// <summary>Media type of the request and the response.</summary>
    public const string MediaType = "application/json";

    /// <summary>Unix time in seconds at which CHO signed the request.</summary>
    public const string TimestampHeader = "X-CHO-Timestamp";

    /// <summary><c>v1=</c> followed by the lower-case hex HMAC-SHA256 (see <see cref="PlanEditSignature"/>).</summary>
    public const string SignatureHeader = "X-CHO-Signature";

    /// <summary>The request's <see cref="PlanEditRequest.RequestId"/>, repeated as a header for logs and proxies.</summary>
    public const string RequestIdHeader = "X-CHO-Request-Id";

    /// <summary>
    /// The same value as <see cref="RequestIdHeader"/>. Evaluating edits has no
    /// side effect, so CHO may send a request again after a transient failure;
    /// the key lets a plan service recognise the repeat.
    /// </summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Largest response body CHO reads, in bytes.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>Most edits one response may return.</summary>
    public const int MaxEdits = 100;

    /// <summary>Longest <see cref="PlanEdit.RuleId"/>.</summary>
    public const int MaxRuleIdLength = 40;

    /// <summary>Longest <see cref="PlanEdit.Message"/>.</summary>
    public const int MaxMessageLength = 500;

    /// <summary>How far a request's timestamp may be from the receiver's clock before it is refused.</summary>
    public static readonly TimeSpan SignatureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Shortest signing secret, in characters, either side accepts.</summary>
    public const int MinSecretLength = 32;
}

/// <summary>Why CHO is asking.</summary>
public static class PlanEditPurpose
{
    /// <summary>The claim is being adjudicated.</summary>
    public const string Adjudication = "adjudication";

    /// <summary>
    /// An examiner approved a pended claim and it is being adjudicated again
    /// with their decisions. Return the same edits for the same facts: CHO
    /// matches them to the examiner's decisions by rule and lines.
    /// </summary>
    public const string ApprovalReadjudication = "approval-readjudication";
}
