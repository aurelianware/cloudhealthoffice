namespace CloudHealthOffice.GrouperEngine.Ipps;

/// <summary>
/// What CMS's provider-specific file says about one hospital, as a payment
/// calculation needs it.
///
/// <para>
/// <b>Every one of these is an input, not a computation.</b> That is the
/// division of labour CMS itself draws, and it is worth stating because two of
/// them look computable and are not. The disproportionate patient percentage of
/// 42 CFR 412.106(b) is built from Medicare/SSI and Medicaid patient days over
/// a cost reporting period, and the resident-to-bed ratio of 412.105(a) from
/// full-time-equivalent residents over average available beds with several
/// categories of bed day excluded. Both are settled on the cost report, under
/// caps and category rules and with the hospital's appeal rights attached.
/// Neither can be derived from a claim, and a claim-time engine that tried
/// would be inventing the hospital's payment rather than applying it.
/// </para>
///
/// <para>
/// So this record is deliberately dull: it is the provider-specific file, and
/// loading it is a reference-data obligation rather than a calculation.
/// </para>
/// </summary>
/// <param name="CcnNumber">
/// The CMS certification number (the Medicare provider number), which is what
/// the provider-specific file is keyed by.
/// </param>
/// <param name="WageIndex">
/// The area wage index for the hospital's labor market area, after any
/// reclassification, floor or frontier-state adjustment it is entitled to
/// (42 CFR 412.64(h), 412.64(m)(2)). The reclassification is already in the
/// number: a calculator must not try to reapply it.
/// </param>
/// <param name="CostOfLivingAdjustment">
/// The non-labor cost-of-living adjustment, 1.0 outside Alaska and Hawaii
/// (412.64(m)(1)(ii)).
/// </param>
/// <param name="OperatingCostToChargeRatio">
/// The operating cost-to-charge ratio used to estimate a case's cost from its
/// charges for the outlier test (412.84(g), (i)(2)). From the most recent
/// settled or tentatively settled cost report, or the statewide average where
/// 412.84(i)(3) requires it — a new hospital, a ratio more than three standard
/// deviations above the national geometric mean, or no accurate data.
/// </param>
/// <param name="CapitalCostToChargeRatio">
/// The capital cost-to-charge ratio. Separate from the operating one because
/// 412.84(g) keeps them separate, and the two outlier tests are separate with
/// them.
/// </param>
/// <param name="ResidentToBedRatio">
/// Full-time-equivalent residents over available beds, after the caps of
/// 412.105(a)(1)(i) and (f)(1)(iv). Zero at a hospital with no teaching
/// programme, which is most of them.
/// </param>
/// <param name="OperatingDshAdjustment">
/// The hospital's operating disproportionate share adjustment factor from
/// 412.106(d), as a fraction and before the 75 percent reduction of
/// 412.106(f). Which of 412.106(d)'s formulas produced it, and which of its
/// 12 percent caps applied, depend on the hospital's category, bed count and
/// rural status — all settled off-claim, which is why the factor arrives here
/// rather than the inputs to it.
/// </param>
/// <param name="UncompensatedCarePerDischarge">
/// The uncompensated care payment the hospital is paid per discharge under
/// 412.106(g). An amount, not a percentage: CMS divides the hospital's annual
/// uncompensated care allocation by its estimated discharges, so nothing about
/// it scales with the case. Zero for a hospital that gets none.
/// </param>
/// <param name="CapitalGeographicAdjustmentFactor">
/// The geographic adjustment factor applied to the federal capital rate.
/// </param>
/// <param name="CapitalIndirectMedicalEducationFactor">
/// The capital indirect medical education adjustment. A separate factor from
/// the operating one and not derivable from it.
/// </param>
/// <param name="CapitalDisproportionateShareFactor">
/// The capital disproportionate share adjustment, likewise separate.
/// </param>
/// <param name="QualityDataSubmitted">
/// Whether the hospital met the inpatient quality reporting requirement for
/// this year. A hospital that did not is paid a reduced market basket update,
/// which arrives as a smaller standardized amount and not as a percentage
/// taken off the payment, so it selects a different row of
/// <see cref="IppsOperatingRateTable"/>.
///
/// <para>
/// CMS's provider file holds this as an indicator that is <c>"1"</c> or
/// something else, and its pricer treats <i>anything</i> but <c>"1"</c> as not
/// met — a blank included. A loader that defaults a missing indicator to true
/// is therefore not being lenient; it is overpaying.
/// </para>
/// </param>
/// <param name="ElectronicRecordsReduction">
/// Whether the hospital is subject to the reduction for not being a meaningful
/// electronic health record user. Independent of the quality requirement: a
/// hospital can carry both reductions, and CMS publishes a standardized amount
/// for each of the four combinations.
/// </param>
/// <param name="ReadmissionsAdjustmentFactor">
/// The hospital's readmissions payment adjustment factor (42 CFR 412.154),
/// or null where the hospital is not in the programme — which is a different
/// thing from a factor of 1.0 and is how CMS's provider file distinguishes
/// them.
///
/// <para>
/// CMS enforces a range of 0.97 to 1.0 and refuses the claim outside it, so
/// the factor is always a reduction. A value outside that range is a bad
/// provider file rather than an unusual hospital.
/// </para>
/// </param>
/// <param name="ValueBasedPurchasingAdjustmentFactor">
/// The hospital's value-based purchasing adjustment factor (412.160 and
/// following), or null where it is not a participant. Unlike the readmissions
/// factor this one goes both ways: CMS's range is 0.98 to 2.0, so a hospital
/// can be paid more for it.
/// </param>
/// <param name="LowVolumeTemporaryRelief">
/// Whether the hospital has the low-volume adjustment approved for this year.
/// A gate in its own right: CMS pays the adjustment only when this is set
/// <i>and</i> the factor is in range, and a factor alone is not enough.
/// </param>
/// <param name="LowVolumeAdjustmentFactor">
/// The low-volume adjustment factor (412.101), zero at the great majority of
/// hospitals. CMS's range is above 0 and at most 0.25, and a factor above 0.25
/// pays <b>nothing</b> rather than being capped — a cap would quietly pay a
/// hospital whose provider file is wrong.
/// </param>
/// <param name="HospitalAcquiredConditionReduction">
/// Whether the hospital is in the worst-performing quartile for
/// hospital-acquired conditions and so subject to the one percent penalty
/// (412.170 and 412.172). Reaches further than it looks: the penalty is taken
/// on almost the whole payment, the cost outlier and the uncompensated care
/// payment included.
/// </param>
public sealed record IppsProvider(
    string CcnNumber,
    decimal WageIndex,
    decimal CostOfLivingAdjustment,
    decimal OperatingCostToChargeRatio,
    decimal CapitalCostToChargeRatio,
    decimal ResidentToBedRatio,
    decimal OperatingDshAdjustment,
    decimal UncompensatedCarePerDischarge,
    decimal CapitalGeographicAdjustmentFactor,
    decimal CapitalIndirectMedicalEducationFactor,
    decimal CapitalDisproportionateShareFactor,
    bool QualityDataSubmitted,
    bool ElectronicRecordsReduction,
    decimal? ReadmissionsAdjustmentFactor,
    decimal? ValueBasedPurchasingAdjustmentFactor,
    bool LowVolumeTemporaryRelief,
    decimal LowVolumeAdjustmentFactor,
    bool HospitalAcquiredConditionReduction)
{
    /// <summary>
    /// A hospital with no teaching programme, no disproportionate share
    /// adjustment, the national average geography, neither payment reduction
    /// and none of the quality programmes: the shape a test uses when it is
    /// about something else, and a reasonable starting point for a deployment
    /// that has one wage index and nothing further.
    /// </summary>
    public static IppsProvider Plain(string ccnNumber, decimal wageIndex) => new(
        ccnNumber,
        wageIndex,
        CostOfLivingAdjustment: 1m,
        OperatingCostToChargeRatio: 0m,
        CapitalCostToChargeRatio: 0m,
        ResidentToBedRatio: 0m,
        OperatingDshAdjustment: 0m,
        UncompensatedCarePerDischarge: 0m,
        CapitalGeographicAdjustmentFactor: 1m,
        CapitalIndirectMedicalEducationFactor: 0m,
        CapitalDisproportionateShareFactor: 0m,
        QualityDataSubmitted: true,
        ElectronicRecordsReduction: false,
        ReadmissionsAdjustmentFactor: null,
        ValueBasedPurchasingAdjustmentFactor: null,
        LowVolumeTemporaryRelief: false,
        LowVolumeAdjustmentFactor: 0m,
        HospitalAcquiredConditionReduction: false);
}

/// <summary>
/// Supplies the provider-specific file record for a hospital, as it stood for
/// a discharge date.
///
/// <para>
/// The date matters: a wage index and a cost-to-charge ratio both change, and
/// a claim reprocessed two years later has to be paid on what was in force
/// when the patient was discharged, not on what is in force now.
/// </para>
/// </summary>
public interface IIppsProviderProvider
{
    /// <summary>
    /// The hospital's record for that discharge date, or null when the
    /// deployment holds none.
    /// </summary>
    Task<IppsProvider?> GetAsync(
        string ccnNumber, DateOnly dischargeDate, CancellationToken cancellationToken = default);
}
