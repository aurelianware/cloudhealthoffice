using System.Diagnostics;
using System.Text.RegularExpressions;
using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication.CodeValidity;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Checks that the claim's ICD-10-CM diagnoses, ICD-10-PCS procedures and HCPCS
/// Level II line codes were valid on the date of service, against
/// terminology-service's dated CMS code sets (CodeSystem/$batch-validate-code).
/// Runs at <see cref="Order"/> = 105, right after structural scrubbing.
///
/// <list type="bullet">
///   <item><description>Dates: diagnoses and ICD-10-PCS codes use the
///     discharge (through) date on an inpatient claim and the from date
///     otherwise; a line code uses its line's from date.</description></item>
///   <item><description>CPT, CDT and other codes are not checked (no public
///     dated code set), and an "unknown" answer (no release loaded for that
///     date) is never a finding.</description></item>
///   <item><description>terminology-service unreachable or slow → the claim
///     passes unchecked with a note. A new check must not hold claims on an
///     outage.</description></item>
///   <item><description>Findings go to
///     <see cref="ClaimAdjudicationContext.CodeValidityFindings"/>; then by
///     <see cref="CodeValidityEnforcementMode"/>: Report (default) passes with
///     notes, PendForReview pends <see cref="PendCode"/>, Deny denies with
///     CARC 146 (a diagnosis) or 181 (a procedure).</description></item>
/// </list>
/// </summary>
public sealed partial class CodeValidityStage : IClaimAdjudicationStage
{
    public const string StageName = "CodeValidity";

    /// <summary>Pend code: a code not valid on the date of service.</summary>
    public const string PendCode = "CODEVALID";

    /// <summary>CARC 146 — diagnosis was invalid for the date(s) of service reported.</summary>
    public const string DiagnosisInvalidCarc = "146";

    /// <summary>CARC 181 — procedure code was invalid on the date of service.</summary>
    public const string ProcedureInvalidCarc = "181";

    public const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string Icd10PcsSystem = "http://www.cms.gov/Medicare/Coding/ICD10";
    public const string HcpcsSystem = "http://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";

    private static readonly ActivitySource ActivitySource = new("ClaimsService.Adjudication");

    private readonly ICodeValidityClient _client;
    private readonly CodeValidityOptions _options;
    private readonly ILogger<CodeValidityStage> _logger;

    public CodeValidityStage(
        ICodeValidityClient client, IOptions<CodeValidityOptions> options, ILogger<CodeValidityStage> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 105;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        var mode = _options.For(context.TenantId).Mode;
        if (mode == CodeValidityEnforcementMode.Off)
            return ClaimAdjudicationStageResult.Pass(StageName);

        var checks = Checks(context.Claim);
        if (checks.Count == 0)
            return ClaimAdjudicationStageResult.Pass(StageName);

        using var activity = ActivitySource.StartActivity("Adjudication.CodeValidity", ActivityKind.Internal);
        activity?.SetTag("claim.versionId", context.ClaimVersionId);
        activity?.SetTag("code_validity.codes", checks.Count);

        var answers = await _client.ValidateAsync(
            context.TenantId,
            checks.Select(c => c.Query).ToList(),
            TimeSpan.FromMilliseconds(Math.Clamp(_options.TimeoutMilliseconds, 100, 30_000)),
            ct).ConfigureAwait(false);

        if (answers is null)
        {
            activity?.SetTag("code_validity.outcome", "unavailable");
            return new ClaimAdjudicationStageResult
            {
                StageName = StageName,
                Notes = ["Codes not checked against the date of service: terminology-service did not answer."],
            };
        }

        var findings = checks.Zip(answers)
            .Where(x => x.Second.IsInvalid)
            .Select(x => new CodeValidityFinding
            {
                Kind = x.First.Kind,
                LineNumber = x.First.LineNumber,
                CodeSystem = x.First.Label,
                Code = x.First.Query.Code,
                DateOfService = x.First.Query.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                Reason = x.Second.Reason ?? "invalid",
                Message = x.Second.Message,
                ReleaseVersion = x.Second.ReleaseVersion,
            })
            .ToList();

        context.CodeValidityFindings.AddRange(findings);
        activity?.SetTag("code_validity.findings", findings.Count);

        if (findings.Count == 0)
        {
            activity?.SetTag("code_validity.outcome", "valid");
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var reason = Truncate(string.Join("; ", findings.Select(f => f.Label())));
        activity?.SetTag("code_validity.outcome", mode.ToString());
        _logger.LogInformation(
            "CodeValidityStage found {Count} code(s) not valid on the date of service on claim {ClaimVersionId} ({Mode})",
            findings.Count, LogSanitizer.Sanitize(context.ClaimVersionId), mode);

        switch (mode)
        {
            case CodeValidityEnforcementMode.Deny:
                var carc = findings.Any(f => f.Kind == CodeValidityFinding.DiagnosisKind)
                    ? DiagnosisInvalidCarc
                    : ProcedureInvalidCarc;
                Deny(context, carc, reason);
                return ClaimAdjudicationStageResult.Deny(StageName, reason);

            case CodeValidityEnforcementMode.PendForReview:
                context.PendDetails = new PendDetails
                {
                    PendCode = PendCode,
                    PendReason = reason,
                    PendedAt = DateTime.UtcNow,
                };
                return ClaimAdjudicationStageResult.Pend(StageName, reason);

            default:
                return new ClaimAdjudicationStageResult
                {
                    StageName = StageName,
                    Notes = findings.Select(f => f.Message ?? f.Label()).ToList(),
                };
        }
    }

    /// <summary>Denies the claim with the CARC on the header and every line (group CO), as timely filing does.</summary>
    private static void Deny(ClaimAdjudicationContext context, string carc, string reason)
    {
        var claim = context.Claim;
        var description = carc == DiagnosisInvalidCarc
            ? "The diagnosis was invalid for the date(s) of service reported"
            : "The procedure code was invalid on the date of service";

        AdjudicationDenialCodes.Apply(context, carc, reason);
        context.AdjudicationResult.AdjustmentReasons.Add(new ClaimAdjustmentReason
        {
            GroupCode = "CO",
            ReasonCode = carc,
            Amount = claim.TotalChargeAmount,
            Description = description,
        });
        context.LineAdjudicationResults = claim.ClaimLines
            .Select(l => new LineAdjudicationResult
            {
                AllowedAmount = 0m,
                PaidAmount = 0m,
                PatientResponsibility = 0m,
                AdjustmentReasons = new List<ClaimAdjustmentReason>
                {
                    new() { GroupCode = "CO", ReasonCode = carc, Amount = l.ChargeAmount, Description = description },
                },
            })
            .ToList();
    }

    internal sealed record Check(string Kind, int? LineNumber, string Label, CodeValidityQuery Query);

    /// <summary>The codes of a claim this stage can check, in claim order.</summary>
    internal static List<Check> Checks(AdapterClaim claim)
    {
        var inpatient = InpatientGroupingStage.IsInpatient(claim);
        var claimDate = DateOnly.FromDateTime(
            (inpatient && claim.ServiceDateTo != default ? claim.ServiceDateTo : claim.ServiceDateFrom).Date);
        var checks = new List<Check>();
        if (claimDate == default) return checks;

        foreach (var dx in claim.DiagnosisCodes)
        {
            var code = Normalize(dx.Code);
            if (code.Length == 0) continue;
            switch (System(dx.CodeQualifier))
            {
                case Icd10CmSystem:
                    checks.Add(new Check(CodeValidityFinding.DiagnosisKind, null, "ICD-10-CM",
                        new CodeValidityQuery(Icd10CmSystem, code, claimDate)));
                    break;
                case Icd10PcsSystem:
                    checks.Add(new Check(CodeValidityFinding.ProcedureKind, null, "ICD-10-PCS",
                        new CodeValidityQuery(Icd10PcsSystem, code, claimDate)));
                    break;
            }
        }

        foreach (var line in claim.ClaimLines)
        {
            var code = Normalize(line.ProcedureCode);
            if (!HcpcsLevelII().IsMatch(code)) continue;
            var lineDate = DateOnly.FromDateTime(
                (line.ServiceDateFrom != default ? line.ServiceDateFrom : claim.ServiceDateFrom).Date);
            checks.Add(new Check(CodeValidityFinding.ProcedureKind, line.LineNumber, "HCPCS",
                new CodeValidityQuery(HcpcsSystem, code, lineDate)));
        }

        return checks;
    }

    /// <summary>
    /// The system an X12 HI qualifier names: ABK/ABF/ABJ/ABN/APR (and an empty
    /// qualifier) are ICD-10-CM, BBR/BBQ are ICD-10-PCS; ICD-9 and others are not checked.
    /// </summary>
    internal static string? System(string? qualifier)
    {
        var q = (qualifier ?? string.Empty).Trim().ToUpperInvariant();
        return q switch
        {
            "" or "ABK" or "ABF" or "ABJ" or "ABN" or "APR" => Icd10CmSystem,
            "BBR" or "BBQ" => Icd10PcsSystem,
            _ => null,
        };
    }

    private static string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().Replace(".", string.Empty).ToUpperInvariant();

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];

    /// <summary>
    /// A HCPCS Level II code: a letter A–V and four digits. D codes (CDT, ADA
    /// copyright) are not in the CMS file and are left out.
    /// </summary>
    [GeneratedRegex("^[A-CE-V][0-9]{4}$")]
    private static partial Regex HcpcsLevelII();
}
