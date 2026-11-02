using System.Globalization;
using System.Text.RegularExpressions;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FeeScheduleEngine.Medicaid;

/// <summary>
/// Prices an inpatient stay by its state's Medicaid APR-DRG method when the
/// claim asks for one (<see cref="PricingRequest.MedicaidDrgMethod"/>), and
/// passes every other claim to the engine unchanged.
///
/// <para>
/// The state's numbers come from the imported tables
/// (<see cref="IMedicaidDrgRateRepository"/>): the weight table and the
/// hospital's rate row in force on the discharge date. The arithmetic is the
/// state pricer's (<see cref="TexasMedicaidDrgPricer"/>). The stay is paid
/// once and allocated across the claim's lines by billed charges, like any
/// other DRG case rate.
/// </para>
///
/// <para>
/// <b>Psychiatric hospitals.</b> Texas pays a freestanding psychiatric
/// hospital its HHSC per diem, not a DRG. When the hospital (facility NPI)
/// has a per diem loaded (<see cref="IMedicaidPsychiatricPerDiemRepository"/>),
/// the stay is priced by <see cref="PsychiatricPerDiemPricer"/> instead:
/// days × per diem on the room and board lines, ancillaries included, with
/// the adult IMD monthly day limit.
/// </para>
///
/// <para>
/// <b>Fail closed.</b> Anything that stops the stay being priced (no
/// DRG-SOI key, no weight table, no rate row for the hospital, two rows that
/// disagree, an outlier that cannot be evaluated) marks every line
/// <see cref="RateSource.Unresolved"/> with the reason, so adjudication pends
/// the claim. It is never priced by a contract or fee schedule instead: the
/// plan said this line of business pays the state method.
/// </para>
/// </summary>
public sealed partial class MedicaidDrgRateResolutionService : IRateResolutionService
{
    private const string TexasState = "TX";

    private readonly IRateResolutionService _inner;
    private readonly IMedicaidDrgRateRepository _rates;
    private readonly IMedicaidInterimRateRepository? _interimRates;
    private readonly IMedicaidPsychiatricPerDiemRepository? _psychRates;
    private readonly PsychiatricPerDiemPolicy _psychPolicy;
    private readonly ILogger<MedicaidDrgRateResolutionService> _logger;

    /// <param name="interimRates">
    /// Hospital interim rates (cost-to-charge ratios). Needed only for stays
    /// that can earn an outlier (Texas: clients under 21); without them those
    /// stays pend.
    /// </param>
    /// <param name="psychRates">
    /// Psychiatric hospital per diems. Without them a psychiatric hospital's
    /// stay is priced by DRG like any other hospital, so load them before
    /// enabling the Texas method for a plan that uses psychiatric hospitals.
    /// </param>
    /// <param name="psychPolicy">Psychiatric per diem rules; Texas defaults.</param>
    public MedicaidDrgRateResolutionService(
        IRateResolutionService inner,
        IMedicaidDrgRateRepository rates,
        ILogger<MedicaidDrgRateResolutionService> logger,
        IMedicaidInterimRateRepository? interimRates = null,
        IMedicaidPsychiatricPerDiemRepository? psychRates = null,
        PsychiatricPerDiemPolicy? psychPolicy = null)
    {
        _psychRates = psychRates;
        _psychPolicy = psychPolicy ?? PsychiatricPerDiemPolicy.Texas();
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _rates = rates ?? throw new ArgumentNullException(nameof(rates));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interimRates = interimRates;
    }

    /// <inheritdoc />
    public async Task<PricingResult> ResolveAsync(PricingRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MedicaidDrgMethods.IsSet(request.MedicaidDrgMethod))
            return await _inner.ResolveAsync(request, ct).ConfigureAwait(false);

        var set = await PriceStayAsync([request], ct).ConfigureAwait(false);
        return set.LineResults[0];
    }

    /// <inheritdoc />
    public async Task<PricingResultSet> ResolveBatchAsync(
        IReadOnlyList<PricingRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0 || !requests.Any(r => MedicaidDrgMethods.IsSet(r.MedicaidDrgMethod)))
            return await _inner.ResolveBatchAsync(requests, ct).ConfigureAwait(false);

        return await PriceStayAsync(requests, ct).ConfigureAwait(false);
    }

    private async Task<PricingResultSet> PriceStayAsync(IReadOnlyList<PricingRequest> requests, CancellationToken ct)
    {
        var lines = requests.OrderBy(r => r.LineNumber).ToList();
        var first = lines[0];

        var methods = lines.Select(r => MedicaidDrgMethods.Normalize(r.MedicaidDrgMethod)).Distinct().ToList();
        if (methods.Count > 1)
            return Unresolved(lines, "the claim's lines name different Medicaid DRG methods; a stay is priced once, by one method");

        var method = methods[0];
        if (method != MedicaidDrgMethods.Texas)
            return Unresolved(lines, $"Medicaid DRG method \"{method}\" is not supported (supported: {MedicaidDrgMethods.Texas})");

        var tenantId = first.TenantId;
        var facilityNpi = (first.FacilityNpi ?? first.ProviderNpi)?.Trim() ?? string.Empty;

        // A freestanding psychiatric hospital is paid its per diem, not a DRG.
        if (_psychRates is not null && facilityNpi.Length > 0)
        {
            var admission = lines.Min(r => r.ServiceDate).Date;
            var perDiem = await _psychRates.FindAsync(tenantId, TexasState, facilityNpi, admission, ct).ConfigureAwait(false);
            if (perDiem is not null)
                return await PricePsychiatricStayAsync(lines, tenantId, facilityNpi, perDiem, ct).ConfigureAwait(false);
        }

        var drgs = lines.Select(r => r.DrgCode?.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (drgs.Count > 1)
            return Unresolved(lines, "the claim's lines carry different DRGs; a stay is priced once, by one DRG");

        var billedKey = drgs[0];
        var keyMatch = string.IsNullOrEmpty(billedKey) ? null : DrgSoiKey().Match(billedKey);
        if (keyMatch is not { Success: true })
        {
            return Unresolved(lines,
                $"Texas Medicaid prices by APR-DRG and severity, and the claim's DRG ({billedKey ?? "none"}) is not a DRG-SOI key such as 139-3; "
                + "the plan must group inpatient claims by APR-DRG (pricing.inpatient.groupDrg and drgClassification aprDrg)");
        }

        // State tables key the base APR-DRG as three digits ("011-1").
        var drgSoi = $"{keyMatch.Groups["drg"].Value.PadLeft(3, '0')}-{keyMatch.Groups["soi"].Value}";

        if (first.LengthOfStay is not { } los || los < 0)
            return Unresolved(lines, "the stay has no length of stay (admission and discharge dates are needed)");

        if (facilityNpi.Length == 0)
            return Unresolved(lines, "the claim has no facility NPI to look up the hospital's SDA");

        var dischargeDate = (first.DischargeDate ?? lines.Max(r => r.ServiceDate)).Date;

        var weights = await _rates.GetWeightTableAsync(tenantId, TexasState, dischargeDate, ct).ConfigureAwait(false);
        if (weights is null)
            return Unresolved(lines, $"no Texas APR-DRG weight table is loaded for a discharge on {Day(dischargeDate)}");

        var matches = await _rates.FindHospitalAsync(tenantId, TexasState, facilityNpi, dischargeDate, ct).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return Unresolved(lines,
                $"no Texas SDA is loaded for hospital NPI {facilityNpi} for a discharge on {Day(dischargeDate)} "
                + "(the hospital is not in the imported SDA files, or its row was held back as inconsistent)");
        }

        var distinct = matches
            .Select(m => (m.Row.FinalBaseRate, m.Row.DeliveryBaseRate, m.Table.HospitalClass.ToUpperInvariant()))
            .Distinct()
            .ToList();
        if (distinct.Count > 1)
        {
            return Unresolved(lines,
                $"hospital NPI {facilityNpi} has {matches.Count} SDA rows that disagree ("
                + string.Join("; ", matches.Select(m => $"TPI {m.Row.StateProviderId} {m.Table.HospitalClass} {m.Row.FinalBaseRate:0.00}"))
                + "); the plan must say which state provider id the claim is billed under");
        }

        var match = matches[0];
        var policy = new TexasDrgPolicy { UniversalMean = weights.UniversalMean ?? match.Table.UniversalMean };
        var stay = new MedicaidDrgStay
        {
            DrgSoi = drgSoi,
            LengthOfStayDays = los,
            AllowedCharges = lines.Sum(r => r.BilledAmount),
            AgeYearsAtAdmission = first.PatientAgeYears,
            DischargeStatus = first.DischargeStatus,
        };

        // The interim rate (the hospital's inpatient cost-to-charge ratio) is
        // only used for outliers, which Texas pays to clients under 21, so it
        // is looked up only for them. Without one such a stay pends.
        MedicaidInterimRate? interim = null;
        if (first.PatientAgeYears is { } age && age < policy.OutlierAgeLimit && _interimRates is not null)
        {
            interim = await _interimRates
                .FindAsync(tenantId, TexasState, facilityNpi, InterimRateKind.Inpatient, dischargeDate, ct)
                .ConfigureAwait(false);
        }

        var price = new TexasMedicaidDrgPricer(policy).Price(
            stay, weights.Find(drgSoi), match.ToPricerHospital(interim?.InpatientRate));
        if (interim is not null)
        {
            price = price with
            {
                Steps = [$"Inpatient interim rate {interim.InpatientRate} effective {Day(interim.EffectiveDate)} (from {interim.SourceFile}).", .. price.Steps],
            };
        }
        if (!price.Priced)
            return Unresolved(lines, $"Texas Medicaid APR-DRG: {price.UnpricedReason}", price.Steps);

        var scheduleName = string.Create(CultureInfo.InvariantCulture,
            $"Texas Medicaid APR-DRG (weights effective {Day(weights.EffectiveDate)}, {match.Table.HospitalClass} SDA effective {Day(match.Table.EffectiveDate)})");

        _logger.LogInformation(
            "Priced a Texas Medicaid stay at APR-DRG {DrgSoi}: {Allowed} for {LineCount} line(s)",
            drgSoi, price.AllowedAmount, lines.Count);

        return Allocate(lines, price, weights.Id, scheduleName);
    }

    /// <summary>
    /// Prices a psychiatric hospital stay at the hospital's per diem. Every
    /// rate the hospital has in force across the stay is passed to the pricer,
    /// so a stay spanning a new fiscal year's rate pays each line at the rate
    /// in force on its dates.
    /// </summary>
    private async Task<PricingResultSet> PricePsychiatricStayAsync(
        IReadOnlyList<PricingRequest> lines, string tenantId, string facilityNpi, MedicaidPsychiatricPerDiem atAdmission, CancellationToken ct)
    {
        var rates = new List<PsychiatricPerDiemRate> { atAdmission.ToRate() };
        var lastDay = lines.Max(r => r.ServiceDate.Date.AddDays(Math.Max(0, (int)Math.Ceiling(r.Units) - 1)));
        var latest = await _psychRates!.FindAsync(tenantId, TexasState, facilityNpi, lastDay, ct).ConfigureAwait(false);
        if (latest is not null && latest.Id != atAdmission.Id)
            rates.Add(latest.ToRate());
        // Each rate runs until the next one starts.
        rates = rates.OrderBy(r => r.EffectiveFrom).ToList();
        for (var i = 0; i < rates.Count - 1; i++)
            rates[i] = rates[i] with { EffectiveTo = rates[i + 1].EffectiveFrom.AddDays(-1) };

        var claim = new PsychiatricStayClaim
        {
            FacilityNpi = facilityNpi,
            AgeAtAdmission = lines[0].PatientAgeYears,
            Lines = lines.Select(r => new PsychiatricStayLine
            {
                LineNumber = r.LineNumber,
                RevenueCode = r.RevenueCode ?? string.Empty,
                ServiceDate = DateOnly.FromDateTime(r.ServiceDate),
                Days = (int)Math.Round(r.Units, MidpointRounding.AwayFromZero),
                ChargeAmount = r.BilledAmount,
            }).ToList(),
        };

        var price = new PsychiatricPerDiemPricer(_psychPolicy).Price(claim, rates);
        var steps = new List<string>
        {
            $"Psychiatric hospital {facilityNpi} ({atAdmission.HospitalName}, {atAdmission.FacilityClass}) is paid its per diem, not a DRG. "
            + "IMD days on the member's other claims are not yet counted toward the monthly limit.",
        };
        steps.AddRange(price.Steps);
        if (!price.Priced)
            return Unresolved(lines, $"Texas psychiatric hospital per diem: {price.UnpricedReason}", steps, FeeScheduleType.PerDiem);

        var scheduleName = string.Create(CultureInfo.InvariantCulture,
            $"Texas Medicaid psychiatric hospital per diem (effective {Day(atAdmission.EffectiveDate)})");

        _logger.LogInformation(
            "Priced a Texas psychiatric hospital stay by per diem: {Allowed} for {LineCount} line(s)",
            price.TotalAllowed, lines.Count);

        var byLine = price.Lines.ToDictionary(l => l.LineNumber);
        return new PricingResultSet
        {
            LineResults = lines.Select(r =>
            {
                var priced = byLine[r.LineNumber];
                return new PricingResult
                {
                    LineNumber = r.LineNumber,
                    ProcedureCode = r.ProcedureCode,
                    AllowedAmount = priced.Allowed,
                    BaseAmount = price.TotalAllowed,
                    BilledAmount = r.BilledAmount,
                    FeeScheduleType = FeeScheduleType.PerDiem,
                    RateSource = RateSource.Medicaid,
                    NetworkStatus = NetworkStatus.Unknown,
                    FeeScheduleId = atAdmission.Id,
                    FeeScheduleName = scheduleName,
                    // Cost sharing applies once to the stay (inpatient per diem).
                    IsPerStayRate = true,
                    CalculationSteps = steps,
                };
            }).ToList(),
        };
    }

    /// <summary>
    /// Spreads the stay's allowed amount across the lines by billed charges:
    /// each line's share truncated to the cent, the remainder on the last
    /// line. Texas pays the DRG amount whatever the charges, so there is no
    /// cap at billed.
    /// </summary>
    private static PricingResultSet Allocate(
        IReadOnlyList<PricingRequest> lines, MedicaidDrgPrice price, string scheduleId, string scheduleName)
    {
        var totalBilled = lines.Sum(r => r.BilledAmount);
        var proportions = lines
            .Select(r => totalBilled > 0m ? r.BilledAmount / totalBilled : 1m / lines.Count)
            .ToList();
        // Multiply before dividing, so an even split stays exact (1/3 as a
        // decimal would truncate $2,400.00 to $2,399.99).
        var shares = lines
            .Select(r => totalBilled > 0m
                ? Math.Floor(price.AllowedAmount * r.BilledAmount * 100m / totalBilled) / 100m
                : Math.Floor(price.AllowedAmount * 100m / lines.Count) / 100m)
            .ToList();
        shares[^1] += price.AllowedAmount - shares.Sum();

        var results = new List<PricingResult>(lines.Count);
        for (var n = 0; n < lines.Count; n++)
        {
            var line = lines[n];
            var adjustments = new List<RateAdjustment>();
            if (lines.Count > 1)
            {
                adjustments.Add(new RateAdjustment
                {
                    Modifier = string.Empty,
                    Description = $"Medicaid DRG payment {price.AllowedAmount:0.00} for the stay allocated by billed charges ({proportions[n]:P2} to line {line.LineNumber})",
                    AdjustmentFactor = Math.Round(proportions[n], 6),
                    AdjustmentAmount = shares[n] - price.AllowedAmount,
                });
            }

            results.Add(new PricingResult
            {
                LineNumber = line.LineNumber,
                ProcedureCode = line.ProcedureCode,
                AllowedAmount = shares[n],
                BaseAmount = price.AllowedAmount,
                BilledAmount = line.BilledAmount,
                FeeScheduleType = FeeScheduleType.Drg,
                RateSource = RateSource.Medicaid,
                NetworkStatus = NetworkStatus.Unknown,
                FeeScheduleId = scheduleId,
                FeeScheduleName = scheduleName,
                Adjustments = adjustments,
                IsPerStayRate = true,
                CalculationSteps = price.Steps,
            });
        }

        return new PricingResultSet { LineResults = results };
    }

    private static PricingResultSet Unresolved(
        IReadOnlyList<PricingRequest> lines, string reason, IReadOnlyList<string>? steps = null,
        FeeScheduleType type = FeeScheduleType.Drg) => new()
        {
            LineResults = lines.Select(r => new PricingResult
            {
                LineNumber = r.LineNumber,
                ProcedureCode = r.ProcedureCode,
                AllowedAmount = 0m,
                BilledAmount = r.BilledAmount,
                FeeScheduleType = type,
                RateSource = RateSource.Unresolved,
                NetworkStatus = NetworkStatus.Unknown,
                UnresolvedReason = reason,
                CalculationSteps = steps ?? Array.Empty<string>(),
            }).ToList(),
        };

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<drg>\d{1,3})-(?<soi>[1-4])$")]
    private static partial Regex DrgSoiKey();
}
