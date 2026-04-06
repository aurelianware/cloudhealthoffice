namespace CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

/// <summary>
/// CDT dental procedure codes organized by category.
/// </summary>
internal static class DentalCodes
{
    /// <summary>Preventive dental codes (D0100-D1999).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Preventive =
    {
        ("D0120", "Periodic oral evaluation", 55m),
        ("D0140", "Limited oral evaluation — problem focused", 75m),
        ("D0150", "Comprehensive oral evaluation — new or established patient", 95m),
        ("D0210", "Intraoral — complete series of radiographic images", 135m),
        ("D0220", "Intraoral — periapical first radiographic image", 35m),
        ("D0274", "Bitewings — four radiographic images", 65m),
        ("D0330", "Panoramic radiographic image", 120m),
        ("D1110", "Prophylaxis — adult", 95m),
        ("D1120", "Prophylaxis — child", 65m),
        ("D1206", "Topical application of fluoride varnish", 40m),
        ("D1351", "Sealant — per tooth", 50m),
        ("D1510", "Space maintainer — fixed, unilateral", 285m)
    };

    /// <summary>Restorative dental codes (D2000-D2999).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Restorative =
    {
        ("D2140", "Amalgam — one surface, primary or permanent", 165m),
        ("D2150", "Amalgam — two surfaces, primary or permanent", 195m),
        ("D2160", "Amalgam — three surfaces, primary or permanent", 235m),
        ("D2330", "Resin-based composite — one surface, anterior", 175m),
        ("D2331", "Resin-based composite — two surfaces, anterior", 215m),
        ("D2332", "Resin-based composite — three surfaces, anterior", 255m),
        ("D2391", "Resin-based composite — one surface, posterior", 195m),
        ("D2392", "Resin-based composite — two surfaces, posterior", 245m),
        ("D2740", "Crown — porcelain/ceramic substrate", 1150m),
        ("D2750", "Crown — porcelain fused to high noble metal", 1250m),
        ("D2950", "Core buildup, including any pins when required", 295m)
    };

    /// <summary>Endodontic dental codes (D3000-D3999).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Endodontics =
    {
        ("D3110", "Pulp cap — direct", 125m),
        ("D3220", "Therapeutic pulpotomy", 195m),
        ("D3310", "Endodontic therapy, anterior tooth", 750m),
        ("D3320", "Endodontic therapy, premolar tooth", 895m),
        ("D3330", "Endodontic therapy, molar tooth", 1095m),
        ("D3346", "Retreatment, anterior tooth", 895m),
        ("D3410", "Apicoectomy — anterior", 650m)
    };

    /// <summary>Periodontic dental codes (D4000-D4999).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Periodontics =
    {
        ("D4210", "Gingivectomy — per quadrant", 395m),
        ("D4240", "Gingival flap procedure — per quadrant", 595m),
        ("D4341", "Periodontal scaling and root planing — per quadrant", 265m),
        ("D4342", "Periodontal scaling and root planing — one to three teeth", 175m),
        ("D4355", "Full mouth debridement", 195m),
        ("D4910", "Periodontal maintenance", 155m),
        ("D4381", "Localized delivery of antimicrobial agents — per tooth", 85m)
    };

    /// <summary>Orthodontic dental codes (D8000-D8999).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] Orthodontics =
    {
        ("D8010", "Limited orthodontic treatment — primary dentition", 2500m),
        ("D8020", "Limited orthodontic treatment — transitional dentition", 3200m),
        ("D8080", "Comprehensive orthodontic treatment — adolescent", 5500m),
        ("D8090", "Comprehensive orthodontic treatment — adult", 6200m),
        ("D8210", "Removable appliance therapy", 1800m),
        ("D8670", "Periodic orthodontic treatment visit", 195m),
        ("D8680", "Orthodontic retention", 450m)
    };

    /// <summary>Oral surgery dental codes (D7000-D7999).</summary>
    internal static readonly (string Code, string Description, decimal BaseCharge)[] OralSurgery =
    {
        ("D7111", "Extraction, coronal remnants — primary tooth", 125m),
        ("D7140", "Extraction, erupted tooth or exposed root", 195m),
        ("D7210", "Extraction — surgical, erupted tooth", 325m),
        ("D7220", "Removal of impacted tooth — soft tissue", 395m),
        ("D7230", "Removal of impacted tooth — partially bony", 495m),
        ("D7240", "Removal of impacted tooth — completely bony", 595m),
        ("D7310", "Alveoloplasty in conjunction with extractions — per quadrant", 295m),
        ("D7510", "Incision and drainage of abscess — intraoral soft tissue", 395m)
    };
}
