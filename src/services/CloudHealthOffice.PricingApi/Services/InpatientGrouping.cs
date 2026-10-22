using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using CloudHealthOffice.PricingApi.Models;
using GrouperDomain = CloudHealthOffice.GrouperEngine.Domain;

namespace CloudHealthOffice.PricingApi.Services;

/// <summary>
/// What asking the grouper produced: a DRG to price with, or a reason the
/// claim has none.
/// </summary>
/// <param name="DrgCode">The DRG to price, or null.</param>
/// <param name="Warning">
/// What to tell the caller when there is no DRG. Null when there is one.
/// </param>
/// <param name="Notes">
/// What the grouper said about the answer, worth returning even on success:
/// the hospital-acquired-condition policy moving the DRG, or codes its tables
/// do not hold.
/// </param>
public sealed record InpatientDrgOutcome(string? DrgCode, string? Warning, IReadOnlyList<string> Notes)
{
    /// <summary>The claim already carried a DRG, so nothing was grouped.</summary>
    public static InpatientDrgOutcome Billed(string drg) => new(drg, null, []);
}

/// <summary>
/// Resolves the DRG an inpatient claim is priced by.
/// </summary>
public interface IInpatientDrgResolver
{
    /// <summary>
    /// The DRG for this claim: the one it carries, or one from the configured
    /// grouper, or none with a reason.
    /// </summary>
    Task<InpatientDrgOutcome> ResolveAsync(RepricingRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Asks the configured grouper for a DRG when the claim does not carry one.
///
/// <para>
/// Which grouper answers is a deployment's decision, made by what is bound to
/// <see cref="IMsDrgGrouper"/>. <see cref="BilledDrgGrouper"/> is the default
/// and reproduces the platform's behaviour before it had a grouper, so
/// installing this resolver changes no payment anywhere until a deployment
/// deliberately binds a real grouper.
/// </para>
///
/// <para>
/// The one judgement this class makes is when NOT to ask. A repricing request
/// carries diagnoses but no procedures and no discharge date, and a stay
/// grouped without its operating-room procedures lands in the medical
/// partition of its MDC and returns a plausible DRG that pays the wrong
/// amount. So the grouper is asked only when the request carries
/// <see cref="RepricingRequest.InpatientStay"/>; without it the claim is
/// treated exactly as it was before, as one that cannot be priced.
/// </para>
/// </summary>
public sealed class GrouperInpatientDrgResolver : IInpatientDrgResolver
{
    private readonly IMsDrgGrouper _grouper;
    private readonly ILogger<GrouperInpatientDrgResolver> _logger;

    public GrouperInpatientDrgResolver(IMsDrgGrouper grouper, ILogger<GrouperInpatientDrgResolver> logger)
    {
        _grouper = grouper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InpatientDrgOutcome> ResolveAsync(
        RepricingRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.DrgCode))
            return InpatientDrgOutcome.Billed(request.DrgCode.Trim());

        if (request.InpatientStay is not { } stay)
        {
            return new InpatientDrgOutcome(
                null,
                "No DRG code provided. Inpatient pricing requires an MS-DRG: send DrgCode, or send "
                + "InpatientStay (discharge date and ICD-10-PCS procedures) so the claim can be grouped.",
                []);
        }

        if (string.IsNullOrWhiteSpace(request.PrimaryDiagnosis))
        {
            return new InpatientDrgOutcome(
                null,
                "No DRG code provided and the claim carries no primary diagnosis, so it cannot be grouped.",
                []);
        }

        var result = await _grouper
            .GroupAsync(BuildGroupingRequest(request, stay), cancellationToken)
            .ConfigureAwait(false);

        if (result.Status != GrouperDomain.GroupingStatus.Grouped || result.Drg is null)
        {
            _logger.LogInformation(
                "Inpatient claim not grouped: {Status} (grouper version {Version})",
                result.Status, result.GrouperVersion);

            return new InpatientDrgOutcome(
                null,
                $"The claim could not be grouped ({result.Status}). " + string.Join(" ", result.Notes),
                result.Notes);
        }

        return new InpatientDrgOutcome(result.Drg, null, result.Notes);
    }

    /// <summary>
    /// Translates the repricing request into the grouper's own request.
    /// </summary>
    internal static MsDrgGroupingRequest BuildGroupingRequest(
        RepricingRequest request, InpatientStayRequest stay)
    {
        var poa = stay.SecondaryDiagnosisPoa ?? [];
        var secondaries = (request.SecondaryDiagnoses ?? [])
            .Select((code, index) => new GroupingDiagnosis(
                Normalise(code),

                // A short or absent indicator list is not an error: it means
                // the facility sent none for those diagnoses, which the HAC
                // rules have their own answer for. Pairing them by position
                // only works while the two lists are the same order, which is
                // what the field documents.
                index < poa.Count ? poa[index] : null))
            .ToList();

        return new MsDrgGroupingRequest
        {
            PrincipalDiagnosis = new GroupingDiagnosis(Normalise(request.PrimaryDiagnosis!)),
            SecondaryDiagnoses = secondaries,
            Procedures = (stay.ProcedureCodes ?? [])
                .Select(code => new GroupingProcedure(Normalise(code)))
                .ToList(),
            DischargeDate = stay.DischargeDate,
            DischargeStatus = stay.DischargeStatus,
            AgeYears = stay.AgeYears,
            AgeDays = stay.AgeDays,
            Sex = stay.Sex,
            LengthOfStayDays = stay.LengthOfStayDays,
            BirthWeightGrams = stay.BirthWeightGrams,
            PoaReporting = stay.FacilityReportsPoa switch
            {
                true => HospitalPoaReporting.NonExempt,
                false => HospitalPoaReporting.Exempt,
                null => HospitalPoaReporting.Unknown
            },
            BilledDrg = request.DrgCode
        };
    }

    /// <summary>
    /// The grouper's tables are keyed without the decimal point and in upper
    /// case, and the grouper looks codes up verbatim. The API takes codes as
    /// they appear on a claim, so this is the one place they are put into the
    /// tables' shape.
    /// </summary>
    private static string Normalise(string code) =>
        code.Replace(".", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
}
