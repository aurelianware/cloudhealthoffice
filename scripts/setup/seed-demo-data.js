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
  "Members", "Claims", "Providers", "BenefitPlans", "Sponsors",
  "Authorizations", "WorkQueueItems", "Payments", "Accumulators",
  "Appeals", "Correspondence", "EnrollmentFiles"
];

COLLECTIONS.forEach(function (col) {
  const r = choDb[col].deleteMany({ tenantId: TENANT_ID });
  if (r.deletedCount > 0) print("  Cleared " + r.deletedCount + " from " + col);
});

// ═══════════════════════════════════════════════════════════════════════════
// 1. BENEFIT PLANS (5)
// ═══════════════════════════════════════════════════════════════════════════

function makePlanBenefits(copayPCP, copaySpec, coinsIn, coinsOut, erCopay, deductAppliesPCP) {
  return [
    { id: "svc-pcp",     serviceCategory: "PrimaryCare",    description: "Primary care office visit",         cptCodes: ["99213","99214","99215"],   inNetworkCopay: copayPCP, outNetworkCopay: copayPCP*2, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: deductAppliesPCP, priorAuthRequired: false },
    { id: "svc-spec",    serviceCategory: "Specialist",     description: "Specialist office visit",           cptCodes: ["99243","99244","99245"],   inNetworkCopay: copaySpec, outNetworkCopay: copaySpec*2, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: false },
    { id: "svc-er",      serviceCategory: "Emergency",      description: "Emergency room visit",              cptCodes: ["99283","99284","99285"],   inNetworkCopay: erCopay, outNetworkCopay: erCopay, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsIn, deductibleApplies: true, priorAuthRequired: false },
    { id: "svc-inpt",    serviceCategory: "Inpatient",      description: "Inpatient hospital stay",           cptCodes: ["99221","99222","99223"],   inNetworkCopay: 0, outNetworkCopay: 0, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: true },
    { id: "svc-outpt",   serviceCategory: "OutpatientSurg", description: "Outpatient surgery",                cptCodes: ["27447","29881","49505"],   inNetworkCopay: 0, outNetworkCopay: 0, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: true },
    { id: "svc-img",     serviceCategory: "Imaging",        description: "Advanced imaging (MRI/CT/PET)",     cptCodes: ["70553","71260","73721"],   inNetworkCopay: 0, outNetworkCopay: 0, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: true },
    { id: "svc-lab",     serviceCategory: "Laboratory",     description: "Laboratory / pathology services",   cptCodes: ["80053","85025","88305"],   inNetworkCopay: 0, outNetworkCopay: 0, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: false },
    { id: "svc-pt",      serviceCategory: "PhysicalTherapy",description: "Physical therapy",                  cptCodes: ["97110","97140","97530"],   inNetworkCopay: copayPCP, outNetworkCopay: copayPCP*2, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: true },
    { id: "svc-mh",      serviceCategory: "MentalHealth",   description: "Outpatient mental health",          cptCodes: ["90834","90837","90847"],   inNetworkCopay: copayPCP, outNetworkCopay: copayPCP*2, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: false, priorAuthRequired: false },
    { id: "svc-prev",    serviceCategory: "Preventive",     description: "Preventive care / wellness visit",  cptCodes: ["99385","99395","99396"],   inNetworkCopay: 0, outNetworkCopay: 0, inNetworkCoinsurance: 0, outNetworkCoinsurance: 0, deductibleApplies: false, priorAuthRequired: false },
    { id: "svc-rx-gen",  serviceCategory: "PharmacyGeneric", description: "Generic drugs (Tier 1)",           cptCodes: [],                          inNetworkCopay: 10, outNetworkCopay: 20, inNetworkCoinsurance: 0, outNetworkCoinsurance: 0, deductibleApplies: false, priorAuthRequired: false },
    { id: "svc-dme",     serviceCategory: "DME",            description: "Durable medical equipment",         cptCodes: ["E0601","E0260","L3670"],   inNetworkCopay: 0, outNetworkCopay: 0, inNetworkCoinsurance: coinsIn, outNetworkCoinsurance: coinsOut, deductibleApplies: true, priorAuthRequired: true },
  ];
}

const benefitPlansData = [
  { idx: 1, name: "Gold PPO",     type: "PPO",  metal: "Gold",     lob: "Commercial", indDed: 500,  famDed: 1500, indOop: 3000, famOop: 9000,  copayPCP: 30, copaySpec: 50, coinsIn: 20, coinsOut: 40, erCopay: 250, dedPCP: false },
  { idx: 2, name: "Silver PPO",   type: "PPO",  metal: "Silver",   lob: "Commercial", indDed: 1000, famDed: 2500, indOop: 5000, famOop: 10000, copayPCP: 40, copaySpec: 65, coinsIn: 30, coinsOut: 50, erCopay: 350, dedPCP: false },
  { idx: 3, name: "Bronze HDHP",  type: "HDHP", metal: "Bronze",   lob: "Commercial", indDed: 3000, famDed: 6000, indOop: 7000, famOop: 14000, copayPCP: 0,  copaySpec: 0,  coinsIn: 20, coinsOut: 40, erCopay: 0,   dedPCP: true },
  { idx: 4, name: "Platinum HMO", type: "HMO",  metal: "Platinum", lob: "Commercial", indDed: 250,  famDed: 500,  indOop: 2000, famOop: 4000,  copayPCP: 20, copaySpec: 40, coinsIn: 10, coinsOut: 50, erCopay: 150, dedPCP: false },
  { idx: 5, name: "Medicaid MCO", type: "MCO",  metal: "N/A",      lob: "Medicaid",   indDed: 0,    famDed: 0,    indOop: 250,  famOop: 500,   copayPCP: 3,  copaySpec: 5,  coinsIn: 0,  coinsOut: 0,  erCopay: 8,   dedPCP: false },
];

const benefitPlans = benefitPlansData.map(function (p) {
  return {
    _id: makeId("plan", p.idx),
    tenantId: TENANT_ID,
    planId: makeId("plan", p.idx),
    planName: p.name,
    payer: "CloudHealthOffice Demo Payer",
    effectiveDate: new Date("2025-01-01"),
    terminationDate: null,
    planType: p.type,
    metalLevel: p.metal,
    lineOfBusiness: p.lob,
    costSharing: {
      individualDeductible: p.indDed,
      familyDeductible: p.famDed,
      individualOutOfPocketMax: p.indOop,
      familyOutOfPocketMax: p.famOop,
      inNetworkDeductible: p.indDed,
      outOfNetworkDeductible: p.indDed * 2,
      inNetworkOutOfPocketMax: p.indOop,
      outOfNetworkOutOfPocketMax: p.indOop * 2
    },
    benefits: makePlanBenefits(p.copayPCP, p.copaySpec, p.coinsIn, p.coinsOut, p.erCopay, p.dedPCP),
    networkTiers: ["Tier1", "Tier2"],
    isActive: true,
    createdDate: now,
    modifiedDate: now,
    createdBy: "seed-script"
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
    tenantId: TENANT_ID,
    groupNumber: s.grp,
    employerName: s.name,
    taxId: "74-" + String(3200000 + s.idx * 111111).substring(0, 7),
    address: (s.idx * 200) + " " + pick(["Congress Ave","Main St","Commerce St","Lamar Blvd","Guadalupe St"]) + ", Suite " + (s.idx * 100),
    city: s.city,
    state: s.state,
    zipCode: s.zip,
    contactName: pick(["Patricia","Robert","Linda","James","Maria"]) + " " + pick(["Garza","Tran","Davis","Park","Rivera"]),
    contactPhone: "512" + String(2000000 + s.idx * 111111),
    contactEmail: "benefits@sponsor-" + s.idx + ".test",
    effectiveDate: new Date("2025-01-01"),
    terminationDate: null,
    status: "Active",
    lineOfBusiness: s.idx === 5 ? "Medicaid" : "Commercial",
    groupSizeTier: s.tier,
    billingInfo: {
      premiumAmount: s.premium,
      frequency: "Monthly",
      billingDay: s.idx <= 2 ? 1 : 15,
      billingAccountNumber: s.grp + "-BA-001",
      paymentMethod: s.idx === 5 ? "Wire" : "ACH",
      gracePeriodDays: 30
    },
    benefitPlanIds: s.plans.map(function (p) { return makeId("plan", p); }),
    totalMembers: 0,
    totalDependents: 0,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
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
    tenantId: TENANT_ID,
    npi: npi,
    providerType: "Individual",
    firstName: p.first,
    lastName: p.last,
    middleName: String.fromCharCode(65 + (p.idx % 26)),
    credentials: p.cred,
    organizationName: null,
    primarySpecialty: p.spec,
    taxonomyCode: p.tax,
    secondarySpecialties: [],
    address: (1000 + p.idx * 111) + " " + streets[p.idx % streets.length] + ", Suite " + (100 + p.idx * 10),
    city: cities[p.idx % cities.length],
    state: "TX",
    zipCode: "78" + String(700 + p.idx).substring(0, 3),
    phone: "512" + String(9870000 + p.idx * 1111),
    fax: "512" + String(9870001 + p.idx * 1111),
    email: "provider-" + String(p.idx).padStart(3, "0") + "@demo-clinic.test",
    networkParticipations: [{
      planId: makeId("plan", 1),
      lineOfBusiness: "Commercial",
      networkTier: "Tier1",
      effectiveDate: new Date("2025-01-01"),
      terminationDate: null,
      acceptingNewPatients: true
    }],
    credentialingStatus: "Approved",
    credentialingDate: daysAgo(randInt(180, 730)),
    recredentialingDueDate: daysFromNow(randInt(365, 1095)),
    boardCertifications: p.cred === "MD" || p.cred === "DO" ? [{ boardName: "AB" + p.spec.substring(0, 4).toUpperCase(), certificationDate: daysAgo(randInt(365, 3650)), expirationDate: daysFromNow(randInt(365, 3650)) }] : [],
    hospitalAffiliations: p.idx <= 7 ? [{ hospitalName: "St. David's Medical Center", npi: makeNpi(16), privilegeStatus: "Active" }] : [],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: p.langs,
    contractedRate: { feeScheduleTier: "Tier1", reimbursementMethod: p.idx <= 2 ? "Capitation" : "FeeSchedule", capitationRate: p.idx <= 2 ? 42.50 : null },
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  });
});

orgProviders.forEach(function (p) {
  const npi = makeNpi(p.idx);
  const isOon = p.idx >= 21 && p.idx <= 25; // last 5 orgs are OON
  providers.push({
    _id: makeId("prov", p.idx),
    tenantId: TENANT_ID,
    npi: npi,
    providerType: "Organization",
    firstName: null,
    lastName: null,
    middleName: null,
    credentials: null,
    organizationName: p.name,
    dbaName: p.dba,
    primarySpecialty: p.spec,
    taxonomyCode: p.tax,
    secondarySpecialties: [],
    address: (2000 + p.idx * 100) + " " + streets[p.idx % streets.length],
    city: cities[p.idx % cities.length],
    state: "TX",
    zipCode: "78" + String(700 + p.idx).substring(0, 3),
    phone: "512" + String(5550000 + p.idx * 1111),
    fax: "512" + String(5550001 + p.idx * 1111),
    email: "facility-" + String(p.idx).padStart(3, "0") + "@demo-clinic.test",
    networkParticipations: isOon ? [] : [{
      planId: makeId("plan", 1),
      lineOfBusiness: "Commercial",
      networkTier: "Tier1",
      effectiveDate: new Date("2025-01-01"),
      terminationDate: null,
      acceptingNewPatients: true
    }],
    credentialingStatus: isOon ? "Pending" : "Approved",
    credentialingDate: isOon ? null : daysAgo(randInt(180, 730)),
    recredentialingDueDate: isOon ? null : daysFromNow(randInt(365, 1095)),
    boardCertifications: [],
    hospitalAffiliations: [],
    acceptingNewPatients: !isOon,
    handicapAccessible: true,
    languagesSpoken: ["English", "Spanish"],
    contractedRate: isOon ? null : { feeScheduleTier: p.spec === "Hospital" ? "Tier1-Facility" : "Tier2", reimbursementMethod: "FeeSchedule" },
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  });
});

choDb.Providers.insertMany(providers);
print("✓ " + providers.length + " Providers");

const providerNameByIdx = {};
providers.forEach(function (p, i) {
  const idx = i + 1;
  providerNameByIdx[idx] = p.organizationName ? p.organizationName : (p.firstName + " " + p.lastName + ", " + p.credentials);
});
const providerNpiByIdx = {};
providers.forEach(function (p, i) { providerNpiByIdx[i + 1] = p.npi; });

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

const memberStatuses = [];
for (let i = 0; i < 40; i++) memberStatuses.push("Active");
for (let i = 0; i < 5; i++) memberStatuses.push("COBRA");
for (let i = 0; i < 3; i++) memberStatuses.push("Terminated");
for (let i = 0; i < 2; i++) memberStatuses.push("Pending");

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

  const hasPcp = i <= 35 && status === "Active"; // 35 subscribers with PCP

  members.push({
    _id: memberId,
    tenantId: TENANT_ID,
    memberId: memberId,
    subscriberId: subscriberId,
    ssn: "***-**-" + String(1000 + i),
    groupNumber: sponsorData.grp,
    isSubscriber: isSub,
    subscriberMemberId: isSub ? null : makeId("mbr", subIdx),
    relationshipCode: relCode,
    firstName: first,
    lastName: last,
    middleName: null,
    dateOfBirth: dateOfBirth(age),
    gender: gender === "M" ? "M" : "F",
    address: (1000 + i * 37) + " " + pick(["Barton Springs Rd","Oak Lawn Ave","Westheimer Rd","Alamo Plaza","Commerce St","S Congress Ave","Montana Ave","Legacy Dr","Jollyville Rd","Randol Mill Rd"]),
    city: pick(["Austin","Austin","Austin","Round Rock","Cedar Park","Houston","San Antonio","Dallas","Pflugerville","Georgetown"]),
    state: "TX",
    zipCode: "78" + String(700 + (i % 100)).substring(0, 3),
    phone: "512" + String(2000000 + i * 1111),
    email: (first.toLowerCase() + "." + last.toLowerCase().replace(/'/g, "") + "@example.com"),
    effectiveDate: new Date("2025-01-01"),
    terminationDate: status === "Terminated" ? daysAgo(randInt(10, 60)) : null,
    status: status,
    lineOfBusiness: planIdx === 5 ? "Medicaid" : "Commercial",
    benefitPlanId: makeId("plan", planIdx),
    sponsorId: makeId("spon", sponsorIdx),
    employmentStatus: status === "Terminated" ? "Terminated" : (status === "COBRA" ? "Terminated" : "FullTime"),
    tobaccoUser: seededRandom() < 0.12,
    isStudent: age >= 18 && age <= 25 && seededRandom() < 0.3,
    pcpProviderId: hasPcp ? makeId("prov", pcpProviders[i % pcpProviders.length]) : null,
    pcpProviderName: hasPcp ? providerNameByIdx[pcpProviders[i % pcpProviders.length]] : null,
    pcpAssignedDate: hasPcp ? daysAgo(randInt(30, 365)) : null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  });
}

// Update sponsor member counts
sponsorsData.forEach(function (s) {
  const count = members.filter(function (m) { return m.sponsorId === makeId("spon", s.idx); }).length;
  s.members = count;
});

choDb.Members.insertMany(members);
print("✓ " + members.length + " Members");

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
  memberAccumData[m.memberId] = { deductible: 0, copay: 0, coinsurance: 0, planPaid: 0, ptVisits: 0, mhVisits: 0 };
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
  const pos = posMap[selectedCpts[0].cat] || "11";

  // Build service lines
  const lineItems = selectedCpts.map(function (cpt, li) {
    const chargedAmount = roundMoney(cpt.charge[0] + seededRandom() * (cpt.charge[1] - cpt.charge[0]));
    return {
      lineNumber: li + 1,
      procedureCode: cpt.code,
      procedureDescription: cpt.desc,
      modifiers: [],
      chargedAmount: chargedAmount,
      units: cpt.cat === "PhysicalTherapy" ? randInt(1, 4) : 1,
      placeOfServiceCode: posMap[cpt.cat] || "11",
      serviceDateFrom: serviceDate,
      serviceDateTo: serviceDate,
      diagnosisCodePointers: [1, 2]
    };
  });

  const totalCharged = roundMoney(lineItems.reduce(function (s, l) { return s + l.chargedAmount; }, 0));

  const claim = {
    _id: claimId,
    tenantId: TENANT_ID,
    claimNumber: "CLM-2026-" + String(c).padStart(5, "0"),
    memberId: member.memberId,
    subscriberId: member.subscriberId,
    benefitPlanId: member.benefitPlanId,
    coverageId: null,
    subscriberFirstName: member.firstName,
    subscriberLastName: member.lastName,
    patientFirstName: member.firstName,
    patientLastName: member.lastName,
    patientRelationship: member.relationshipCode,
    lineOfBusiness: member.lineOfBusiness,
    billingProviderNPI: provNpi,
    billingProviderName: provName,
    renderingProviderNPI: provNpi,
    renderingProviderName: provName,
    facilityNPI: claimType === "Institutional" ? providerNpiByIdx[pick([16, 17, 18])] : null,
    facilityName: claimType === "Institutional" ? providerNameByIdx[pick([16, 17, 18])] : null,
    placeOfServiceCode: pos,
    serviceDateFrom: serviceDate,
    serviceDateTo: serviceDate,
    receivedDate: receivedDate,
    status: statusSlot === "Adjudicated" ? "Paid" : (statusSlot === "WorkQueue" ? "Pended" : statusSlot),
    claimType: claimType,
    totalChargedAmount: totalCharged,
    diagnosisCodes: [
      { code: primaryDiag.code, codeQualifier: "BK", description: primaryDiag.desc },
      { code: secondaryDiag.code, codeQualifier: "BF", description: secondaryDiag.desc },
    ],
    lineItems: lineItems,
    adjudication: null,
    denialReasonCode: null,
    denialReason: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  };

  // Adjudicate paid claims
  if (statusSlot === "Adjudicated") {
    const plan = planCostSharing[member.benefitPlanId];
    const allowedAmount = roundMoney(totalCharged * (0.7 + seededRandom() * 0.15));
    const deductibleAmount = plan ? roundMoney(Math.min(allowedAmount * 0.1, plan.indDed * 0.05)) : 0;
    const copayAmount = plan ? plan.copayPCP : 30;
    const coinsuranceAmount = plan ? roundMoney((allowedAmount - deductibleAmount - copayAmount) * (plan.coinsIn / 100)) : 0;
    const memberResp = roundMoney(deductibleAmount + copayAmount + coinsuranceAmount);
    const planPaid = roundMoney(Math.max(0, allowedAmount - memberResp));

    claim.adjudication = {
      adjudicatedDate: new Date(serviceDate.getTime() + randInt(3, 14) * 86400000),
      allowedAmount: allowedAmount,
      planPaid: planPaid,
      memberResponsibility: memberResp,
      copayAmount: copayAmount,
      coinsuranceAmount: Math.max(0, coinsuranceAmount),
      deductibleAmount: deductibleAmount,
      adjustmentReasonCode: "CO-45",
      adjustmentAmount: roundMoney(totalCharged - allowedAmount),
      checkNumber: "EFT-" + String(900000 + c),
      paidDate: new Date(serviceDate.getTime() + randInt(14, 30) * 86400000)
    };

    // Track accumulators
    const acc = memberAccumData[member.memberId];
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
    claim.denialReasonCode = denial.code;
    claim.denialReason = denial.reason;
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
      tenantId: TENANT_ID,
      authorizationNumber: "AUTH-2026-" + String(authIdx).padStart(5, "0"),
      memberId: member.memberId,
      coverageId: null,
      patientFirstName: member.firstName,
      patientLastName: member.lastName,
      patientDateOfBirth: member.dateOfBirth,
      lineOfBusiness: member.lineOfBusiness,
      requestingProviderNPI: providerNpiByIdx[provIdx],
      requestingProviderName: providerNameByIdx[provIdx],
      servicingProviderNPI: providerNpiByIdx[provIdx],
      servicingProviderName: providerNameByIdx[provIdx],
      facilityNPI: svc.type === "Inpatient" ? providerNpiByIdx[16] : null,
      facilityName: svc.type === "Inpatient" ? providerNameByIdx[16] : null,
      authorizationType: svc.type === "Specialist Referral" ? "Referral" : "PreAuthorization",
      certificationTypeCode: svc.type === "Inpatient" ? "I" : "S",
      serviceTypeCode: "02",
      levelOfService: "U",
      requestedServiceDateFrom: new Date(submitDate.getTime() + 7 * 86400000),
      requestedServiceDateTo: new Date(submitDate.getTime() + (svc.type === "Physical Therapy" ? 90 : 30) * 86400000),
      diagnosisCodes: [{ code: diag.code, codeQualifier: "BK", description: diag.desc }],
      requestedServices: [{
        procedureCode: pick(svc.codes),
        procedureDescription: svc.type,
        modifiers: [],
        requestedUnits: reqUnits,
        approvedUnits: appUnits,
        unitType: svc.type === "Inpatient" ? "Day" : "Visit",
        placeOfServiceCode: svc.type === "Inpatient" ? "21" : "11",
        serviceStatus: status === "InReview" ? "Pending" : status
      }],
      status: status,
      reviewDecision: status === "Approved" ? "A1" : (status === "Denied" ? "A4" : (status === "Modified" ? "A2" : null)),
      approvedUnits: appUnits,
      approvedServiceDateFrom: status === "Approved" || status === "Modified" ? new Date(submitDate.getTime() + 7 * 86400000) : null,
      approvedServiceDateTo: status === "Approved" || status === "Modified" ? new Date(submitDate.getTime() + 90 * 86400000) : null,
      denialReasonCode: status === "Denied" ? "NOT_MEDICALLY_NECESSARY" : null,
      denialReason: status === "Denied" ? "Clinical documentation does not support medical necessity for requested service." : null,
      reviewerName: status !== "InReview" ? pick(["Dr. Sarah Williams", "Dr. Mark Torres", "Dr. Lisa Chen"]) : null,
      submittedDate: submitDate,
      reviewedDate: status !== "InReview" ? new Date(submitDate.getTime() + randInt(1, 5) * 86400000) : null,
      expirationDate: status === "Expired" ? daysAgo(randInt(1, 15)) : (status === "Approved" ? daysFromNow(randInt(30, 90)) : null),
      notes: "",
      createdDate: now,
      lastUpdatedDate: now,
      createdBy: "seed-script",
      lastUpdatedBy: "seed-script"
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
      tenantId: TENANT_ID,
      claimId: claimRef,
      claimNumber: refClaim ? refClaim.claimNumber : "CLM-2026-" + String(wqIdx).padStart(5, "0"),
      memberName: refClaim ? (refClaim.patientFirstName + " " + refClaim.patientLastName) : "Unknown",
      memberId: refClaim ? refClaim.memberId : makeId("mbr", 1),
      providerName: refClaim ? refClaim.billingProviderName : "Unknown Provider",
      serviceDate: refClaim ? refClaim.serviceDateFrom : daysAgo(randInt(1, 14)),
      queueReason: wq.reason,
      queueReasonCode: wq.code,
      daysInQueue: randInt(0, 14),
      priority: priorities[wqIdx % priorities.length],
      assignedTo: examiners[wqIdx % examiners.length],
      totalCharged: refClaim ? refClaim.totalChargedAmount : 1000,
      procedureCodes: refClaim ? refClaim.lineItems.map(function (l) { return l.procedureCode; }) : ["99213"],
      createdDate: daysAgo(randInt(0, 14)),
      lastUpdatedDate: now,
      createdBy: "seed-script"
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
const payableClaims = claims.filter(function (cl) { return cl.adjudication && cl.adjudication.planPaid > 0; });
const claimsToUse = payableClaims.slice(0, 80);

claimsToUse.forEach(function (cl, i) {
  const payIdx = i + 1;
  const run = paymentRuns[i % paymentRuns.length];
  payments.push({
    _id: makeId("pmt", payIdx),
    tenantId: TENANT_ID,
    paymentId: makeId("pmt", payIdx),
    paymentRunId: "PMTRUN-2026-" + String(run.runIdx).padStart(4, "0"),
    paymentRunName: run.name,
    claimId: cl._id,
    claimNumber: cl.claimNumber,
    memberId: cl.memberId,
    memberName: cl.patientFirstName + " " + cl.patientLastName,
    providerNpi: cl.billingProviderNPI,
    providerName: cl.billingProviderName,
    paymentMethod: i % 5 === 0 ? "Check" : "EFT",
    checkEftNumber: (i % 5 === 0 ? "CHK-" : "EFT-") + String(100000 + payIdx),
    paymentDate: run.date,
    chargeAmount: cl.totalChargedAmount,
    allowedAmount: cl.adjudication.allowedAmount,
    paidAmount: cl.adjudication.planPaid,
    memberResponsibility: cl.adjudication.memberResponsibility,
    status: "Completed",
    createdDate: now,
    createdBy: "seed-script"
  });
});

choDb.Payments.insertMany(payments);
print("✓ " + payments.length + " Payments");

// ═══════════════════════════════════════════════════════════════════════════
// 9. ACCUMULATORS (50) — consistent with adjudicated claims
// ═══════════════════════════════════════════════════════════════════════════

const accumulators = members.map(function (m) {
  const acc = memberAccumData[m.memberId];
  const plan = planCostSharing[m.benefitPlanId];
  const indDedLimit = plan ? plan.indDed : 1500;
  const famDedLimit = plan ? plan.famDed : 3000;
  const indOopLimit = plan ? plan.indOop : 5000;
  const famOopLimit = plan ? plan.famOop : 10000;

  return {
    _id: makeId("acc", members.indexOf(m) + 1),
    tenantId: TENANT_ID,
    memberId: m.memberId,
    memberName: m.firstName + " " + m.lastName,
    benefitPlanId: m.benefitPlanId,
    planYear: "2026",
    individualDeductibleUsed: roundMoney(acc.deductible),
    individualDeductibleLimit: indDedLimit,
    familyDeductibleUsed: roundMoney(acc.deductible * 1.5),
    familyDeductibleLimit: famDedLimit,
    individualOopUsed: roundMoney(acc.deductible + acc.copay + acc.coinsurance),
    individualOopLimit: indOopLimit,
    familyOopUsed: roundMoney((acc.deductible + acc.copay + acc.coinsurance) * 1.5),
    familyOopLimit: famOopLimit,
    serviceAccumulators: [
      { serviceType: "Physical Therapy", used: acc.ptVisits, limit: 20, unitType: "visits" },
      { serviceType: "Mental Health Outpatient", used: acc.mhVisits, limit: 30, unitType: "visits" },
      { serviceType: "Skilled Nursing", used: 0, limit: 60, unitType: "days" },
    ],
    lastUpdatedDate: now,
    createdBy: "seed-script"
  };
});

choDb.Accumulators.insertMany(accumulators);
print("✓ " + accumulators.length + " Accumulators");

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
    tenantId: TENANT_ID,
    appealId: "APL-2026-" + String(i).padStart(4, "0"),
    memberName: member.firstName + " " + member.lastName,
    memberId: member.memberId,
    appealType: type,
    originalDecisionId: origId,
    originalDecision: "Denied",
    originalDenialReason: pick(denialReasons).reason,
    status: status,
    isExpedited: i <= 2,
    filedDate: filedDate,
    dueDate: dueDate,
    daysRemaining: Math.round((dueDate - TODAY) / 86400000),
    assignedReviewer: status !== "Open" ? pick(["Dr. Mark Torres", "Dr. Sarah Williams", "Medical Director"]) : "",
    complianceStatus: status === "Escalated" ? "At Risk" : "On Track",
    finalDecision: status === "Decision Made" ? (i % 2 === 0 ? "Overturned" : "Upheld") : null,
    decisionDate: status === "Decision Made" ? daysAgo(randInt(1, 5)) : null,
    createdDate: filedDate,
    lastUpdatedDate: now,
    createdBy: "seed-script"
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
    tenantId: TENANT_ID,
    letterId: "LTR-2026-" + String(5000 + i).padStart(5, "0"),
    letterType: type,
    recipientName: type === "RFAI" ? providerNameByIdx[pick(inNetworkIndProviders)] : (member.firstName + " " + member.lastName),
    recipientType: type === "RFAI" ? "Provider" : "Member",
    relatedId: relatedId,
    generatedDate: status === "Queued" ? null : daysAgo(randInt(0, 7)),
    status: status,
    deliveryMethod: pick(["Mail", "Fax", "Email", "Portal"]),
    createdDate: now,
    createdBy: "seed-script"
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
    tenantId: TENANT_ID,
    fileId: "834-" + receivedTime.toISOString().substring(0, 10).replace(/-/g, "") + "-" + String(i).padStart(3, "0"),
    fileName: sponsorData.name.substring(0, 3).toUpperCase() + "_834_" + receivedTime.toISOString().substring(0, 10).replace(/-/g, "") + "_0" + i + "00.edi",
    receivedTime: receivedTime,
    sponsorName: sponsorData.name,
    sponsorId: makeId("spon", (i - 1) % sponsorsData.length + 1),
    groupNumber: sponsorData.grp,
    transactionCount: totalRecords,
    addedCount: randInt(3, Math.floor(totalRecords * 0.2)),
    termedCount: randInt(1, Math.floor(totalRecords * 0.1)),
    changedCount: totalRecords - rejectedCount - randInt(5, 20),
    rejectedCount: rejectedCount,
    status: hasErrors ? "PartiallyAccepted" : (i === 8 ? "Failed" : "Completed"),
    rejections: hasErrors ? [
      { memberId: "MBR-ERR-001", memberName: "Garcia, Roberto", errorCode: "834-E003", errorDescription: "Invalid date of birth format" },
      { memberId: "MBR-ERR-002", memberName: "Petrov, Natasha", errorCode: "834-E007", errorDescription: "Subscriber not found in active roster" },
      { memberId: "MBR-ERR-003", memberName: "Chen, Mei-Lin",   errorCode: "834-E015", errorDescription: "Coverage date gap detected (14 days)" },
    ] : [],
    errorMessage: i === 8 ? "SFTP connection timeout after 30s — file transfer incomplete" : null,
    createdDate: receivedTime,
    createdBy: "seed-script"
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
