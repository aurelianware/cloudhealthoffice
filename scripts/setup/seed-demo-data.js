// seed-demo-data.js
// MongoDB seed script that populates a comprehensive, production-grade
// operational dataset for a given tenant in the CloudHealthOffice database.
//
// WARNING: This script REPLACES any existing data for the specified tenant
// in ALL seeded collections. Back up first if you need to preserve data.
//
// Usage:
//   mongosh <connection-string> scripts/setup/seed-demo-data.js \
//     --eval 'var tenantId="my-tenant", groupNumber="GRP-001-2026"'
//
// Required --eval variables:
//   tenantId    — tenant identifier written to every document
//   groupNumber — primary group number for the main sponsor
//
// Optional --eval variables:
//   sponsor1 — name for large-group sponsor 1 (default: "Metro Employees Association")
//   sponsor2 — name for large-group sponsor 2 (default: "Lone Star Manufacturing Group")

// ═══════════════════════════════════════════════════════════════════════════
// VALIDATE PARAMETERS
// ═══════════════════════════════════════════════════════════════════════════

if (typeof tenantId === "undefined" || typeof groupNumber === "undefined") {
  print("ERROR: Missing required parameters.");
  print("");
  print("Usage:");
  print('  mongosh <connection-string> seed-demo-data.js \\');
  print('    --eval \'var tenantId="my-tenant", groupNumber="GRP-001-2026"\'');
  print("");
  print("Optional: sponsor1, sponsor2 (display names for the two large sponsor orgs)");
  quit(1);
}

const TENANT_ID = tenantId;
const GROUP_NUMBER = groupNumber;
const SPONSOR1_NAME = (typeof sponsor1 !== "undefined") ? sponsor1 : "Metro Employees Association";
const SPONSOR2_NAME = (typeof sponsor2 !== "undefined") ? sponsor2 : "Lone Star Manufacturing Group";

const choDb = db.getSiblingDB("cloudhealthoffice");
const now = new Date();
const TODAY = new Date(now.getFullYear(), now.getMonth(), now.getDate());

// ═══════════════════════════════════════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════════════════════════════════════

function makeId(prefix, n) {
  const pad = String(n).padStart(4, "0");
  return `${prefix}-${TENANT_ID.substring(0, 8)}-${pad}`;
}

// Seeded PRNG so data is deterministic across runs
let _seed = 42;
function seededRandom() {
  _seed = (_seed * 16807 + 0) % 2147483647;
  return (_seed - 1) / 2147483646;
}

function randInt(min, max) {
  return min + Math.floor(seededRandom() * (max - min + 1));
}

function pick(arr) { return arr[randInt(0, arr.length - 1)]; }

function pickN(arr, n) {
  const copy = arr.slice();
  const result = [];
  for (let i = 0; i < Math.min(n, copy.length); i++) {
    const idx = randInt(0, copy.length - 1);
    result.push(copy.splice(idx, 1)[0]);
  }
  return result;
}

function daysAgo(n) {
  const d = new Date(TODAY);
  d.setDate(d.getDate() - n);
  return d;
}

function daysFromNow(n) {
  const d = new Date(TODAY);
  d.setDate(d.getDate() + n);
  return d;
}

function dateOfBirth(age) {
  return new Date(now.getFullYear() - age, randInt(0, 11), randInt(1, 28));
}

function roundMoney(v) { return Math.round(v * 100) / 100; }

function npiCheckDigit(nineDigits) {
  const prefixed = "80840" + nineDigits;
  let sum = 0;
  for (let i = prefixed.length - 1; i >= 0; i--) {
    let d = parseInt(prefixed[i]);
    if ((prefixed.length - i) % 2 === 0) { d *= 2; if (d > 9) d -= 9; }
    sum += d;
  }
  return String((10 - (sum % 10)) % 10);
}

function makeNpi(idx) {
  const nine = String(1000000000 + idx).slice(1);
  return nine + npiCheckDigit(nine);
}

// ═══════════════════════════════════════════════════════════════════════════
// CLEAR EXISTING DATA
// ═══════════════════════════════════════════════════════════════════════════

const COLLECTIONS = [
  "Members", "Coverage", "Claims", "Providers", "BenefitPlans", "Sponsors",
  "Authorizations", "WorkQueueItems", "Payments", "Accumulators",
  "Appeals", "Correspondence", "EnrollmentFiles"
];

COLLECTIONS.forEach(function (col) {
  // Clear both PascalCase (C# driver) and camelCase (legacy seed) documents
  const r = choDb[col].deleteMany({ $or: [{ TenantId: TENANT_ID }, { tenantId: TENANT_ID }] });
  if (r.deletedCount > 0) print("  Cleared " + r.deletedCount + " from " + col);
});

// ═══════════════════════════════════════════════════════════════════════════
// 1. BENEFIT PLANS (5)
// ═══════════════════════════════════════════════════════════════════════════

function makePlanBenefits(copayPCP, copaySpec, coinsIn, coinsOut, erCopay, deductAppliesPCP) {
  return [
    { Id: "svc-pcp",     ServiceCategory: "PrimaryCare",    Description: "Primary care office visit",         CptCodes: ["99213","99214","99215"],   InNetworkCopay: copayPCP, OutNetworkCopay: copayPCP*2, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: deductAppliesPCP, PriorAuthRequired: false },
    { Id: "svc-spec",    ServiceCategory: "Specialist",     Description: "Specialist office visit",           CptCodes: ["99243","99244","99245"],   InNetworkCopay: copaySpec, OutNetworkCopay: copaySpec*2, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: false },
    { Id: "svc-er",      ServiceCategory: "Emergency",      Description: "Emergency room visit",              CptCodes: ["99283","99284","99285"],   InNetworkCopay: erCopay, OutNetworkCopay: erCopay, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsIn, DeductibleApplies: true, PriorAuthRequired: false },
    { Id: "svc-inpt",    ServiceCategory: "Inpatient",      Description: "Inpatient hospital stay",           CptCodes: ["99221","99222","99223"],   InNetworkCopay: 0, OutNetworkCopay: 0, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: true },
    { Id: "svc-outpt",   ServiceCategory: "OutpatientSurg", Description: "Outpatient surgery",                CptCodes: ["27447","29881","49505"],   InNetworkCopay: 0, OutNetworkCopay: 0, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: true },
    { Id: "svc-img",     ServiceCategory: "Imaging",        Description: "Advanced imaging (MRI/CT/PET)",     CptCodes: ["70553","71260","73721"],   InNetworkCopay: 0, OutNetworkCopay: 0, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: true },
    { Id: "svc-lab",     ServiceCategory: "Laboratory",     Description: "Laboratory / pathology services",   CptCodes: ["80053","85025","88305"],   InNetworkCopay: 0, OutNetworkCopay: 0, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: false },
    { Id: "svc-pt",      ServiceCategory: "PhysicalTherapy",Description: "Physical therapy",                  CptCodes: ["97110","97140","97530"],   InNetworkCopay: copayPCP, OutNetworkCopay: copayPCP*2, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: true },
    { Id: "svc-mh",      ServiceCategory: "MentalHealth",   Description: "Outpatient mental health",          CptCodes: ["90834","90837","90847"],   InNetworkCopay: copayPCP, OutNetworkCopay: copayPCP*2, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: false, PriorAuthRequired: false },
    { Id: "svc-prev",    ServiceCategory: "Preventive",     Description: "Preventive care / wellness visit",  CptCodes: ["99385","99395","99396"],   InNetworkCopay: 0, OutNetworkCopay: 0, InNetworkCoinsurance: 0, OutNetworkCoinsurance: 0, DeductibleApplies: false, PriorAuthRequired: false },
    { Id: "svc-rx-gen",  ServiceCategory: "PharmacyGeneric", Description: "Generic drugs (Tier 1)",           CptCodes: [],                          InNetworkCopay: 10, OutNetworkCopay: 20, InNetworkCoinsurance: 0, OutNetworkCoinsurance: 0, DeductibleApplies: false, PriorAuthRequired: false },
    { Id: "svc-dme",     ServiceCategory: "DME",            Description: "Durable medical equipment",         CptCodes: ["E0601","E0260","L3670"],   InNetworkCopay: 0, OutNetworkCopay: 0, InNetworkCoinsurance: coinsIn, OutNetworkCoinsurance: coinsOut, DeductibleApplies: true, PriorAuthRequired: true },
  ];
}

const benefitPlansData = [
  { idx: 1, name: "Gold PPO",     type: 1,    metal: 2,          lob: 1,            indDed: 1500, famDed: 3000, indOop: 6000, famOop: 12000, copayPCP: 25, copaySpec: 50, coinsIn: 20, coinsOut: 40, erCopay: 150, dedPCP: false },
  { idx: 2, name: "Silver PPO",   type: 1,    metal: 1,          lob: 1,            indDed: 1000, famDed: 2500, indOop: 5000, famOop: 10000, copayPCP: 40, copaySpec: 65, coinsIn: 30, coinsOut: 50, erCopay: 350, dedPCP: false },
  { idx: 3, name: "Bronze HDHP",  type: 4,    metal: 0,          lob: 1,            indDed: 3000, famDed: 6000, indOop: 7000, famOop: 14000, copayPCP: 0,  copaySpec: 0,  coinsIn: 20, coinsOut: 40, erCopay: 0,   dedPCP: true },
  { idx: 4, name: "Platinum HMO", type: 0,    metal: 3,          lob: 1,            indDed: 250,  famDed: 500,  indOop: 2000, famOop: 4000,  copayPCP: 20, copaySpec: 40, coinsIn: 10, coinsOut: 50, erCopay: 150, dedPCP: false },
  { idx: 5, name: "Medicaid MCO", type: 5,    metal: 0,          lob: 3,            indDed: 0,    famDed: 0,    indOop: 250,  famOop: 500,   copayPCP: 3,  copaySpec: 5,  coinsIn: 0,  coinsOut: 0,  erCopay: 8,   dedPCP: false },
];

const benefitPlans = benefitPlansData.map(function (p) {
  return {
    _id: makeId("plan", p.idx),
    TenantId: TENANT_ID,
    PlanId: makeId("plan", p.idx),
    PlanName: p.name,
    Payer: "CloudHealthOffice Demo Payer",
    EffectiveDate: new Date("2025-01-01"),
    TerminationDate: null,
    PlanType: p.type,
    MetalLevel: p.metal,
    LineOfBusiness: p.lob,
    CostSharing: {
      IndividualDeductible: p.indDed,
      FamilyDeductible: p.famDed,
      IndividualOutOfPocketMax: p.indOop,
      FamilyOutOfPocketMax: p.famOop,
      InNetworkDeductible: p.indDed,
      OutOfNetworkDeductible: p.indDed * 2,
      InNetworkOutOfPocketMax: p.indOop,
      OutOfNetworkOutOfPocketMax: p.indOop * 2
    },
    Benefits: makePlanBenefits(p.copayPCP, p.copaySpec, p.coinsIn, p.coinsOut, p.erCopay, p.dedPCP),
    NetworkTiers: ["Tier1", "Tier2"],
    IsActive: true,
    CreatedDate: now,
    ModifiedDate: now,
    CreatedBy: "seed-script"
  };
});
choDb.BenefitPlans.insertMany(benefitPlans);
print("✓ " + benefitPlans.length + " BenefitPlans");

// Plan cost-sharing lookup for adjudication
const planCostSharing = {};
benefitPlansData.forEach(function (p) {
  planCostSharing[makeId("plan", p.idx)] = p;
});

// ═══════════════════════════════════════════════════════════════════════════
// 2. SPONSORS (5)
// ═══════════════════════════════════════════════════════════════════════════

const sponsorsData = [
  { idx: 1, name: SPONSOR1_NAME,                    grp: GROUP_NUMBER,          type: "Employer", state: "TX", city: "Austin",      zip: "78701", tier: "Large (500+)", members: 0, premium: 487500, plans: [1,2,3] },
  { idx: 2, name: SPONSOR2_NAME,                    grp: GROUP_NUMBER + "-B",   type: "Employer", state: "TX", city: "Houston",     zip: "77002", tier: "Large (500+)", members: 0, premium: 325000, plans: [1,2] },
  { idx: 3, name: "Hill Country Educators Co-Op",   grp: GROUP_NUMBER + "-C",   type: "Association", state: "TX", city: "San Antonio", zip: "78205", tier: "Small (<100)", members: 0, premium: 42000, plans: [4] },
  { idx: 4, name: "Austin Tech Startup Alliance",   grp: GROUP_NUMBER + "-D",   type: "Employer", state: "TX", city: "Austin",      zip: "78702", tier: "Small (<100)", members: 0, premium: 28000, plans: [3] },
  { idx: 5, name: "Travis County Health Services",  grp: GROUP_NUMBER + "-GOV", type: "Government", state: "TX", city: "Austin",    zip: "78701", tier: "Large (500+)", members: 0, premium: 0, plans: [5] },
];

const sponsors = sponsorsData.map(function (s) {
  return {
    _id: makeId("spon", s.idx),
    TenantId: TENANT_ID,
    GroupNumber: s.grp,
    EmployerName: s.name,
    TaxId: "74-" + String(3200000 + s.idx * 111111).substring(0, 7),
    Address: (s.idx * 200) + " " + pick(["Congress Ave","Main St","Commerce St","Lamar Blvd","Guadalupe St"]) + ", Suite " + (s.idx * 100),
    City: s.city,
    State: s.state,
    ZipCode: s.zip,
    ContactName: pick(["Patricia","Robert","Linda","James","Maria"]) + " " + pick(["Garza","Tran","Davis","Park","Rivera"]),
    ContactPhone: "512" + String(2000000 + s.idx * 111111),
    ContactEmail: "benefits@sponsor-" + s.idx + ".test",
    EffectiveDate: new Date("2025-01-01"),
    TerminationDate: null,
    Status: 1,
    LineOfBusiness: s.idx === 5 ? 3 : 1,
    GroupSizeTier: s.tier,
    BillingInfo: {
      PremiumAmount: s.premium,
      Frequency: 1,
      BillingDay: s.idx <= 2 ? 1 : 15,
      BillingAccountNumber: s.grp + "-BA-001",
      PaymentMethod: s.idx === 5 ? "Wire" : "ACH",
      GracePeriodDays: 30
    },
    BenefitPlanIds: s.plans.map(function (p) { return makeId("plan", p); }),
    TotalMembers: 0,
    TotalDependents: 0,
    CreatedDate: now,
    LastUpdatedDate: now,
    CreatedBy: "seed-script",
    LastUpdatedBy: "seed-script"
  };
});
choDb.Sponsors.insertMany(sponsors);
print("✓ " + sponsors.length + " Sponsors");

// ═══════════════════════════════════════════════════════════════════════════
// 3. PROVIDERS (25)
// ═══════════════════════════════════════════════════════════════════════════

const individualProviders = [
  { idx: 1,  first: "Maria",    last: "Santos",     cred: "MD",  spec: "Family Medicine",     tax: "207Q00000X", langs: ["English","Spanish"] },
  { idx: 2,  first: "James",    last: "Chen",       cred: "DO",  spec: "Internal Medicine",    tax: "207R00000X", langs: ["English","Mandarin"] },
  { idx: 3,  first: "Rebecca",  last: "Okafor",     cred: "MD",  spec: "Orthopedics",          tax: "207X00000X", langs: ["English"] },
  { idx: 4,  first: "Raj",      last: "Patel",      cred: "MD",  spec: "Cardiology",           tax: "207RC0000X", langs: ["English","Hindi","Gujarati"] },
  { idx: 5,  first: "Catherine",last: "Reyes",      cred: "MD",  spec: "OB/GYN",               tax: "207V00000X", langs: ["English","Tagalog"] },
  { idx: 6,  first: "Thomas",   last: "Nguyen",     cred: "MD",  spec: "Pediatrics",           tax: "208000000X", langs: ["English","Vietnamese"] },
  { idx: 7,  first: "Karen",    last: "Mitchell",   cred: "MD",  spec: "Emergency Medicine",   tax: "207P00000X", langs: ["English"] },
  { idx: 8,  first: "David",    last: "Goldstein",  cred: "MD",  spec: "Radiology",            tax: "2085R0202X", langs: ["English"] },
  { idx: 9,  first: "Fatima",   last: "Al-Rashid",  cred: "MD",  spec: "Pathology",            tax: "207ZP0102X", langs: ["English","Arabic"] },
  { idx: 10, first: "Michael",  last: "Byrne",      cred: "MD",  spec: "Anesthesiology",       tax: "207L00000X", langs: ["English"] },
  { idx: 11, first: "Linda",    last: "Tran",       cred: "DPT", spec: "Physical Therapy",     tax: "225100000X", langs: ["English","Vietnamese"] },
  { idx: 12, first: "Andrew",   last: "Park",       cred: "MD",  spec: "Psychiatry",           tax: "2084P0800X", langs: ["English","Korean"] },
  { idx: 13, first: "Jasmine",  last: "Howard",     cred: "MD",  spec: "Dermatology",          tax: "207N00000X", langs: ["English"] },
  { idx: 14, first: "Robert",   last: "Kowalski",   cred: "MD",  spec: "Neurology",            tax: "2084N0400X", langs: ["English","Polish"] },
  { idx: 15, first: "Priya",    last: "Venkatesh",  cred: "MD",  spec: "Ophthalmology",        tax: "207W00000X", langs: ["English","Tamil","Hindi"] },
];

const orgProviders = [
  { idx: 16, name: "St. David's Medical Center",          dba: "St. David's",             spec: "Hospital",     tax: "282N00000X" },
  { idx: 17, name: "Dell Seton Medical Center",           dba: "Dell Seton",              spec: "Hospital",     tax: "282N00000X" },
  { idx: 18, name: "Ascension Seton Northwest",           dba: "Seton NW",                spec: "Hospital",     tax: "282N00000X" },
  { idx: 19, name: "Austin Urgent Care - South",          dba: "AUC South",               spec: "Urgent Care",  tax: "261QU0200X" },
  { idx: 20, name: "CareNow Clinic - North Lamar",        dba: "CareNow NL",              spec: "Urgent Care",  tax: "261QU0200X" },
  { idx: 21, name: "Lone Star Radiology Group",           dba: "Lone Star Radiology",     spec: "Imaging Center", tax: "261QR0200X" },
  { idx: 22, name: "Austin Advanced Imaging",             dba: "AAI",                      spec: "Imaging Center", tax: "261QR0200X" },
  { idx: 23, name: "Quest Diagnostics Austin",            dba: "Quest",                    spec: "Laboratory",   tax: "291U00000X" },
  { idx: 24, name: "Regency Oaks Skilled Nursing",        dba: "Regency Oaks SNF",        spec: "SNF",          tax: "314000000X" },
  { idx: 25, name: "Heart of Texas Home Health",          dba: "HTHH",                     spec: "Home Health",  tax: "251E00000X" },
];

const cities = ["Austin","Austin","Austin","Round Rock","Cedar Park","Georgetown","San Marcos","Kyle","Pflugerville","Leander"];
const streets = ["Medical Pkwy","Research Blvd","N MoPac Expy","S Lamar Blvd","W 38th St","Barton Springs Rd","S Congress Ave","E Riverside Dr","Burnet Rd","Anderson Ln"];

const providers = [];

individualProviders.forEach(function (p) {
  const npi = makeNpi(p.idx);
  const isOon = p.idx > 20; // all individual are in-network
  providers.push({
    _id: makeId("prov", p.idx),
    TenantId: TENANT_ID,
    NPI: npi,
    ProviderType: 1,
    FirstName: p.first,
    LastName: p.last,
    MiddleName: String.fromCharCode(65 + (p.idx % 26)),
    Credentials: p.cred,
    OrganizationName: null,
    PrimarySpecialty: p.spec,
    TaxonomyCode: p.tax,
    SecondarySpecialties: [],
    Address: (1000 + p.idx * 111) + " " + streets[p.idx % streets.length] + ", Suite " + (100 + p.idx * 10),
    City: cities[p.idx % cities.length],
    State: "TX",
    ZipCode: "78" + String(700 + p.idx).substring(0, 3),
    Phone: "512" + String(9870000 + p.idx * 1111),
    Fax: "512" + String(9870001 + p.idx * 1111),
    Email: "provider-" + String(p.idx).padStart(3, "0") + "@demo-clinic.test",
    NetworkParticipations: [{
      PlanId: makeId("plan", 1),
      LineOfBusiness: 1,
      NetworkTier: "Tier1",
      EffectiveDate: new Date("2025-01-01"),
      TerminationDate: null,
      AcceptingNewPatients: true
    }],
    CredentialingStatus: 2,
    CredentialingDate: daysAgo(randInt(180, 730)),
    RecredentialingDueDate: daysFromNow(randInt(365, 1095)),
    BoardCertifications: p.cred === "MD" || p.cred === "DO" ? [{ BoardName: "AB" + p.spec.substring(0, 4).toUpperCase(), CertificationDate: daysAgo(randInt(365, 3650)), ExpirationDate: daysFromNow(randInt(365, 3650)) }] : [],
    HospitalAffiliations: p.idx <= 7 ? [{ HospitalName: "St. David's Medical Center", NPI: makeNpi(16), PrivilegeStatus: "Active" }] : [],
    AcceptingNewPatients: true,
    HandicapAccessible: true,
    LanguagesSpoken: p.langs,
    ContractedRate: { FeeScheduleTier: "Tier1", ReimbursementMethod: p.idx <= 2 ? "Capitation" : "FeeSchedule", CapitationRate: p.idx <= 2 ? 42.50 : null },
    Status: 1,
    TerminationDate: null,
    CreatedDate: now,
    LastUpdatedDate: now,
    CreatedBy: "seed-script",
    LastUpdatedBy: "seed-script"
  });
});

orgProviders.forEach(function (p) {
  const npi = makeNpi(p.idx);
  const isOon = p.idx >= 21 && p.idx <= 25; // last 5 orgs are OON
  providers.push({
    _id: makeId("prov", p.idx),
    TenantId: TENANT_ID,
    NPI: npi,
    ProviderType: 2,
    FirstName: null,
    LastName: null,
    MiddleName: null,
    Credentials: null,
    OrganizationName: p.name,
    DBAName: p.dba,
    PrimarySpecialty: p.spec,
    TaxonomyCode: p.tax,
    SecondarySpecialties: [],
    Address: (2000 + p.idx * 100) + " " + streets[p.idx % streets.length],
    City: cities[p.idx % cities.length],
    State: "TX",
    ZipCode: "78" + String(700 + p.idx).substring(0, 3),
    Phone: "512" + String(5550000 + p.idx * 1111),
    Fax: "512" + String(5550001 + p.idx * 1111),
    Email: "facility-" + String(p.idx).padStart(3, "0") + "@demo-clinic.test",
    NetworkParticipations: isOon ? [] : [{
      PlanId: makeId("plan", 1),
      LineOfBusiness: 1,
      NetworkTier: "Tier1",
      EffectiveDate: new Date("2025-01-01"),
      TerminationDate: null,
      AcceptingNewPatients: true
    }],
    CredentialingStatus: isOon ? 0 : 2,
    CredentialingDate: isOon ? null : daysAgo(randInt(180, 730)),
    RecredentialingDueDate: isOon ? null : daysFromNow(randInt(365, 1095)),
    BoardCertifications: [],
    HospitalAffiliations: [],
    AcceptingNewPatients: !isOon,
    HandicapAccessible: true,
    LanguagesSpoken: ["English", "Spanish"],
    ContractedRate: isOon ? null : { FeeScheduleTier: p.spec === "Hospital" ? "Tier1-Facility" : "Tier2", ReimbursementMethod: "FeeSchedule" },
    Status: 1,
    TerminationDate: null,
    CreatedDate: now,
    LastUpdatedDate: now,
    CreatedBy: "seed-script",
    LastUpdatedBy: "seed-script"
  });
});

choDb.Providers.insertMany(providers);
print("✓ " + providers.length + " Providers");

const providerNameByIdx = {};
providers.forEach(function (p, i) {
  const idx = i + 1;
  providerNameByIdx[idx] = p.OrganizationName ? p.OrganizationName : (p.FirstName + " " + p.LastName + ", " + p.Credentials);
});
const providerNpiByIdx = {};
providers.forEach(function (p, i) { providerNpiByIdx[i + 1] = p.NPI; });

// In-network individual provider indices (for claims)
const inNetworkIndProviders = [1,2,3,4,5,6,7,8,9,10,11,12,13,14,15];
// In-network facility providers
const inNetworkFacProviders = [16,17,18,19,20];
// OON providers
const oonProviders = [21,22,23,24,25];

// ═══════════════════════════════════════════════════════════════════════════
// 4. MEMBERS (50)
// ═══════════════════════════════════════════════════════════════════════════

const firstNamesM = ["Carlos","Michael","William","Robert","David","James","Thomas","Daniel","Christopher","Matthew","Andrew","Joshua","Anthony","Kevin","Brian"];
const firstNamesF = ["Angela","Priya","Thanh","Sophia","Margaret","Jennifer","Emily","Jessica","Sarah","Amanda","Samantha","Nicole","Rachel","Megan","Laura"];
const lastNames = ["Ramirez","O'Brien","Henderson","Kim","Martinez","Johnson","Thompson","Garcia","Washington","Sharma","Le","Rodriguez","Patel","Foster","Anderson","Chen","Mitchell","Howard","Nguyen","Park","Davis","Wilson","Taylor","Brown","Moore","Jackson","White","Harris","Clark","Lewis","Robinson","Walker","Young","Allen","King","Wright","Scott","Hill","Green","Baker"];

const memberStatuses = []; // Active=1, Pending=2, Terminated=3, COBRA=5
for (let i = 0; i < 40; i++) memberStatuses.push(1);
for (let i = 0; i < 5; i++) memberStatuses.push(5);
for (let i = 0; i < 3; i++) memberStatuses.push(3);
for (let i = 0; i < 2; i++) memberStatuses.push(2);

// Subscriber/dependent mapping: members 1-35 are subscribers, 36-50 are dependents
const subscriberPlanAssignment = [
  // sponsor 1 (plans 1,2,3) — 18 subscribers
  1,1,1,2,2,2,3,3,1,1,2,2,3,1,2,1,3,1,
  // sponsor 2 (plans 1,2) — 7 subscribers
  1,1,2,2,1,2,1,
  // sponsor 3 (plan 4) — 4 subscribers
  4,4,4,4,
  // sponsor 4 (plan 3) — 3 subscribers
  3,3,3,
  // sponsor 5 (plan 5) — 3 subscribers
  5,5,5,
];
const subscriberSponsor = [
  1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,
  2,2,2,2,2,2,2,
  3,3,3,3,
  4,4,4,
  5,5,5,
];

const pcpProviders = [1,2,3,4,5,6,11,1,2,6]; // PCP-eligible providers

const members = [];
for (let i = 1; i <= 50; i++) {
  const isSub = i <= 35;
  const gender = i % 2 === 0 ? "F" : "M";
  const first = gender === "M" ? firstNamesM[(i - 1) % firstNamesM.length] : firstNamesF[(i - 1) % firstNamesF.length];
  const last = lastNames[(i - 1) % lastNames.length];
  const age = isSub ? randInt(22, 65) : randInt(2, 85);
  const status = memberStatuses[i - 1];

  let subIdx, planIdx, sponsorIdx, relCode;
  if (isSub) {
    subIdx = i;
    planIdx = subscriberPlanAssignment[i - 1];
    sponsorIdx = subscriberSponsor[i - 1];
    relCode = "18"; // self
  } else {
    // Dependent: link to a subscriber
    subIdx = ((i - 36) % 35) + 1; // cycle through subscribers
    planIdx = subscriberPlanAssignment[subIdx - 1];
    sponsorIdx = subscriberSponsor[subIdx - 1];
    relCode = age < 18 ? "19" : "01"; // child or spouse
  }

  const memberId = makeId("mbr", i);
  const subscriberId = "SUB" + String(100000 + subIdx);
  const sponsorData = sponsorsData[sponsorIdx - 1];

  const hasPcp = i <= 35 && status === 1; // 35 subscribers with PCP (Active=1)

  members.push({
    _id: memberId,
    TenantId: TENANT_ID,
    MemberId: memberId,
    SSN: "***-**-" + String(1000 + i),
    GroupNumber: sponsorData.grp,
    IsSubscriber: isSub,
    SubscriberMemberId: isSub ? null : makeId("mbr", subIdx),
    RelationshipCode: relCode,
    FirstName: first,
    LastName: last,
    MiddleName: null,
    DateOfBirth: dateOfBirth(age),
    Gender: gender === "M" ? "M" : "F",
    Address: (1000 + i * 37) + " " + pick(["Barton Springs Rd","Oak Lawn Ave","Westheimer Rd","Alamo Plaza","Commerce St","S Congress Ave","Montana Ave","Legacy Dr","Jollyville Rd","Randol Mill Rd"]),
    City: pick(["Austin","Austin","Austin","Round Rock","Cedar Park","Houston","San Antonio","Dallas","Pflugerville","Georgetown"]),
    State: "TX",
    ZipCode: "78" + String(700 + (i % 100)).substring(0, 3),
    Phone: "512" + String(2000000 + i * 1111),
    Email: (first.toLowerCase() + "." + last.toLowerCase().replace(/'/g, "") + "@example.com"),
    EffectiveDate: new Date("2025-01-01"),
    TerminationDate: status === 3 ? daysAgo(randInt(10, 60)) : null, // Terminated=3
    Status: status,
    LineOfBusiness: planIdx === 5 ? 3 : 1, // Commercial=1, Medicaid=3
    BenefitPlanId: makeId("plan", planIdx),
    SponsorId: makeId("spon", sponsorIdx),
    EmploymentStatus: status === 3 ? 5 : (status === 5 ? 5 : 1), // FullTime=1, Terminated=5; COBRA(5) members are also employment-terminated
    TobaccoUser: seededRandom() < 0.12,
    IsStudent: age >= 18 && age <= 25 && seededRandom() < 0.3,
    PcpProviderId: hasPcp ? makeId("prov", pcpProviders[i % pcpProviders.length]) : null,
    PcpProviderName: hasPcp ? providerNameByIdx[pcpProviders[i % pcpProviders.length]] : null,
    PcpAssignedDate: hasPcp ? daysAgo(randInt(30, 365)) : null,
    CreatedDate: now,
    LastUpdatedDate: now,
    CreatedBy: "seed-script",
    LastUpdatedBy: "seed-script"
  });
}

// Update sponsor member counts
sponsorsData.forEach(function (s) {
  const count = members.filter(function (m) { return m.SponsorId === makeId("spon", s.idx); }).length;
  s.members = count;
});

choDb.Members.insertMany(members);
print("✓ " + members.length + " Members");

// ═══════════════════════════════════════════════════════════════════════════
// 4b. COVERAGE (one record per member — required for 270/271 eligibility)
// ═══════════════════════════════════════════════════════════════════════════

const coverageLevels = { "18": "EMP", "01": "ESP", "19": "ECH" }; // self→EMP, spouse→ESP, child→ECH

const coverageRecords = members.map(function (m) {
  var isTerminated = m.Status === 3; // Terminated=3
  var isCobra = m.Status === 5; // COBRA=5
  var coverageStatus = isTerminated ? 3 : (isCobra ? 5 : (m.Status === 2 ? 2 : 1)); // Active=1, Pending=2, Terminated=3, COBRA=5
  return {
    _id: m.MemberId.replace("mbr", "cov"),
    TenantId: TENANT_ID,
    MemberId: m.MemberId,
    GroupNumber: m.GroupNumber,
    PlanId: m.BenefitPlanId,
    CoverageLevel: coverageLevels[m.RelationshipCode] || "EMP",
    InsuranceLineCode: "HLT",
    EffectiveDate: m.EffectiveDate,
    TerminationDate: m.TerminationDate,
    Status: coverageStatus,
    LineOfBusiness: m.LineOfBusiness, // Already integer: Commercial=1, Medicaid=3
    IsCOBRA: isCobra,
    COBRAEffectiveDate: isCobra ? m.EffectiveDate : null,
    MedicareCoverage: null,
    OtherInsurance: null,
    MonthlyPremium: m.LineOfBusiness === 3 ? 0 : (m.IsSubscriber ? 450.00 : 225.00), // Medicaid=3
    EmployerContribution: m.LineOfBusiness === 3 ? 0 : (m.IsSubscriber ? 350.00 : 175.00), // Medicaid=3
    MaintenanceTypeCode: "021",
    MaintenanceReasonCode: null,
    PcpNpi: m.PcpProviderId ? makeNpi(parseInt(m.PcpProviderId.split("-").pop())) : null,
    PcpName: m.PcpProviderName,
    PcpAssignmentDate: m.PcpAssignedDate,
    PcpAssignmentMethod: m.PcpProviderId ? 1 : null, // AutoAssigned=1
    PreviousPcpNpi: null,
    CreatedDate: now,
    LastUpdatedDate: now,
    CreatedBy: "seed-script",
    LastUpdatedBy: "seed-script"
  };
});

choDb.Coverage.insertMany(coverageRecords);
print("✓ " + coverageRecords.length + " Coverage records");

// ═══════════════════════════════════════════════════════════════════════════
// 5. CLAIMS (200)
// ═══════════════════════════════════════════════════════════════════════════

const cptPool = [
  { code: "99213", desc: "Office visit established, level 3", charge: [125, 175], cat: "PrimaryCare", type: "Professional" },
  { code: "99214", desc: "Office visit established, level 4", charge: [200, 275], cat: "PrimaryCare", type: "Professional" },
  { code: "99215", desc: "Office visit established, level 5", charge: [300, 400], cat: "Specialist", type: "Professional" },
  { code: "99243", desc: "Office consultation, level 3",      charge: [250, 350], cat: "Specialist", type: "Professional" },
  { code: "99283", desc: "Emergency dept visit, level 3",     charge: [800, 2500], cat: "Emergency", type: "Professional" },
  { code: "99284", desc: "Emergency dept visit, level 4",     charge: [1500, 4000], cat: "Emergency", type: "Professional" },
  { code: "99221", desc: "Initial hospital care, level 1",    charge: [2000, 5000], cat: "Inpatient", type: "Institutional" },
  { code: "99222", desc: "Initial hospital care, level 2",    charge: [3000, 7000], cat: "Inpatient", type: "Institutional" },
  { code: "99223", desc: "Initial hospital care, level 3",    charge: [4000, 12000], cat: "Inpatient", type: "Institutional" },
  { code: "70553", desc: "MRI brain w/ and w/o contrast",     charge: [1500, 4000], cat: "Imaging", type: "Professional" },
  { code: "71260", desc: "CT chest with contrast",            charge: [1200, 3000], cat: "Imaging", type: "Professional" },
  { code: "73721", desc: "MRI lower extremity joint",         charge: [1500, 4500], cat: "Imaging", type: "Professional" },
  { code: "80053", desc: "Comprehensive metabolic panel",     charge: [85, 200], cat: "Laboratory", type: "Professional" },
  { code: "85025", desc: "Complete blood count (CBC)",         charge: [90, 180], cat: "Laboratory", type: "Professional" },
  { code: "88305", desc: "Surgical pathology, level IV",      charge: [200, 500], cat: "Laboratory", type: "Professional" },
  { code: "97110", desc: "Therapeutic exercises",              charge: [75, 150], cat: "PhysicalTherapy", type: "Professional" },
  { code: "97140", desc: "Manual therapy techniques",          charge: [75, 150], cat: "PhysicalTherapy", type: "Professional" },
  { code: "27447", desc: "Total knee arthroplasty",           charge: [15000, 35000], cat: "OutpatientSurg", type: "Institutional" },
  { code: "29881", desc: "Arthroscopy knee, meniscectomy",    charge: [5000, 12000], cat: "OutpatientSurg", type: "Institutional" },
  { code: "49505", desc: "Inguinal hernia repair",            charge: [4000, 8000], cat: "OutpatientSurg", type: "Institutional" },
  { code: "90834", desc: "Psychotherapy, 45 minutes",         charge: [150, 250], cat: "MentalHealth", type: "Professional" },
  { code: "90837", desc: "Psychotherapy, 60 minutes",         charge: [180, 300], cat: "MentalHealth", type: "Professional" },
  { code: "99385", desc: "Preventive visit, 18-39",           charge: [200, 350], cat: "Preventive", type: "Professional" },
  { code: "99395", desc: "Preventive visit, 40-64",           charge: [250, 400], cat: "Preventive", type: "Professional" },
  { code: "D0120", desc: "Periodic oral evaluation",          charge: [50, 100], cat: "Dental", type: "Dental" },
  { code: "D1110", desc: "Prophylaxis - adult",               charge: [80, 150], cat: "Dental", type: "Dental" },
  { code: "D2740", desc: "Crown - porcelain/ceramic",         charge: [800, 1500], cat: "Dental", type: "Dental" },
];

const diagPool = [
  { code: "M54.5",  desc: "Low back pain" },
  { code: "Z00.00", desc: "General adult medical examination" },
  { code: "I10",    desc: "Essential hypertension" },
  { code: "E11.9",  desc: "Type 2 diabetes mellitus w/o complications" },
  { code: "J06.9",  desc: "Acute upper respiratory infection, unspecified" },
  { code: "M17.11", desc: "Unilateral primary osteoarthritis, right knee" },
  { code: "G43.909",desc: "Migraine, unspecified" },
  { code: "F41.1",  desc: "Generalized anxiety disorder" },
  { code: "R10.9",  desc: "Unspecified abdominal pain" },
  { code: "N39.0",  desc: "Urinary tract infection" },
  { code: "J44.1",  desc: "COPD with acute exacerbation" },
  { code: "K21.0",  desc: "GERD with esophagitis" },
];

const denialReasons = [
  { code: "CO-50",  reason: "Not medically necessary" },
  { code: "CO-29",  reason: "Service not covered under plan" },
  { code: "CO-197", reason: "Prior authorization required but not obtained" },
  { code: "CO-29",  reason: "Timely filing limit exceeded" },
  { code: "CO-18",  reason: "Duplicate claim/service" },
];

const posMap = { "PrimaryCare": "11", "Specialist": "11", "Emergency": "23", "Inpatient": "21", "OutpatientSurg": "24", "Imaging": "22", "Laboratory": "81", "PhysicalTherapy": "11", "MentalHealth": "11", "Preventive": "11", "Dental": "11" };

// Status distribution: 140 Adjudicated, 25 Pending, 20 Denied, 15 WorkQueue
const claimStatusSlots = [];
for (let i = 0; i < 140; i++) claimStatusSlots.push("Adjudicated");
for (let i = 0; i < 25; i++) claimStatusSlots.push("Pending");
for (let i = 0; i < 20; i++) claimStatusSlots.push("Denied");
for (let i = 0; i < 15; i++) claimStatusSlots.push("WorkQueue");

// Type distribution: 150 Prof, 40 Inst, 10 Dental
function pickClaimCpt(claimIdx) {
  if (claimIdx > 190) return cptPool.filter(function (c) { return c.type === "Dental"; });
  if (claimIdx > 150) return cptPool.filter(function (c) { return c.type === "Institutional"; });
  return cptPool.filter(function (c) { return c.type === "Professional"; });
}

// Track per-member accumulations for consistency
const memberAccumData = {};
members.forEach(function (m) {
  memberAccumData[m.MemberId] = { deductible: 0, copay: 0, coinsurance: 0, planPaid: 0, ptVisits: 0, mhVisits: 0 };
});

const claims = [];
const adjudicatedClaimIds = [];
const deniedClaimIds = [];
const workQueueClaimIds = [];

for (let c = 1; c <= 200; c++) {
  const memberIdx = ((c - 1) % 45) + 1; // spread across first 45 members (active/COBRA)
  const member = members[memberIdx - 1];
  const statusSlot = claimStatusSlots[c - 1];
  const serviceDate = daysAgo(randInt(1, 90));
  const receivedDate = new Date(serviceDate.getTime() + randInt(1, 5) * 86400000);
  const claimId = makeId("clm", c);

  // Pick CPTs appropriate for claim type
  const cptCandidates = pickClaimCpt(c);
  const numLines = randInt(1, Math.min(6, cptCandidates.length));
  const selectedCpts = pickN(cptCandidates, numLines);

  const primaryDiag = pick(diagPool);
  const secondaryDiag = pick(diagPool.filter(function (d) { return d.code !== primaryDiag.code; }));

  // Pick provider
  const provIdx = pick(inNetworkIndProviders.concat(inNetworkFacProviders));
  const provNpi = providerNpiByIdx[provIdx];
  const provName = providerNameByIdx[provIdx];

  const claimType = selectedCpts[0].type === "Dental" ? "Dental" : (selectedCpts[0].type === "Institutional" ? "Institutional" : "Professional");
  const facilityIdx = pick([16, 17, 18]);
  const pos = posMap[selectedCpts[0].cat] || "11";

  // Build service lines
  const lineItems = selectedCpts.map(function (cpt, li) {
    const chargedAmount = roundMoney(cpt.charge[0] + seededRandom() * (cpt.charge[1] - cpt.charge[0]));
    return {
      LineNumber: li + 1,
      ProcedureCode: cpt.code,
      ProcedureDescription: cpt.desc,
      Modifiers: [],
      ChargeAmount: chargedAmount,
      Units: cpt.cat === "PhysicalTherapy" ? randInt(1, 4) : 1,
      PlaceOfServiceCode: posMap[cpt.cat] || "11",
      ServiceDateFrom: serviceDate,
      ServiceDateTo: serviceDate,
      DiagnosisPointers: [1, 2]
    };
  });

  const totalCharged = roundMoney(lineItems.reduce(function (s, l) { return s + l.ChargeAmount; }, 0));

  const claim = {
    _id: claimId,
    TenantId: TENANT_ID,
    ClaimNumber: "CLM-2026-" + String(c).padStart(5, "0"),
    MemberId: member.MemberId,
    SubscriberId: "SUB" + String(100000 + ((memberIdx <= 35) ? memberIdx : (((memberIdx - 36) % 35) + 1))),
    BenefitPlanId: member.BenefitPlanId,
    CoverageId: null,
    SubscriberFirstName: member.FirstName,
    SubscriberLastName: member.LastName,
    PatientFirstName: member.FirstName,
    PatientLastName: member.LastName,
    PatientRelationship: member.RelationshipCode,
    LineOfBusiness: member.LineOfBusiness,
    BillingProviderNPI: provNpi,
    BillingProviderName: provName,
    RenderingProviderNPI: provNpi,
    RenderingProviderName: provName,
    FacilityNPI: claimType === "Institutional" ? providerNpiByIdx[facilityIdx] : null,
    FacilityName: claimType === "Institutional" ? providerNameByIdx[facilityIdx] : null,
    PlaceOfServiceCode: pos,
    ServiceDateFrom: serviceDate,
    ServiceDateTo: serviceDate,
    ReceivedDate: receivedDate,
    Status: statusSlot === "Adjudicated" ? 7 : (statusSlot === "WorkQueue" ? 4 : (statusSlot === "Pending" ? 4 : (statusSlot === "Denied" ? 6 : 1))), // Submitted=1, Pended=4, Denied=6, Paid=7
    ClaimType: claimType === "Dental" ? 3 : (claimType === "Institutional" ? 2 : 1), // Professional=1, Institutional=2, Dental=3
    TotalChargeAmount: totalCharged,
    DiagnosisCodes: [
      { Code: primaryDiag.code, CodeQualifier: "BK", Description: primaryDiag.desc },
      { Code: secondaryDiag.code, CodeQualifier: "BF", Description: secondaryDiag.desc },
    ],
    ClaimLines: lineItems,
    AdjudicationResult: null,
    DenialReasonCode: null,
    DenialReason: null,
    CreatedDate: now,
    LastUpdatedDate: now,
    CreatedBy: "seed-script",
    LastUpdatedBy: "seed-script"
  };

  // Adjudicate paid claims
  if (statusSlot === "Adjudicated") {
    const plan = planCostSharing[member.BenefitPlanId];
    const allowedAmount = roundMoney(totalCharged * (0.7 + seededRandom() * 0.15));
    const deductibleAmount = plan ? roundMoney(Math.min(allowedAmount * 0.1, plan.indDed * 0.05)) : 0;
    const copayAmount = plan ? plan.copayPCP : 30;
    const coinsuranceAmount = plan ? roundMoney((allowedAmount - deductibleAmount - copayAmount) * (plan.coinsIn / 100)) : 0;
    const memberResp = roundMoney(deductibleAmount + copayAmount + coinsuranceAmount);
    const planPaid = roundMoney(Math.max(0, allowedAmount - memberResp));

    claim.AdjudicationResult = {
      AdjudicatedDate: new Date(serviceDate.getTime() + randInt(3, 14) * 86400000),
      AllowedAmount: allowedAmount,
      PlanPaid: planPaid,
      PatientResponsibility: memberResp,
      CopayAmount: copayAmount,
      CoinsuranceAmount: Math.max(0, coinsuranceAmount),
      DeductibleAmount: deductibleAmount,
      AdjustmentReasonCode: "CO-45",
      AdjustmentAmount: roundMoney(totalCharged - allowedAmount),
      CheckNumber: "EFT-" + String(900000 + c),
      PaidDate: new Date(serviceDate.getTime() + randInt(14, 30) * 86400000)
    };

    // Track accumulators
    const acc = memberAccumData[member.MemberId];
    acc.deductible += deductibleAmount;
    acc.copay += copayAmount;
    acc.coinsurance += coinsuranceAmount;
    acc.planPaid += planPaid;
    if (selectedCpts.some(function (cpt) { return cpt.cat === "PhysicalTherapy"; })) acc.ptVisits += randInt(1, 4);
    if (selectedCpts.some(function (cpt) { return cpt.cat === "MentalHealth"; })) acc.mhVisits += 1;

    adjudicatedClaimIds.push(claimId);
  }

  if (statusSlot === "Denied") {
    const denial = denialReasons[c % denialReasons.length];
    claim.DenialReasonCode = denial.code;
    claim.DenialReason = denial.reason;
    deniedClaimIds.push(claimId);
  }

  if (statusSlot === "WorkQueue") {
    workQueueClaimIds.push(claimId);
  }

  claims.push(claim);
}

choDb.Claims.insertMany(claims);
print("✓ " + claims.length + " Claims");

// ═══════════════════════════════════════════════════════════════════════════
// 6. AUTHORIZATIONS (40)
// ═══════════════════════════════════════════════════════════════════════════

const authServiceTypes = [
  { type: "Inpatient",          codes: ["99221","99222","99223"], count: 8 },
  { type: "Outpatient Surgery", codes: ["27447","29881","49505"], count: 6 },
  { type: "Imaging",            codes: ["70553","71260","73721"], count: 8 },
  { type: "Physical Therapy",   codes: ["97110","97140","97530"], count: 6 },
  { type: "Specialist Referral",codes: ["99243","99244","99245"], count: 5 },
  { type: "DME",                codes: ["E0601","E0260","L3670"], count: 3 },
  { type: "Home Health",        codes: ["99341","99342","99343"], count: 4 },
];

// Status: 20 Approved, 8 Pending, 5 Denied, 4 Partial, 3 Expired
const authStatuses = [];
for (let i = 0; i < 20; i++) authStatuses.push("Approved");
for (let i = 0; i < 8; i++) authStatuses.push("InReview");
for (let i = 0; i < 5; i++) authStatuses.push("Denied");
for (let i = 0; i < 4; i++) authStatuses.push("Modified");
for (let i = 0; i < 3; i++) authStatuses.push("Expired");

const deniedAuthIds = [];
const authorizations = [];
let authIdx = 0;
authServiceTypes.forEach(function (svc) {
  for (let i = 0; i < svc.count; i++) {
    authIdx++;
    const member = members[(authIdx * 3) % 45];
    const provIdx = pick(inNetworkIndProviders);
    const status = authStatuses[authIdx - 1] || "Approved";
    const reqUnits = svc.type === "Physical Therapy" ? randInt(8, 24) : (svc.type === "Inpatient" ? randInt(2, 7) : randInt(1, 3));
    const appUnits = status === "Approved" ? reqUnits : (status === "Modified" ? Math.floor(reqUnits * 0.6) : (status === "Denied" ? 0 : null));
    const diag = pick(diagPool);
    const submitDate = daysAgo(randInt(5, 65));

    const auth = {
      _id: makeId("auth", authIdx),
      TenantId: TENANT_ID,
      AuthorizationNumber: "AUTH-2026-" + String(authIdx).padStart(5, "0"),
      MemberId: member.MemberId,
      CoverageId: null,
      PatientFirstName: member.FirstName,
      PatientLastName: member.LastName,
      PatientDateOfBirth: member.DateOfBirth,
      LineOfBusiness: member.LineOfBusiness,
      RequestingProviderNPI: providerNpiByIdx[provIdx],
      RequestingProviderName: providerNameByIdx[provIdx],
      ServicingProviderNPI: providerNpiByIdx[provIdx],
      ServicingProviderName: providerNameByIdx[provIdx],
      FacilityNPI: svc.type === "Inpatient" ? providerNpiByIdx[16] : null,
      FacilityName: svc.type === "Inpatient" ? providerNameByIdx[16] : null,
      AuthorizationType: svc.type === "Specialist Referral" ? 2 : 1, // PreAuthorization=1, Referral=2
      CertificationTypeCode: svc.type === "Inpatient" ? "I" : "S",
      ServiceTypeCode: "02",
      LevelOfService: "U",
      RequestedServiceDateFrom: new Date(submitDate.getTime() + 7 * 86400000),
      RequestedServiceDateTo: new Date(submitDate.getTime() + (svc.type === "Physical Therapy" ? 90 : 30) * 86400000),
      DiagnosisCodes: [{ Code: diag.code, CodeQualifier: "BK", Description: diag.desc }],
      RequestedServices: [{
        ProcedureCode: pick(svc.codes),
        ProcedureDescription: svc.type,
        Modifiers: [],
        RequestedUnits: reqUnits,
        ApprovedUnits: appUnits,
        UnitType: svc.type === "Inpatient" ? "Day" : "Visit",
        PlaceOfServiceCode: svc.type === "Inpatient" ? "21" : "11",
        ServiceStatus: status === "InReview" ? 3 : (status === "Approved" ? 4 : (status === "Denied" ? 6 : (status === "Modified" ? 5 : (status === "Expired" ? 7 : 1)))) // Pended=3, Approved=4, Modified=5, Denied=6, Expired=7
      }],
      Status: status === "Approved" ? 4 : (status === "Denied" ? 6 : (status === "InReview" ? 2 : (status === "Modified" ? 5 : (status === "Expired" ? 7 : 1)))), // Submitted=1, InReview=2, Pended=3, Approved=4, Modified=5, Denied=6, Expired=7
      ReviewDecision: status === "Approved" ? "A1" : (status === "Denied" ? "A4" : (status === "Modified" ? "A2" : null)),
      ApprovedUnits: appUnits,
      ApprovedServiceDateFrom: status === "Approved" || status === "Modified" ? new Date(submitDate.getTime() + 7 * 86400000) : null,
      ApprovedServiceDateTo: status === "Approved" || status === "Modified" ? new Date(submitDate.getTime() + 90 * 86400000) : null,
      DenialReasonCode: status === "Denied" ? "NOT_MEDICALLY_NECESSARY" : null,
      DenialReason: status === "Denied" ? "Clinical documentation does not support medical necessity for requested service." : null,
      ReviewerName: status !== "InReview" ? pick(["Dr. Sarah Williams", "Dr. Mark Torres", "Dr. Lisa Chen"]) : null,
      SubmittedDate: submitDate,
      ReviewedDate: status !== "InReview" ? new Date(submitDate.getTime() + randInt(1, 5) * 86400000) : null,
      ExpirationDate: status === "Expired" ? daysAgo(randInt(1, 15)) : (status === "Approved" ? daysFromNow(randInt(30, 90)) : null),
      Notes: "",
      CreatedDate: now,
      LastUpdatedDate: now,
      CreatedBy: "seed-script",
      LastUpdatedBy: "seed-script"
    };

    if (status === "Denied") deniedAuthIds.push(auth._id);
    authorizations.push(auth);
  }
});

choDb.Authorizations.insertMany(authorizations);
print("✓ " + authorizations.length + " Authorizations");

// ═══════════════════════════════════════════════════════════════════════════
// 7. WORK QUEUE ITEMS (35)
// ═══════════════════════════════════════════════════════════════════════════

const wqReasons = [
  { code: "NCCI", reason: "NCCI/MUE Edit Failure", count: 12 },
  { code: "AUTH", reason: "Missing Prior Authorization", count: 8 },
  { code: "OON",  reason: "Provider Not Contracted", count: 6 },
  { code: "COB",  reason: "COB/Other Payer Required", count: 5 },
  { code: "MED",  reason: "Medical Review Required", count: 4 },
];

const examiners = ["Sarah Williams", "James Martinez", "Priya Kapoor", "David Chen"];
const priorities = ["High", "Medium", "Medium", "Low", "Low"];

const workQueueItems = [];
let wqIdx = 0;
wqReasons.forEach(function (wq) {
  for (let i = 0; i < wq.count; i++) {
    wqIdx++;
    // Reference a real claim — use work queue claims first, then cycle adjudicated
    const claimRef = wqIdx <= workQueueClaimIds.length ? workQueueClaimIds[wqIdx - 1] : adjudicatedClaimIds[wqIdx % adjudicatedClaimIds.length];
    const refClaim = claims.find(function (cl) { return cl._id === claimRef; });

    workQueueItems.push({
      _id: makeId("wq", wqIdx),
      TenantId: TENANT_ID,
      ClaimId: claimRef,
      ClaimNumber: refClaim ? refClaim.ClaimNumber : "CLM-2026-" + String(wqIdx).padStart(5, "0"),
      MemberName: refClaim ? (refClaim.PatientFirstName + " " + refClaim.PatientLastName) : "Unknown",
      MemberId: refClaim ? refClaim.MemberId : makeId("mbr", 1),
      ProviderName: refClaim ? refClaim.BillingProviderName : "Unknown Provider",
      ServiceDate: refClaim ? refClaim.ServiceDateFrom : daysAgo(randInt(1, 14)),
      QueueReason: wq.reason,
      QueueReasonCode: wq.code,
      DaysInQueue: randInt(0, 14),
      Priority: priorities[wqIdx % priorities.length],
      AssignedTo: examiners[wqIdx % examiners.length],
      TotalCharged: refClaim ? refClaim.TotalChargeAmount : 1000,
      ProcedureCodes: refClaim ? refClaim.ClaimLines.map(function (l) { return l.ProcedureCode; }) : ["99213"],
      CreatedDate: daysAgo(randInt(0, 14)),
      LastUpdatedDate: now,
      CreatedBy: "seed-script"
    });
  }
});

choDb.WorkQueueItems.insertMany(workQueueItems);
print("✓ " + workQueueItems.length + " WorkQueueItems");

// ═══════════════════════════════════════════════════════════════════════════
// 8. PAYMENTS (80) — from adjudicated claims, grouped into 6 runs
// ═══════════════════════════════════════════════════════════════════════════

const paymentRuns = [
  { runIdx: 1, name: "January 2026 Bi-Weekly #1", date: daysAgo(75) },
  { runIdx: 2, name: "January 2026 Bi-Weekly #2", date: daysAgo(60) },
  { runIdx: 3, name: "February 2026 Bi-Weekly #1", date: daysAgo(45) },
  { runIdx: 4, name: "February 2026 Bi-Weekly #2", date: daysAgo(30) },
  { runIdx: 5, name: "March 2026 Bi-Weekly #1",    date: daysAgo(15) },
  { runIdx: 6, name: "March 2026 Bi-Weekly #2",    date: daysAgo(2) },
];

const payments = [];
const payableClaims = claims.filter(function (cl) { return cl.AdjudicationResult && cl.AdjudicationResult.PlanPaid > 0; });
const claimsToUse = payableClaims.slice(0, 80);

claimsToUse.forEach(function (cl, i) {
  const payIdx = i + 1;
  const run = paymentRuns[i % paymentRuns.length];
  payments.push({
    _id: makeId("pmt", payIdx),
    TenantId: TENANT_ID,
    PaymentId: makeId("pmt", payIdx),
    PaymentRunId: "PMTRUN-2026-" + String(run.runIdx).padStart(4, "0"),
    PaymentRunName: run.name,
    ClaimId: cl._id,
    ClaimNumber: cl.ClaimNumber,
    MemberId: cl.MemberId,
    MemberName: cl.PatientFirstName + " " + cl.PatientLastName,
    ProviderNpi: cl.BillingProviderNPI,
    ProviderName: cl.BillingProviderName,
    PaymentMethod: i % 5 === 0 ? "Check" : "EFT",
    CheckEftNumber: (i % 5 === 0 ? "CHK-" : "EFT-") + String(100000 + payIdx),
    PaymentDate: run.date,
    ChargeAmount: cl.TotalChargeAmount,
    AllowedAmount: cl.AdjudicationResult.AllowedAmount,
    PaidAmount: cl.AdjudicationResult.PlanPaid,
    MemberResponsibility: cl.AdjudicationResult.PatientResponsibility,
    Status: 2, // Posted=2
    CreatedDate: now,
    CreatedBy: "seed-script"
  });
});

choDb.Payments.insertMany(payments);
print("✓ " + payments.length + " Payments");

// ═══════════════════════════════════════════════════════════════════════════
// 9. ACCUMULATORS (50) — consistent with adjudicated claims
// ═══════════════════════════════════════════════════════════════════════════

const accumulators = members.map(function (m) {
  const acc = memberAccumData[m.MemberId];
  const plan = planCostSharing[m.BenefitPlanId];
  const indDedLimit = plan ? plan.indDed : 1500;
  const famDedLimit = plan ? plan.famDed : 3000;
  const indOopLimit = plan ? plan.indOop : 5000;
  const famOopLimit = plan ? plan.famOop : 10000;

  return {
    _id: makeId("acc", members.indexOf(m) + 1),
    TenantId: TENANT_ID,
    OwnerId: m.MemberId,
    Scope: "Individual",
    BenefitPlanId: m.BenefitPlanId,
    PlanYear: "2026",
    Version: 1,
    LastUpdated: now,
    Balances: [
      { Type: "IndividualDeductible", NetworkTier: "InNetwork", LimitAmount: indDedLimit, AccumulatedAmount: roundMoney(acc.deductible) },
      { Type: "FamilyDeductible", NetworkTier: "InNetwork", LimitAmount: famDedLimit, AccumulatedAmount: roundMoney(acc.deductible * 1.5) },
      { Type: "IndividualOOP", NetworkTier: "InNetwork", LimitAmount: indOopLimit, AccumulatedAmount: roundMoney(acc.deductible + acc.copay + acc.coinsurance) },
      { Type: "FamilyOOP", NetworkTier: "InNetwork", LimitAmount: famOopLimit, AccumulatedAmount: roundMoney((acc.deductible + acc.copay + acc.coinsurance) * 1.5) },
    ],
    Transactions: [],
    CreatedDate: now,
    CreatedBy: "seed-script"
  };
});

choDb.Accumulators.insertMany(accumulators);
print("✓ " + accumulators.length + " Accumulators");

// Override member 1's accumulator with specific demo values
// (believable partial-year utilisation for the demo member)
choDb.Accumulators.updateOne(
  { OwnerId: makeId("mbr", 1), TenantId: TENANT_ID },
  { $set: {
    Balances: [
      { Type: "IndividualDeductible", NetworkTier: "InNetwork", LimitAmount: 1500,  AccumulatedAmount: 875.00  },
      { Type: "FamilyDeductible",     NetworkTier: "InNetwork", LimitAmount: 3000,  AccumulatedAmount: 1240.00 },
      { Type: "IndividualOOP",        NetworkTier: "InNetwork", LimitAmount: 6000,  AccumulatedAmount: 2100.00 },
      { Type: "FamilyOOP",            NetworkTier: "InNetwork", LimitAmount: 12000, AccumulatedAmount: 3150.00 }
    ]
  }}
);
print("✓ Member 1 accumulator overridden with demo values (ded $875/$1500, OOP $2100/$6000)");

// ═══════════════════════════════════════════════════════════════════════════
// 10. APPEALS (12)
// ═══════════════════════════════════════════════════════════════════════════

const appealStatuses = ["Open", "Open", "Open", "Under Review", "Under Review", "Under Review", "Under Review", "Decision Made", "Decision Made", "Decision Made", "Escalated", "Escalated"];
const appealTypes = ["Claim", "Claim", "Claim", "Claim", "Authorization", "Authorization", "Authorization", "Authorization", "Authorization", "Coverage", "Coverage", "Coverage"];

const appeals = [];
for (let i = 1; i <= 12; i++) {
  const member = members[((i - 1) * 4) % 45];
  const status = appealStatuses[i - 1];
  const type = appealTypes[i - 1];
  const filedDate = daysAgo(randInt(2, 30));
  const dueDate = daysFromNow(randInt(1, 30));

  let origId;
  if (type === "Claim" && deniedClaimIds.length > 0) {
    origId = deniedClaimIds[(i - 1) % deniedClaimIds.length];
  } else if (type === "Authorization" && deniedAuthIds.length > 0) {
    origId = deniedAuthIds[(i - 1) % deniedAuthIds.length];
  } else {
    origId = "COV-2026-" + String(i).padStart(5, "0");
  }

  appeals.push({
    _id: makeId("apl", i),
    TenantId: TENANT_ID,
    AppealNumber: "APL-2026-" + String(i).padStart(4, "0"),
    PatientName: member.FirstName + " " + member.LastName,
    MemberId: member.MemberId,
    AppealType: type === "Claim" ? 0 : (type === "Authorization" ? 1 : 2), // Claim=0 (Reconsideration), Authorization=1 (PeerReview), Coverage=2 (ExternalReview)
    OriginalDecisionId: origId,
    OriginalDecision: 1, // Denied=1
    DenialReason: pick(denialReasons).reason,
    Status: status === "Open" ? 1 : (status === "Under Review" ? 2 : (status === "Decision Made" ? 4 : (status === "Escalated" ? 5 : 1))), // Submitted=1, InReview=2, Approved=4, Denied=5
    IsUrgent: i <= 2,
    SubmittedDate: filedDate,
    TargetResponseDate: dueDate,
    DaysRemaining: Math.round((dueDate - TODAY) / 86400000),
    AssignedReviewer: status !== "Open" ? pick(["Dr. Mark Torres", "Dr. Sarah Williams", "Medical Director"]) : "",
    ComplianceStatus: status === "Escalated" ? "At Risk" : "On Track",
    Decision: status === "Decision Made" ? { DecisionType: (i % 2 === 0 ? 0 : 1), DecisionDate: daysAgo(randInt(1, 5)) } : null, // Approved/Overturned=0, Denied/Upheld=1
    DecisionDate: status === "Decision Made" ? daysAgo(randInt(1, 5)) : null,
    CreatedDate: filedDate,
    LastUpdatedDate: now,
    CreatedBy: "seed-script"
  });
}

choDb.Appeals.insertMany(appeals);
print("✓ " + appeals.length + " Appeals");

// ═══════════════════════════════════════════════════════════════════════════
// 11. CORRESPONDENCE (20)
// ═══════════════════════════════════════════════════════════════════════════

const corrTypes = [];
for (let i = 0; i < 8; i++) corrTypes.push("Adverse Determination");
for (let i = 0; i < 5; i++) corrTypes.push("RFAI");
for (let i = 0; i < 4; i++) corrTypes.push("EOB");
for (let i = 0; i < 3; i++) corrTypes.push("Welcome Letter");

const corrStatuses = ["Queued","Queued","Queued","Queued","Queued","Sent","Sent","Sent","Sent","Sent","Sent","Sent","Sent","Delivered","Delivered","Delivered","Delivered","Failed","Failed","Failed"];

const correspondence = [];
for (let i = 1; i <= 20; i++) {
  const member = members[((i - 1) * 2) % 45];
  const type = corrTypes[i - 1];
  const status = corrStatuses[i - 1];
  const relatedId = type === "Adverse Determination" ? (deniedClaimIds[(i - 1) % Math.max(1, deniedClaimIds.length)]) :
                    type === "RFAI" ? (workQueueClaimIds[(i - 1) % Math.max(1, workQueueClaimIds.length)]) :
                    type === "EOB" ? (adjudicatedClaimIds[(i - 1) % Math.max(1, adjudicatedClaimIds.length)]) :
                    makeId("mbr", ((i - 1) % 10) + 1);

  correspondence.push({
    _id: makeId("ltr", i),
    TenantId: TENANT_ID,
    LetterId: "LTR-2026-" + String(5000 + i).padStart(5, "0"),
    LetterType: type,
    RecipientName: type === "RFAI" ? providerNameByIdx[pick(inNetworkIndProviders)] : (member.FirstName + " " + member.LastName),
    RecipientType: type === "RFAI" ? "Provider" : "Member",
    RelatedId: relatedId,
    GeneratedDate: status === "Queued" ? null : daysAgo(randInt(0, 7)),
    Status: status,
    DeliveryMethod: pick(["Mail", "Fax", "Email", "Portal"]),
    CreatedDate: now,
    CreatedBy: "seed-script"
  });
}

choDb.Correspondence.insertMany(correspondence);
print("✓ " + correspondence.length + " Correspondence");

// ═══════════════════════════════════════════════════════════════════════════
// 12. ENROLLMENT FILES (8) — 834 processing records
// ═══════════════════════════════════════════════════════════════════════════

const enrollmentFiles = [];
for (let i = 1; i <= 8; i++) {
  const sponsorData = sponsorsData[(i - 1) % sponsorsData.length];
  const receivedTime = daysAgo(i <= 2 ? 0 : (i <= 4 ? randInt(1, 7) : randInt(8, 30)));
  receivedTime.setHours(6, randInt(0, 30), 0, 0);
  const totalRecords = randInt(30, 250);
  const hasErrors = i === 3; // One file with errors
  const rejectedCount = hasErrors ? randInt(5, 15) : (seededRandom() < 0.3 ? randInt(1, 4) : 0);
  const acceptedCount = totalRecords - rejectedCount - (i === 8 ? 0 : 0);

  enrollmentFiles.push({
    _id: makeId("834f", i),
    TenantId: TENANT_ID,
    FileId: "834-" + receivedTime.toISOString().substring(0, 10).replace(/-/g, "") + "-" + String(i).padStart(3, "0"),
    FileName: sponsorData.name.substring(0, 3).toUpperCase() + "_834_" + receivedTime.toISOString().substring(0, 10).replace(/-/g, "") + "_0" + i + "00.edi",
    ReceivedTime: receivedTime,
    SponsorName: sponsorData.name,
    SponsorId: makeId("spon", (i - 1) % sponsorsData.length + 1),
    GroupNumber: sponsorData.grp,
    TransactionCount: totalRecords,
    AddedCount: randInt(3, Math.floor(totalRecords * 0.2)),
    TermedCount: randInt(1, Math.floor(totalRecords * 0.1)),
    ChangedCount: totalRecords - rejectedCount - randInt(5, 20),
    RejectedCount: rejectedCount,
    Status: hasErrors ? "PartiallyAccepted" : (i === 8 ? "Failed" : "Completed"),
    Rejections: hasErrors ? [
      { MemberId: "MBR-ERR-001", MemberName: "Garcia, Roberto", ErrorCode: "834-E003", ErrorDescription: "Invalid date of birth format" },
      { MemberId: "MBR-ERR-002", MemberName: "Petrov, Natasha", ErrorCode: "834-E007", ErrorDescription: "Subscriber not found in active roster" },
      { MemberId: "MBR-ERR-003", MemberName: "Chen, Mei-Lin",   ErrorCode: "834-E015", ErrorDescription: "Coverage date gap detected (14 days)" },
    ] : [],
    ErrorMessage: i === 8 ? "SFTP connection timeout after 30s — file transfer incomplete" : null,
    CreatedDate: receivedTime,
    CreatedBy: "seed-script"
  });
}

choDb.EnrollmentFiles.insertMany(enrollmentFiles);
print("✓ " + enrollmentFiles.length + " EnrollmentFiles");

// ═══════════════════════════════════════════════════════════════════════════
// SUMMARY
// ═══════════════════════════════════════════════════════════════════════════

print("\n═══ Seed complete for tenant: " + TENANT_ID + " ═══");
print("  BenefitPlans:     " + benefitPlans.length);
print("  Sponsors:         " + sponsors.length);
print("  Providers:        " + providers.length + " (" + individualProviders.length + " individual, " + orgProviders.length + " organization)");
print("  Members:          " + members.length + " (35 subscribers, 15 dependents)");
print("  Claims:           " + claims.length + " (" + adjudicatedClaimIds.length + " paid, " + deniedClaimIds.length + " denied, 25 pending, " + workQueueClaimIds.length + " work queue)");
print("  Authorizations:   " + authorizations.length);
print("  WorkQueueItems:   " + workQueueItems.length);
print("  Payments:         " + payments.length);
print("  Accumulators:     " + accumulators.length);
print("  Appeals:          " + appeals.length);
print("  Correspondence:   " + correspondence.length);
print("  EnrollmentFiles:  " + enrollmentFiles.length);

const total = benefitPlans.length + sponsors.length + providers.length + members.length +
  claims.length + authorizations.length + workQueueItems.length + payments.length +
  accumulators.length + appeals.length + correspondence.length + enrollmentFiles.length;
print("\n  Total documents:  " + total);
