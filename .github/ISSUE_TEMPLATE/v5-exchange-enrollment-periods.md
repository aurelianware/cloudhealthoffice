---
name: 'ACA Exchange - Enrollment Period Rules & Qualifying Life Events'
about: Implement Open Enrollment Period (OEP) and Special Enrollment Period (SEP) enforcement
title: '[v5.0] ACA Exchange - Enrollment Period Rules (OEP/SEP)'
labels: 'feature, exchange, compliance, priority:high'
assignees: ''
---

## 🎯 Objective

Implement Open Enrollment Period (OEP) and Special Enrollment Period (SEP) enforcement to ensure Exchange enrollments comply with CMS/state marketplace regulations. Prevent out-of-period enrollments, automate SEP eligibility validation, and support retroactive coverage for qualifying life events.

**Priority:** 🟡 **HIGH** (v5.0 - Q2 2026)  
**Effort:** 2-3 weeks (1-2 developers)  
**Regulatory:** ACA Section 1311(c)(6), 45 CFR §155.410-155.420  
**Depends On:** #375 (APTC/CSR Support), #376 (QHP Certification)

---

## 📋 Success Criteria

- [ ] OEP dates configured per plan year (state/federal variations)
- [ ] SEP eligibility validation for 40+ qualifying life events
- [ ] 60-day enrollment window enforcement (before/after QLE)
- [ ] Retroactive coverage support (marriage, birth, adoption)
- [ ] Enrollment period override for admin corrections
- [ ] 834 transaction generation with enrollment effective dates
- [ ] Member notifications for enrollment deadlines
- [ ] State-specific rule variations (CA, NY, CO extended OEP)

---

## 🏥 Business Context

### What are Enrollment Periods?

**Open Enrollment Period (OEP):**
- Annual enrollment window: **November 1 - January 15** (FFM 2026)
- Coverage starts: January 1 (if enrolled by Dec 15) or Feb 1/Mar 1
- State variations: CA extends to January 31, CO to January 15
- Cannot change or cancel plans outside OEP unless SEP-eligible

**Special Enrollment Period (SEP):**
- 60-day window triggered by Qualifying Life Events (QLEs)
- Marriage, birth, adoption, loss of coverage, relocation, etc.
- Members must provide documentation (marriage cert, hospital birth record)
- Some SEPs allow retroactive coverage (birth = retroactive to birth date)

**Why This Matters:**
- Prevents adverse selection (members enrolling only when sick)
- CMS audits marketplace transactions for enrollment period violations
- Incorrect effective dates cause APTC reconciliation errors
- 834 transactions rejected if effective date doesn't match QLE date

---

## 🔧 Implementation Steps

### Phase 1: Enrollment Period Configuration (Week 1)

**1.1 Define Enrollment Period Models**

File: `services/enrollment-service/Models/EnrollmentPeriod.cs`

```csharp
/// <summary>
/// Enrollment period configuration (OEP/SEP)
/// </summary>
public class EnrollmentPeriodConfig
{
    /// <summary>
    /// Plan year (e.g., 2026)
    /// </summary>
    public int PlanYear { get; set; }
    
    /// <summary>
    /// State code (or "FFM" for federal marketplace)
    /// </summary>
    [StringLength(2)]
    public string State { get; set; } = "FFM";
    
    /// <summary>
    /// OEP start date (typically Nov 1)
    /// </summary>
    public DateTime OEPStartDate { get; set; }
    
    /// <summary>
    /// OEP end date (FFM: Jan 15, CA: Jan 31)
    /// </summary>
    public DateTime OEPEndDate { get; set; }
    
    /// <summary>
    /// Coverage effective date if enrolled by this date
    /// (Dec 15 enrollment → Jan 1 coverage)
    /// </summary>
    public List<EnrollmentDeadline> CoverageEffectiveDates { get; set; } = new();
    
    /// <summary>
    /// Is this marketplace still accepting OEP enrollments?
    /// </summary>
    public bool IsOEPActive => DateTime.UtcNow >= OEPStartDate && DateTime.UtcNow <= OEPEndDate;
}

/// <summary>
/// Enrollment deadline → coverage effective date mapping
/// </summary>
public class EnrollmentDeadline
{
    /// <summary>
    /// Enroll by this date (e.g., Dec 15)
    /// </summary>
    public DateTime EnrollmentDeadline { get; set; }
    
    /// <summary>
    /// Coverage starts this date (e.g., Jan 1)
    /// </summary>
    public DateTime CoverageEffectiveDate { get; set; }
}

/// <summary>
/// Qualifying Life Event (QLE) types
/// </summary>
public enum QualifyingLifeEvent
{
    // Loss of Coverage
    [Description("Loss of minimum essential coverage")]
    LossOfCoverage = 1,
    
    [Description("Loss of employer-sponsored coverage")]
    LossOfEmployerCoverage = 2,
    
    [Description("COBRA exhaustion")]
    COBRAExhaustion = 3,
    
    [Description("Aging off parent's plan (26th birthday)")]
    AgingOffParentPlan = 4,
    
    // Household Changes
    [Description("Marriage")]
    Marriage = 10,
    
    [Description("Divorce or legal separation")]
    Divorce = 11,
    
    [Description("Birth of child")]
    Birth = 12,
    
    [Description("Adoption or placement for adoption")]
    Adoption = 13,
    
    [Description("Death of covered family member")]
    Death = 14,
    
    // Relocation
    [Description("Permanent move to new service area")]
    Relocation = 20,
    
    [Description("Move to USA from foreign country")]
    MoveToUSA = 21,
    
    [Description("Release from incarceration")]
    ReleaseFromIncarceration = 22,
    
    // Eligibility Changes
    [Description("Gained citizenship or lawful presence")]
    GainedCitizenship = 30,
    
    [Description("APTC/CSR eligibility change")]
    SubsidyEligibilityChange = 31,
    
    [Description("Medicaid/CHIP eligibility loss")]
    MedicaidCHIPLoss = 32,
    
    // Employer Plan Changes
    [Description("Open enrollment not offered annually")]
    NoAnnualOpenEnrollment = 40,
    
    [Description("Employer plan no longer affordable (>9.02% income)")]
    PlanNoLongerAffordable = 41,
    
    // Exceptional Circumstances
    [Description("Marketplace error or misconduct")]
    MarketplaceError = 50,
    
    [Description("Natural disaster")]
    NaturalDisaster = 51,
    
    [Description("Domestic violence or spousal abandonment")]
    DomesticViolence = 52,
    
    // Other
    [Description("Other exceptional circumstance (admin approval)")]
    OtherExceptional = 99
}

/// <summary>
/// SEP eligibility rules per QLE type
/// </summary>
public class SEPEligibilityRule
{
    public QualifyingLifeEvent QLE { get; set; }
    
    /// <summary>
    /// Can enroll this many days BEFORE the QLE date
    /// (e.g., marriage = 60 days before)
    /// </summary>
    public int DaysBeforeQLE { get; set; }
    
    /// <summary>
    /// Can enroll this many days AFTER the QLE date
    /// (typically 60 days)
    /// </summary>
    public int DaysAfterQLE { get; set; }
    
    /// <summary>
    /// Does this QLE allow retroactive coverage?
    /// Birth/adoption = Yes (retroactive to event date)
    /// Marriage = No (first of month after event)
    /// </summary>
    public bool AllowsRetroactiveCoverage { get; set; }
    
    /// <summary>
    /// How far back can coverage be retroactive?
    /// </summary>
    public int? MaxRetroactiveDays { get; set; }
    
    /// <summary>
    /// Is documentation required?
    /// </summary>
    public bool RequiresDocumentation { get; set; }
    
    /// <summary>
    /// Acceptable document types (marriage cert, birth cert, etc.)
    /// </summary>
    public List<string> AcceptableDocuments { get; set; } = new();
    
    /// <summary>
    /// Can change plans during this SEP?
    /// Some SEPs only allow new enrollments, not plan changes
    /// </summary>
    public bool AllowsPlanChange { get; set; }
}
```

**1.2 Pre-configure SEP Rules**

```csharp
public class SEPConfigurationService
{
    private static readonly Dictionary<QualifyingLifeEvent, SEPEligibilityRule> _sepRules = new()
    {
        // Loss of Coverage - 60 days after loss
        { QualifyingLifeEvent.LossOfCoverage, new SEPEligibilityRule
        {
            QLE = QualifyingLifeEvent.LossOfCoverage,
            DaysBeforeQLE = 60, // Can enroll up to 60 days before loss
            DaysAfterQLE = 60,
            AllowsRetroactiveCoverage = false,
            RequiresDocumentation = true,
            AcceptableDocuments = new() { "COBRA notice", "Termination letter", "Final paycheck stub" },
            AllowsPlanChange = true
        }},
        
        // Marriage - 60 days before or after
        { QualifyingLifeEvent.Marriage, new SEPEligibilityRule
        {
            QLE = QualifyingLifeEvent.Marriage,
            DaysBeforeQLE = 60,
            DaysAfterQLE = 60,
            AllowsRetroactiveCoverage = false, // Coverage starts first of month AFTER marriage
            RequiresDocumentation = true,
            AcceptableDocuments = new() { "Marriage certificate", "Marriage license" },
            AllowsPlanChange = true
        }},
        
        // Birth - Retroactive to birth date
        { QualifyingLifeEvent.Birth, new SEPEligibilityRule
        {
            QLE = QualifyingLifeEvent.Birth,
            DaysBeforeQLE = 0,
            DaysAfterQLE = 60,
            AllowsRetroactiveCoverage = true,
            MaxRetroactiveDays = 60,
            RequiresDocumentation = true,
            AcceptableDocuments = new() { "Birth certificate", "Hospital birth record", "Adoption decree" },
            AllowsPlanChange = true
        }},
        
        // Relocation - 60 days before or after
        { QualifyingLifeEvent.Relocation, new SEPEligibilityRule
        {
            QLE = QualifyingLifeEvent.Relocation,
            DaysBeforeQLE = 60,
            DaysAfterQLE = 60,
            AllowsRetroactiveCoverage = false,
            RequiresDocumentation = true,
            AcceptableDocuments = new() { "Lease agreement", "Utility bill", "Driver's license with new address" },
            AllowsPlanChange = true
        }},
        
        // Aging off parent plan (26th birthday) - 60 days before/after
        { QualifyingLifeEvent.AgingOffParentPlan, new SEPEligibilityRule
        {
            QLE = QualifyingLifeEvent.AgingOffParentPlan,
            DaysBeforeQLE = 60,
            DaysAfterQLE = 60,
            AllowsRetroactiveCoverage = false,
            RequiresDocumentation = false, // DOB verification sufficient
            AcceptableDocuments = new() { "Birth certificate", "Government ID" },
            AllowsPlanChange = false // New enrollment only
        }}
        
        // Add remaining QLEs...
    };
    
    public SEPEligibilityRule GetRuleForQLE(QualifyingLifeEvent qle)
    {
        return _sepRules.TryGetValue(qle, out var rule) 
            ? rule 
            : throw new InvalidOperationException($"No SEP rule configured for QLE: {qle}");
    }
}
```

---

### Phase 2: Enrollment Period Validation (Week 2)

**2.1 Enrollment Eligibility Validator**

File: `services/enrollment-service/Services/EnrollmentEligibilityValidator.cs`

```csharp
public interface IEnrollmentEligibilityValidator
{
    Task<EnrollmentEligibilityResult> ValidateEnrollment(EnrollmentRequest request);
    Task<SEPEligibilityResult> ValidateSEP(string memberId, QualifyingLifeEvent qle, DateTime qleDate);
}

public class EnrollmentEligibilityValidator : IEnrollmentEligibilityValidator
{
    public async Task<EnrollmentEligibilityResult> ValidateEnrollment(EnrollmentRequest request)
    {
        var planYear = request.CoverageStartDate.Year;
        var state = await GetMemberState(request.MemberId);
        
        // 1. Get enrollment period config for state/year
        var periodConfig = await _configService.GetEnrollmentPeriodConfig(planYear, state);
        
        // 2. Check if during OEP
        if (periodConfig.IsOEPActive)
        {
            return new EnrollmentEligibilityResult
            {
                IsEligible = true,
                Reason = "Open Enrollment Period active",
                EnrollmentType = EnrollmentType.OEP,
                CoverageEffectiveDate = CalculateOEPEffectiveDate(request.EnrollmentDate, periodConfig)
            };
        }
        
        // 3. Check for SEP eligibility
        if (request.QualifyingLifeEvent.HasValue)
        {
            var sepResult = await ValidateSEP(
                request.MemberId,
                request.QualifyingLifeEvent.Value,
                request.QualifyingLifeEventDate.Value);
            
            if (sepResult.IsEligible)
            {
                return new EnrollmentEligibilityResult
                {
                    IsEligible = true,
                    Reason = $"SEP: {request.QualifyingLifeEvent}",
                    EnrollmentType = EnrollmentType.SEP,
                    QLE = request.QualifyingLifeEvent,
                    QLEDate = request.QualifyingLifeEventDate.Value,
                    CoverageEffectiveDate = CalculateSEPEffectiveDate(
                        request.QualifyingLifeEvent.Value,
                        request.QualifyingLifeEventDate.Value,
                        request.EnrollmentDate),
                    DocumentationRequired = sepResult.DocumentationRequired,
                    SEPDeadline = sepResult.EnrollmentDeadline
                };
            }
        }
        
        // 4. Not eligible - outside enrollment periods
        return new EnrollmentEligibilityResult
        {
            IsEligible = false,
            Reason = "Enrollment outside Open Enrollment Period and no qualifying SEP",
            NextOEPStartDate = periodConfig.OEPStartDate
        };
    }
    
    public async Task<SEPEligibilityResult> ValidateSEP(
        string memberId,
        QualifyingLifeEvent qle,
        DateTime qleDate)
    {
        // Get SEP rule for this QLE type
        var rule = _sepConfig.GetRuleForQLE(qle);
        
        // Calculate enrollment window
        var earliestEnrollmentDate = qleDate.AddDays(-rule.DaysBeforeQLE);
        var latestEnrollmentDate = qleDate.AddDays(rule.DaysAfterQLE);
        var today = DateTime.UtcNow.Date;
        
        // Check if within enrollment window
        var isWithinWindow = today >= earliestEnrollmentDate && today <= latestEnrollmentDate;
        
        // Special validation for specific QLEs
        if (qle == QualifyingLifeEvent.Relocation)
        {
            // Must move to different service area
            var currentServiceArea = await GetMemberServiceArea(memberId);
            var newServiceArea = await GetServiceAreaForZip(request.NewZipCode);
            
            if (currentServiceArea == newServiceArea)
            {
                return new SEPEligibilityResult
                {
                    IsEligible = false,
                    Reason = "Relocation SEP requires move to different service area"
                };
            }
        }
        
        if (qle == QualifyingLifeEvent.PlanNoLongerAffordable)
        {
            // Employer plan must exceed 9.02% of household income (2026 threshold)
            var employerPremium = request.EmployerPremiumAmount;
            var householdIncome = await GetHouseholdIncome(memberId);
            var affordabilityThreshold = householdIncome * 0.0902m;
            
            if (employerPremium <= affordabilityThreshold)
            {
                return new SEPEligibilityResult
                {
                    IsEligible = false,
                    Reason = $"Employer plan is affordable ({employerPremium:C} ≤ 9.02% threshold {affordabilityThreshold:C})"
                };
            }
        }
        
        return new SEPEligibilityResult
        {
            IsEligible = isWithinWindow,
            Reason = isWithinWindow 
                ? $"Within {rule.DaysAfterQLE}-day SEP window" 
                : $"Outside SEP window (deadline: {latestEnrollmentDate:M/d/yyyy})",
            EnrollmentDeadline = latestEnrollmentDate,
            DocumentationRequired = rule.RequiresDocumentation,
            AcceptableDocuments = rule.AcceptableDocuments,
            AllowsRetroactiveCoverage = rule.AllowsRetroactiveCoverage,
            MaxRetroactiveDays = rule.MaxRetroactiveDays,
            AllowsPlanChange = rule.AllowsPlanChange
        };
    }
    
    /// <summary>
    /// Calculate coverage effective date for OEP enrollment
    /// </summary>
    private DateTime CalculateOEPEffectiveDate(DateTime enrollmentDate, EnrollmentPeriodConfig config)
    {
        // Find first deadline AFTER enrollment date
        var effectiveDate = config.CoverageEffectiveDates
            .Where(d => enrollmentDate <= d.EnrollmentDeadline)
            .OrderBy(d => d.EnrollmentDeadline)
            .FirstOrDefault()?.CoverageEffectiveDate;
        
        // Default: First of next month
        return effectiveDate ?? new DateTime(enrollmentDate.Year, enrollmentDate.Month, 1).AddMonths(1);
    }
    
    /// <summary>
    /// Calculate coverage effective date for SEP enrollment
    /// </summary>
    private DateTime CalculateSEPEffectiveDate(
        QualifyingLifeEvent qle,
        DateTime qleDate,
        DateTime enrollmentDate)
    {
        var rule = _sepConfig.GetRuleForQLE(qle);
        
        // Birth/adoption - Retroactive to event date
        if (rule.AllowsRetroactiveCoverage)
        {
            var requestedEffectiveDate = qleDate;
            
            // Cannot be more than max retroactive days
            if (rule.MaxRetroactiveDays.HasValue)
            {
                var earliestEffective = DateTime.UtcNow.Date.AddDays(-rule.MaxRetroactiveDays.Value);
                requestedEffectiveDate = qleDate > earliestEffective ? qleDate : earliestEffective;
            }
            
            return requestedEffectiveDate;
        }
        
        // Marriage, loss of coverage - First of next month after enrollment
        return new DateTime(enrollmentDate.Year, enrollmentDate.Month, 1).AddMonths(1);
    }
}

public class EnrollmentEligibilityResult
{
    public bool IsEligible { get; set; }
    public string Reason { get; set; } = string.Empty;
    public EnrollmentType EnrollmentType { get; set; }
    public QualifyingLifeEvent? QLE { get; set; }
    public DateTime? QLEDate { get; set; }
    public DateTime CoverageEffectiveDate { get; set; }
    public bool DocumentationRequired { get; set; }
    public DateTime? SEPDeadline { get; set; }
    public DateTime? NextOEPStartDate { get; set; }
}

public enum EnrollmentType
{
    OEP,        // Open Enrollment Period
    SEP,        // Special Enrollment Period
    NewHire,    // Employer new hire (not Exchange)
    AdminOverride  // Manual override
}
```

---

### Phase 3: Coverage Effective Date Logic (Week 2-3)

**3.1 Effective Date Calculator**

```csharp
/// <summary>
/// Calculate coverage effective date with 834 transaction support
/// </summary>
public class CoverageEffectiveDateService
{
    /// <summary>
    /// Generate 834 enrollment transaction with correct effective date
    /// </summary>
    public async Task<string> GenerateEnrollment834(EnrollmentRequest request)
    {
        var eligibility = await _validator.ValidateEnrollment(request);
        
        if (!eligibility.IsEligible)
        {
            throw new InvalidOperationException(
                $"Enrollment not allowed: {eligibility.Reason}. " +
                $"Next OEP: {eligibility.NextOEPStartDate:M/d/yyyy}");
        }
        
        var effectiveDate = eligibility.CoverageEffectiveDate;
        
        // Build 834 transaction
        var transaction = new X12_834_Transaction
        {
            // ... standard 834 fields ...
            
            // DTP*348 - Coverage effective date
            EffectiveDate = effectiveDate,
            
            // REF*23 - Qualifying event code (if SEP)
            QualifyingEventCode = eligibility.QLE.HasValue 
                ? MapQLETo834Code(eligibility.QLE.Value) 
                : null,
            
            // REF*EI - QLE event date
            QualifyingEventDate = eligibility.QLEDate
        };
        
        return _x12Generator.Generate834(transaction);
    }
    
    private string MapQLETo834Code(QualifyingLifeEvent qle)
    {
        // Map internal QLE enum to 834 qualifying event codes
        return qle switch
        {
            QualifyingLifeEvent.Marriage => "32", // Marriage
            QualifyingLifeEvent.Birth => "02", // Birth
            QualifyingLifeEvent.Adoption => "03", // Adoption
            QualifyingLifeEvent.Death => "04", // Death
            QualifyingLifeEvent.Divorce => "33", // Divorce
            QualifyingLifeEvent.LossOfCoverage => "25", // Loss of eligibility
            QualifyingLifeEvent.Relocation => "28", // Change of location
            _ => "ZZ" // Other
        };
    }
}
```

---

## 📊 Database Schema Changes

### Enrollment Collection

```json
{
  "enrollmentId": "ENR_2026_001",
  "memberId": "MBR_001",
  "planId": "PLAN_2026_SILVER_001",
  "planYear": 2026,
  "enrollmentType": "SEP",
  "enrollmentDate": "2026-01-20T14:30:00Z",
  "coverageEffectiveDate": "2026-01-15T00:00:00Z",
  "qualifyingLifeEvent": "Birth",
  "qleDate": "2026-01-15T08:45:00Z",
  "qleDocumentation": {
    "documentType": "Hospital birth record",
    "uploadedDate": "2026-01-20T14:25:00Z",
    "verified": true,
    "verifiedBy": "ADMIN_001",
    "verifiedDate": "2026-01-20T15:00:00Z"
  },
  "sepDeadline": "2026-03-15T23:59:59Z",
  "isRetroactive": true,
  "retroactiveDays": 5
}
```

---

## 🧪 Testing Plan

### Unit Tests
```csharp
[Fact]
public void ValidateOEP_DuringOEP_IsEligible()
{
    // Arrange: Enrollment during OEP (Nov 1 - Jan 15)
    var request = new EnrollmentRequest
    {
        EnrollmentDate = new DateTime(2025, 12, 1),
        PlanYear = 2026
    };
    
    // Act
    var result = _validator.ValidateEnrollment(request).Result;
    
    // Assert
    Assert.True(result.IsEligible);
    Assert.Equal(EnrollmentType.OEP, result.EnrollmentType);
}

[Fact]
public void ValidateSEP_BirthWithin60Days_AllowsRetroactive()
{
    var result = _validator.ValidateSEP(
        "MBR_001",
        QualifyingLifeEvent.Birth,
        DateTime.UtcNow.AddDays(-10) // Birth 10 days ago
    ).Result;
    
    Assert.True(result.IsEligible);
    Assert.True(result.AllowsRetroactiveCoverage);
    Assert.Equal(60, result.MaxRetroactiveDays);
}

[Fact]
public void CalculateEffectiveDate_Marriage_FirstOfNextMonth()
{
    var effectiveDate = _effectiveDateService.CalculateSEPEffectiveDate(
        QualifyingLifeEvent.Marriage,
        new DateTime(2026, 1, 15), // QLE date
        new DateTime(2026, 1, 20)  // Enrollment date
    );
    
    Assert.Equal(new DateTime(2026, 2, 1), effectiveDate);
}
```

---

## 📚 Documentation Updates

### New Documents
- [ ] `docs/ENROLLMENT-PERIODS.md` - OEP/SEP rules reference
- [ ] `docs/QUALIFYING-LIFE-EVENTS.md` - QLE types and documentation requirements
- [ ] `docs/COVERAGE-EFFECTIVE-DATES.md` - Effective date calculation logic

### Update Existing Docs
- [ ] Update `FEATURES.md` - Add enrollment period enforcement
- [ ] Update `README.md` - Add SEP validation capabilities

---

## 💰 Financial Impact

### Risk Mitigation
- **CMS Audit Penalties**: $1M-5M for systematic enrollment period violations
- **APTC Reconciliation Errors**: $500K-2M annual overpayments from incorrect effective dates
- **Member Lawsuits**: $100K-500K per case (denied coverage claims)

### Development Cost
- 2-3 weeks × 2 developers × $150/hr = $24K-36K
- **Total**: $24K-36K

### ROI
- Avoid audit penalties: Break-even in 1 year
- Enable Exchange market entry (prerequisite for state marketplace contracts)

---

## ✅ Acceptance Criteria

- [ ] OEP dates enforced (Nov 1 - Jan 15 for FFM)
- [ ] SEP validation for all 40+ qualifying life events
- [ ] 60-day enrollment windows enforced
- [ ] Retroactive coverage for birth/adoption
- [ ] 834 transactions include QLE codes and event dates
- [ ] Member notifications sent at SEP deadline (7 days before)
- [ ] Admin override capability for exceptional circumstances

---

## 🔗 Dependencies

### Prerequisite Issues
- #375 (APTC/CSR Support) - Subsidies require correct effective dates
- #376 (QHP Certification) - Can only enroll in certified QHPs

### Blocks
- Exchange marketplace integrations (cannot submit enrollments without period validation)
- Member self-service portal (must enforce enrollment windows)

---

## 📖 References

- [CMS Special Enrollment Periods](https://www.cms.gov/CCIIO/Programs-and-Initiatives/Health-Insurance-Marketplaces/Special-Enrollment-Periods)
- [45 CFR §155.410 - Initial and Annual Open Enrollment Periods](https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-B/part-155/subpart-D/section-155.410)
- [45 CFR §155.420 - Special Enrollment Periods](https://www.ecfr.gov/current/title-45/subtitle-A/subchapter-B/part-155/subpart-D/section-155.420)
- [X12 834 Qualifying Event Codes](https://x12.org/codes/qualifying-event-codes)

---

**Labels:** `feature`, `exchange`, `compliance`, `priority:high`, `v5.0`  
**Milestone:** v5.0 - Exchange Market Expansion (Q2 2026)
