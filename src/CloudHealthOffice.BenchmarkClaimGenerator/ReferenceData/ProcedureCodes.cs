namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// Common CPT/HCPCS procedure codes organized by service category.
/// Base charges represent typical billed amounts for benchmarking.
/// </summary>
internal static class ProcedureCodes
{
    /// <summary>Evaluation and Management (E/M) office visit codes.</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] OfficeVisits =
    {
        ("99211", "Office visit, established patient, minimal", 45m),
        ("99212", "Office visit, established patient, straightforward", 95m),
        ("99213", "Office visit, established patient, low complexity", 150m),
        ("99214", "Office visit, established patient, moderate complexity", 225m),
        ("99215", "Office visit, established patient, high complexity", 325m),
        ("99201", "Office visit, new patient, straightforward", 110m),
        ("99202", "Office visit, new patient, straightforward", 135m),
        ("99203", "Office visit, new patient, low complexity", 195m),
        ("99204", "Office visit, new patient, moderate complexity", 295m),
        ("99205", "Office visit, new patient, high complexity", 395m)
    };

    /// <summary>Surgical procedure codes (for multi-line and global surgery scenarios).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Surgical =
    {
        ("27447", "Total knee arthroplasty", 8500m),
        ("27130", "Total hip arthroplasty", 9200m),
        ("47562", "Laparoscopic cholecystectomy", 4500m),
        ("49505", "Inguinal hernia repair, initial", 3200m),
        ("29881", "Arthroscopy, knee, surgical; with meniscectomy", 3800m),
        ("64721", "Carpal tunnel release", 2800m),
        ("66984", "Extracapsular cataract removal with IOL", 4100m),
        ("23412", "Rotator cuff repair", 6500m),
        ("44970", "Laparoscopic appendectomy", 4800m),
        ("50590", "Lithotripsy, extracorporeal shock wave", 5200m),
        ("28296", "Bunionectomy with osteotomy", 4200m),
        ("15002", "Surgical preparation of wound bed, first 100 sq cm", 1800m)
    };

    /// <summary>Telemedicine-compatible procedure codes.</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Telemedicine =
    {
        ("99213", "Office visit, established patient, low complexity (telehealth)", 150m),
        ("99214", "Office visit, established patient, moderate complexity (telehealth)", 225m),
        ("99215", "Office visit, established patient, high complexity (telehealth)", 325m),
        ("90834", "Psychotherapy, 45 minutes (telehealth)", 165m),
        ("90837", "Psychotherapy, 60 minutes (telehealth)", 210m),
        ("99441", "Telephone E/M, 5-10 minutes", 55m),
        ("99442", "Telephone E/M, 11-20 minutes", 95m),
        ("99443", "Telephone E/M, 21-30 minutes", 135m)
    };

    /// <summary>Lab and pathology codes (80000-89999 range).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] LabPathology =
    {
        ("80053", "Comprehensive metabolic panel", 75m),
        ("80061", "Lipid panel", 65m),
        ("85025", "Complete blood count with differential", 45m),
        ("81001", "Urinalysis with microscopy", 25m),
        ("83036", "Hemoglobin A1c", 55m),
        ("84443", "Thyroid stimulating hormone (TSH)", 70m),
        ("87880", "Strep test, rapid", 35m),
        ("87804", "Influenza virus rapid test", 40m),
        ("86900", "Blood typing, ABO", 30m),
        ("88305", "Surgical pathology, gross and micro", 195m),
        ("80048", "Basic metabolic panel", 55m),
        ("82947", "Glucose, quantitative", 20m)
    };

    /// <summary>Bilateral surgery procedure codes (modifier 50 applicable).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Bilateral =
    {
        ("27447", "Total knee arthroplasty (bilateral)", 8500m),
        ("66984", "Cataract removal with IOL (bilateral)", 4100m),
        ("69436", "Tympanostomy (bilateral)", 1800m),
        ("64721", "Carpal tunnel release (bilateral)", 2800m),
        ("29881", "Arthroscopy, knee, meniscectomy (bilateral)", 3800m)
    };

    /// <summary>Procedures requiring assistant surgeon (modifier 80/82).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] AssistantSurgeon =
    {
        ("27447", "Total knee arthroplasty", 8500m),
        ("27130", "Total hip arthroplasty", 9200m),
        ("22612", "Lumbar spinal fusion", 12500m),
        ("33533", "Coronary artery bypass, single graft", 18000m),
        ("35301", "Carotid endarterectomy", 8800m)
    };

    /// <summary>Global surgery follow-up visit codes.</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] PostOpVisits =
    {
        ("99024", "Postoperative follow-up visit (included in global)", 0m),
        ("99213", "Office visit (if outside global period)", 150m),
        ("99214", "Office visit (if outside global period)", 225m)
    };

    /// <summary>Behavioral health procedure codes.</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] BehavioralHealth =
    {
        ("90791", "Psychiatric diagnostic evaluation", 280m),
        ("90834", "Psychotherapy, 45 minutes", 165m),
        ("90837", "Psychotherapy, 60 minutes", 210m),
        ("90847", "Family psychotherapy with patient present", 195m),
        ("90853", "Group psychotherapy", 85m),
        ("99213", "Office E/M, psychiatric follow-up", 150m),
        ("96127", "Brief emotional/behavioral assessment", 25m)
    };
}
