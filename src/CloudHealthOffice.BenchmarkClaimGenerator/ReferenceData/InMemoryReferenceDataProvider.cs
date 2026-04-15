using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// In-memory reference data provider using hardcoded code sets.
/// V1 implementation — will be replaced by TerminologyService integration in v2.
/// </summary>
public class InMemoryReferenceDataProvider : IReferenceDataProvider
{
    private static readonly string[] States =
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA",
        "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ",
        "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY"
    };

    private static readonly string[] FirstNames =
    {
        "James", "Mary", "Robert", "Patricia", "John", "Jennifer", "Michael", "Linda",
        "David", "Elizabeth", "William", "Barbara", "Richard", "Susan", "Joseph", "Jessica",
        "Thomas", "Sarah", "Charles", "Karen", "Christopher", "Lisa", "Daniel", "Nancy",
        "Matthew", "Betty", "Anthony", "Margaret", "Mark", "Sandra"
    };

    private static readonly string[] LastNames =
    {
        "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis",
        "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson",
        "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson",
        "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson"
    };

    private static readonly string[] Specialties =
    {
        ("207Q00000X"),  // Family Medicine
        ("207R00000X"),  // Internal Medicine
        ("208D00000X"),  // General Practice
        ("207V00000X"),  // Obstetrics & Gynecology
        ("208600000X"),  // Surgery
        ("2084N0400X"),  // Neurology
        ("207RC0000X"),  // Cardiovascular Disease
        ("2085R0202X"),  // Diagnostic Radiology
        ("207Y00000X"),  // Otolaryngology
        ("2083P0901X")   // Pediatrics
    };

    private static readonly string[] BenefitPlanIds =
    {
        "PLN-COMM-PPO-001", "PLN-COMM-HMO-001", "PLN-COMM-EPO-001",
        "PLN-COMM-HDHP-001", "PLN-MA-HMO-001", "PLN-MA-PPO-001",
        "PLN-MCAID-FFS-001", "PLN-MCAID-MCO-001",
        "PLN-DENTAL-PPO-001", "PLN-DENTAL-HMO-001"
    };

    private static readonly (string Code, string Description, decimal BaseRate)[] DrgCodes =
    {
        ("470", "Major hip and knee joint replacement", 22000m),
        ("871", "Septicemia or severe sepsis without MV >96 hours with MCC", 18500m),
        ("392", "Esophagitis, gastroenteritis without MCC", 8200m),
        ("690", "Kidney and urinary tract infections without MCC", 7800m),
        ("291", "Heart failure and shock with MCC", 14500m),
        ("194", "Simple pneumonia and pleurisy with CC", 9800m),
        ("683", "Renal failure with CC", 10200m),
        ("766", "Cesarean section without CC/MCC", 11500m),
        ("775", "Vaginal delivery without complicating diagnoses", 7200m),
        ("917", "Poisoning and toxic effects of drugs with MCC", 12800m),
        ("378", "GI hemorrhage with CC", 11000m),
        ("189", "Pulmonary edema and respiratory failure", 13200m),
        ("065", "Intracranial hemorrhage or cerebral infarction with CC", 15500m),
        ("480", "Hip and femur procedures except major joint without CC/MCC", 16800m),
        ("419", "Laparoscopic cholecystectomy without CC/MCC", 9500m)
    };

    /// <inheritdoc />
    public string GetDiagnosisCode(Random random, string context = "general")
    {
        var codes = context.ToLowerInvariant() switch
        {
            "surgical" => DiagnosisCodes.Surgical,
            "emergency" => DiagnosisCodes.Emergency,
            "behavioral" or "behavioralhealth" => DiagnosisCodes.BehavioralHealth,
            "dental" => DiagnosisCodes.Dental,
            "newborn" => DiagnosisCodes.Newborn,
            _ => DiagnosisCodes.General
        };
        return codes[random.Next(codes.Length)].Code;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSecondaryDiagnosisCodes(Random random, int count)
    {
        var result = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(DiagnosisCodes.General[random.Next(DiagnosisCodes.General.Length)].Code);
        }
        return result;
    }

    /// <inheritdoc />
    public (string Code, string Description, decimal BaseCharge) GetProcedureCode(Random random, string subType)
    {
        var codes = subType.ToLowerInvariant() switch
        {
            "officevisit" or "office" => ProcedureCodes.OfficeVisits,
            "surgical" or "surgery" => ProcedureCodes.Surgical,
            "telemedicine" or "telehealth" => ProcedureCodes.Telemedicine,
            "lab" or "labpathology" => ProcedureCodes.LabPathology,
            "bilateral" => ProcedureCodes.Bilateral,
            "assistantsurgeon" or "assistant" => ProcedureCodes.AssistantSurgeon,
            "behavioralhealth" or "behavioral" => ProcedureCodes.BehavioralHealth,
            _ => ProcedureCodes.OfficeVisits
        };
        return codes[random.Next(codes.Length)];
    }

    /// <inheritdoc />
    public (string Code, string Description, decimal BaseCharge) GetDentalCode(Random random, string category)
    {
        var codes = category.ToLowerInvariant() switch
        {
            "preventive" => DentalCodes.Preventive,
            "restorative" => DentalCodes.Restorative,
            "endodontics" => DentalCodes.Endodontics,
            "periodontics" => DentalCodes.Periodontics,
            "orthodontics" => DentalCodes.Orthodontics,
            "oralsurgery" => DentalCodes.OralSurgery,
            _ => DentalCodes.Preventive
        };
        return codes[random.Next(codes.Length)];
    }

    /// <inheritdoc />
    public (string Code, string Description) GetRevenueCode(Random random, string subType)
    {
        var codes = subType.ToLowerInvariant() switch
        {
            "inpatient" => RevenueCodeSets.Inpatient,
            "outpatient" => RevenueCodeSets.Outpatient,
            "emergency" => RevenueCodeSets.Emergency,
            "observation" => RevenueCodeSets.Observation,
            "skillednursing" or "snf" => RevenueCodeSets.SkilledNursing,
            _ => RevenueCodeSets.Outpatient
        };
        return codes[random.Next(codes.Length)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetModifiers(Random random, string scenario)
    {
        var sets = scenario.ToLowerInvariant() switch
        {
            "multiprocedure" or "multiline" => ModifierSets.MultiProcedure,
            "bilateral" => ModifierSets.Bilateral,
            "assistantsurgeon" or "assistant" => ModifierSets.AssistantSurgeon,
            "telemedicine" or "telehealth" => ModifierSets.Telemedicine,
            "globalsurgery" or "surgery" => ModifierSets.GlobalSurgery,
            _ => ModifierSets.MultiProcedure
        };
        return sets[random.Next(sets.Length)];
    }

    /// <inheritdoc />
    public (string Code, string Description, decimal BaseRate) GetDrgCode(Random random)
    {
        return DrgCodes[random.Next(DrgCodes.Length)];
    }

    /// <inheritdoc />
    public string GetPlaceOfServiceCode(Random random, string claimSubType)
    {
        return claimSubType.ToLowerInvariant() switch
        {
            "telemedicine" or "telehealth" => "02",
            "inpatient" => "21",
            "outpatient" => "22",
            "emergency" => "23",
            "observation" => "22",
            "skillednursing" or "snf" => "31",
            "lab" or "labpathology" => "81",
            "dental" => "11",
            _ => "11"  // Office
        };
    }

    /// <inheritdoc />
    public string GetBenefitPlanId(Random random)
    {
        return BenefitPlanIds[random.Next(BenefitPlanIds.Length)];
    }

    /// <inheritdoc />
    public SyntheticMember GenerateMember(Random random)
    {
        var state = States[random.Next(States.Length)];
        var dob = new DateTime(1940 + random.Next(70), 1 + random.Next(12), 1 + random.Next(28));
        var effectiveDate = new DateTime(2023, 1, 1).AddDays(random.Next(365));
        var seq = random.Next(1, 9_999_999);
        var relationship = random.Next(4) switch
        {
            0 => "Self",
            1 => "Spouse",
            2 => "Child",
            _ => "Self"
        };
        var relationshipCode = relationship switch
        {
            "Self" => "18",
            "Spouse" => "01",
            "Child" => "19",
            _ => "18"
        };

        return new SyntheticMember
        {
            MemberId = $"MBR-{seq:D7}",
            SubscriberId = $"SUB-{seq:D7}",
            FirstName = FirstNames[random.Next(FirstNames.Length)],
            LastName = LastNames[random.Next(LastNames.Length)],
            DateOfBirth = dob,
            Gender = random.Next(2) == 0 ? "M" : "F",
            Relationship = relationship,
            RelationshipCode = relationshipCode,
            IsSubscriber = relationship == "Self",
            CoverageEffectiveDate = effectiveDate,
            CoverageTermDate = null,
            PlanId = BenefitPlanIds[random.Next(BenefitPlanIds.Length)],
            EnrollmentStatus = "Active",
            MaintenanceTypeCode = "021",
            LineOfBusiness = "STAR",
            GroupNumber = $"MCC-GRP-{random.Next(1, 100):D3}",
            Address = $"{random.Next(100, 9999)} Main St",
            City = "Dallas",
            State = state,
            ZipCode = $"{random.Next(10000, 99999)}"
        };
    }

    /// <inheritdoc />
    public SyntheticProvider GenerateProvider(Random random, string specialty = "general")
    {
        var state = States[random.Next(States.Length)];
        var isParticipating = random.Next(100) < 85;
        var taxonomyCode = Specialties[random.Next(Specialties.Length)];

        return new SyntheticProvider
        {
            Npi = SyntheticProviderGenerator.GenerateLuhnNpi(random),
            TaxId = $"{random.Next(10, 99)}-{random.Next(1_000_000, 9_999_999)}",
            ProviderType = "Individual",
            FirstName = FirstNames[random.Next(FirstNames.Length)],
            LastName = LastNames[random.Next(LastNames.Length)],
            SpecialtyCode = taxonomyCode,
            TaxonomyCode = taxonomyCode,
            IsParticipating = isParticipating,
            NetworkStatus = isParticipating ? "InNetwork" : "OutOfNetwork",
            CredentialingStatus = isParticipating ? "Active" : "Expired",
            Address = $"{random.Next(100, 9999)} Medical Dr",
            City = "Dallas",
            State = state,
            ZipCode = $"{random.Next(10000, 99999)}",
            EffectiveDate = DateTime.Today.AddYears(-2),
            ContractType = "FeeForService",
            FeeScheduleId = isParticipating ? "FS-MEDICAID" : "FS-OON",
        };
    }
}
