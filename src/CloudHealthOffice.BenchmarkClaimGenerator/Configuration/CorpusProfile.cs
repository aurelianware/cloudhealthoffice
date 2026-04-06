namespace CloudHealthOffice.BenchmarkClaimGenerator.Configuration;

/// <summary>
/// Defines the distribution profile for corpus generation, specifying how many claims
/// of each type and sub-type to produce.
/// </summary>
public class CorpusProfile
{
    /// <summary>Total number of claims to generate.</summary>
    public int TotalClaims { get; set; }

    /// <summary>Random seed for deterministic, reproducible generation.</summary>
    public int Seed { get; set; } = 42;

    /// <summary>Professional claim distribution (CMS-1500).</summary>
    public ProfessionalDistribution Professional { get; set; } = new();

    /// <summary>Institutional claim distribution (UB-04).</summary>
    public InstitutionalDistribution Institutional { get; set; } = new();

    /// <summary>Dental claim distribution (ADA).</summary>
    public DentalDistribution Dental { get; set; } = new();

    /// <summary>Edge case claim distribution.</summary>
    public EdgeCaseDistribution EdgeCases { get; set; } = new();
}

/// <summary>Professional claim sub-type distribution.</summary>
public class ProfessionalDistribution
{
    /// <summary>Total number of professional claims.</summary>
    public int Count { get; set; }

    /// <summary>Fraction of professional claims that are single-line office visits (E/M).</summary>
    public double OfficeVisitFraction { get; set; }

    /// <summary>Fraction that are multi-line procedures with modifier stacking.</summary>
    public double MultiLineProcedureFraction { get; set; }

    /// <summary>Fraction that are global surgery packages.</summary>
    public double GlobalSurgeryFraction { get; set; }

    /// <summary>Fraction that are bilateral procedures (modifier 50).</summary>
    public double BilateralFraction { get; set; }

    /// <summary>Fraction that are assistant surgeon claims (modifier 80/82).</summary>
    public double AssistantSurgeonFraction { get; set; }

    /// <summary>Fraction that are telemedicine (POS 02, modifier 95).</summary>
    public double TelemedicineFraction { get; set; }

    /// <summary>Fraction that are lab/pathology (CPT 80000-89999).</summary>
    public double LabPathologyFraction { get; set; }
}

/// <summary>Institutional claim sub-type distribution.</summary>
public class InstitutionalDistribution
{
    /// <summary>Total number of institutional claims.</summary>
    public int Count { get; set; }

    /// <summary>Fraction that are inpatient with DRG grouping.</summary>
    public double InpatientDrgFraction { get; set; }

    /// <summary>Fraction that are outpatient per diem.</summary>
    public double OutpatientPerDiemFraction { get; set; }

    /// <summary>Fraction that are emergency department.</summary>
    public double EmergencyFraction { get; set; }

    /// <summary>Fraction that are observation stays.</summary>
    public double ObservationFraction { get; set; }

    /// <summary>Fraction that are stop-loss/outlier scenarios.</summary>
    public double StopLossOutlierFraction { get; set; }

    /// <summary>Fraction that are skilled nursing facility.</summary>
    public double SkilledNursingFraction { get; set; }
}

/// <summary>Dental claim sub-type distribution.</summary>
public class DentalDistribution
{
    /// <summary>Total number of dental claims.</summary>
    public int Count { get; set; }

    /// <summary>Fraction that are preventive (D0100-D1999).</summary>
    public double PreventiveFraction { get; set; }

    /// <summary>Fraction that are restorative (D2000-D2999).</summary>
    public double RestorativeFraction { get; set; }

    /// <summary>Fraction that are endodontics (D3000-D3999).</summary>
    public double EndodonticsFraction { get; set; }

    /// <summary>Fraction that are periodontics (D4000-D4999).</summary>
    public double PeriodonticsFraction { get; set; }

    /// <summary>Fraction that are orthodontics (D8000-D8999).</summary>
    public double OrthodonticsFraction { get; set; }

    /// <summary>Fraction that are oral surgery (D7000-D7999).</summary>
    public double OralSurgeryFraction { get; set; }
}

/// <summary>Edge case claim distribution by scenario category.</summary>
public class EdgeCaseDistribution
{
    /// <summary>Total number of edge case claims.</summary>
    public int Count { get; set; }

    /// <summary>Number of COB (coordination of benefits) claims.</summary>
    public int CobCount { get; set; }

    /// <summary>Number of retro-eligibility claims.</summary>
    public int RetroEligibilityCount { get; set; }

    /// <summary>Number of newborn claims.</summary>
    public int NewbornCount { get; set; }

    /// <summary>Number of prior authorization claims.</summary>
    public int PriorAuthCount { get; set; }

    /// <summary>Number of subrogation claims.</summary>
    public int SubrogationCount { get; set; }

    /// <summary>Number of behavioral health claims.</summary>
    public int BehavioralHealthCount { get; set; }

    /// <summary>Number of Medicaid subprogram claims.</summary>
    public int MedicaidCount { get; set; }
}
