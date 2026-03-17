// seed-demo-data.js
// MongoDB seed script that populates realistic healthcare demo data for a
// given tenant in the CloudHealthOffice database.
//
// WARNING: This script REPLACES any existing data for the specified tenant
// in the seeded collections. Back up first if you need to preserve data.
//
// Usage:
//   mongosh <connection-string> scripts/setup/seed-demo-data.js \
//     --eval 'var tenantId="my-tenant", groupNumber="GRP-001-2026", sponsor1="Acme Employees Association", sponsor2="Regional Health Cooperative"'
//
// Required --eval variables:
//   tenantId   — tenant identifier written to every document
//   groupNumber — primary group number for the main sponsor
//
// Optional --eval variables:
//   sponsor1   — name for the large-group sponsor  (default: "Metro Employees Association")
//   sponsor2   — name for the small-group sponsor  (default: "Regional Health Cooperative")

// Validate required parameters
if (typeof tenantId === "undefined" || typeof groupNumber === "undefined") {
  print("ERROR: Missing required parameters.");
  print("");
  print("Usage:");
  print('  mongosh <connection-string> seed-demo-data.js \\');
  print('    --eval \'var tenantId="my-tenant", groupNumber="GRP-001-2026"\'');
  print("");
  print("Optional: sponsor1, sponsor2 (display names for the two sponsor orgs)");
  quit(1);
}

const TENANT_ID = tenantId;
const GROUP_NUMBER = groupNumber;
const SPONSOR1_NAME = (typeof sponsor1 !== "undefined") ? sponsor1 : "Metro Employees Association";
const SPONSOR2_NAME = (typeof sponsor2 !== "undefined") ? sponsor2 : "Regional Health Cooperative";

const choDb = db.getSiblingDB("CloudHealthOffice");
const now = new Date();

// ---------------------------------------------------------------------------
// Helper: deterministic UUID-like IDs for cross-referencing
// ---------------------------------------------------------------------------
function makeId(prefix, n) {
  const pad = String(n).padStart(4, "0");
  return `${prefix}-${TENANT_ID.substring(0, 8)}-${pad}`;
}

// Helper: random date within last N days
function recentDate(maxDaysAgo) {
  const daysAgo = Math.floor(Math.random() * maxDaysAgo);
  const d = new Date(now);
  d.setDate(d.getDate() - daysAgo);
  return d;
}

// ---------------------------------------------------------------------------
// Clean existing tenant data from all target collections
// ---------------------------------------------------------------------------
const collections = [
  "Members", "Claims", "Providers", "BenefitPlans",
  "Authorizations", "Sponsors"
];
collections.forEach(function (col) {
  const r = choDb[col].deleteMany({ tenantId: TENANT_ID });
  print("Cleared " + r.deletedCount + " existing doc(s) from " + col);
});

// =========================================================================
// 1. BENEFIT PLANS (3)
// =========================================================================
const benefitPlans = [
  {
    _id: makeId("plan", 1),
    tenantId: TENANT_ID,
    planId: makeId("plan", 1),
    planName: "Gold PPO",
    payer: "CloudHealthOffice Demo Payer",
    effectiveDate: new Date("2026-01-01"),
    terminationDate: null,
    planType: "PPO",
    metalLevel: "Gold",
    lineOfBusiness: "Commercial",
    costSharing: {
      individualDeductible: 500,
      familyDeductible: 1500,
      individualOutOfPocketMax: 3000,
      familyOutOfPocketMax: 9000,
      inNetworkDeductible: 500,
      outOfNetworkDeductible: 1500,
      inNetworkOutOfPocketMax: 3000,
      outOfNetworkOutOfPocketMax: 9000
    },
    benefits: [
      {
        id: "ben-pcp-1",
        serviceCategory: "PrimaryCare",
        description: "Primary care office visit",
        cptCodes: ["99213", "99214", "99215"],
        inNetworkCopay: 30,
        outNetworkCopay: 60,
        inNetworkCoinsurance: 20,
        outNetworkCoinsurance: 40,
        deductibleApplies: false,
        priorAuthRequired: false
      },
      {
        id: "ben-er-1",
        serviceCategory: "Emergency",
        description: "Emergency room visit",
        cptCodes: ["99283"],
        inNetworkCopay: 250,
        outNetworkCopay: 250,
        inNetworkCoinsurance: 20,
        outNetworkCoinsurance: 20,
        deductibleApplies: true,
        priorAuthRequired: false
      }
    ],
    networkTiers: [],
    isActive: true,
    createdDate: now,
    modifiedDate: now,
    createdBy: "seed-script"
  },
  {
    _id: makeId("plan", 2),
    tenantId: TENANT_ID,
    planId: makeId("plan", 2),
    planName: "Silver HMO",
    payer: "CloudHealthOffice Demo Payer",
    effectiveDate: new Date("2026-01-01"),
    terminationDate: null,
    planType: "HMO",
    metalLevel: "Silver",
    lineOfBusiness: "Commercial",
    costSharing: {
      individualDeductible: 1500,
      familyDeductible: 3000,
      individualOutOfPocketMax: 6000,
      familyOutOfPocketMax: 12000,
      inNetworkDeductible: 1500,
      outOfNetworkDeductible: 3000,
      inNetworkOutOfPocketMax: 6000,
      outOfNetworkOutOfPocketMax: 12000
    },
    benefits: [
      {
        id: "ben-pcp-2",
        serviceCategory: "PrimaryCare",
        description: "Primary care office visit",
        cptCodes: ["99213", "99214", "99215"],
        inNetworkCopay: 40,
        outNetworkCopay: 80,
        inNetworkCoinsurance: 30,
        outNetworkCoinsurance: 50,
        deductibleApplies: false,
        priorAuthRequired: false
      },
      {
        id: "ben-er-2",
        serviceCategory: "Emergency",
        description: "Emergency room visit",
        cptCodes: ["99283"],
        inNetworkCopay: 350,
        outNetworkCopay: 350,
        inNetworkCoinsurance: 30,
        outNetworkCoinsurance: 30,
        deductibleApplies: true,
        priorAuthRequired: false
      }
    ],
    networkTiers: [],
    isActive: true,
    createdDate: now,
    modifiedDate: now,
    createdBy: "seed-script"
  },
  {
    _id: makeId("plan", 3),
    tenantId: TENANT_ID,
    planId: makeId("plan", 3),
    planName: "Bronze HDHP",
    payer: "CloudHealthOffice Demo Payer",
    effectiveDate: new Date("2026-01-01"),
    terminationDate: null,
    planType: "HDHP",
    metalLevel: "Bronze",
    lineOfBusiness: "Commercial",
    costSharing: {
      individualDeductible: 3000,
      familyDeductible: 6000,
      individualOutOfPocketMax: 7000,
      familyOutOfPocketMax: 14000,
      inNetworkDeductible: 3000,
      outOfNetworkDeductible: 6000,
      inNetworkOutOfPocketMax: 7000,
      outOfNetworkOutOfPocketMax: 14000
    },
    benefits: [
      {
        id: "ben-pcp-3",
        serviceCategory: "PrimaryCare",
        description: "Primary care office visit (after deductible)",
        cptCodes: ["99213", "99214", "99215"],
        inNetworkCopay: 0,
        outNetworkCopay: 0,
        inNetworkCoinsurance: 20,
        outNetworkCoinsurance: 40,
        deductibleApplies: true,
        priorAuthRequired: false
      },
      {
        id: "ben-hsa-3",
        serviceCategory: "Preventive",
        description: "HSA-eligible plan — preventive care covered at 100%",
        cptCodes: [],
        inNetworkCopay: 0,
        outNetworkCopay: 0,
        inNetworkCoinsurance: 0,
        outNetworkCoinsurance: 0,
        deductibleApplies: false,
        priorAuthRequired: false
      }
    ],
    networkTiers: [],
    isActive: true,
    createdDate: now,
    modifiedDate: now,
    createdBy: "seed-script"
  }
];

choDb.BenefitPlans.insertMany(benefitPlans);
print("✓ Inserted " + benefitPlans.length + " benefit plans");

// =========================================================================
// 2. SPONSORS (2)
// =========================================================================
const sponsors = [
  {
    _id: makeId("spon", 1),
    tenantId: TENANT_ID,
    groupNumber: GROUP_NUMBER,
    employerName: SPONSOR1_NAME,
    taxId: "74-3218956",
    address: "200 Congress Ave, Suite 400",
    city: "Austin",
    state: "TX",
    zipCode: "78701",
    contactName: "Patricia Garza",
    contactPhone: "5122345678",
    contactEmail: "pgarza@sponsor1.example.com",
    effectiveDate: new Date("2025-01-01"),
    terminationDate: null,
    status: "Active",
    lineOfBusiness: "Commercial",
    billingInfo: {
      premiumAmount: 487500.00,
      frequency: "Monthly",
      billingDay: 1,
      billingAccountNumber: GROUP_NUMBER + "-BA-001",
      paymentMethod: "ACH",
      gracePeriodDays: 30
    },
    benefitPlanIds: [makeId("plan", 1), makeId("plan", 2), makeId("plan", 3)],
    totalMembers: 8,
    totalDependents: 0,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("spon", 2),
    tenantId: TENANT_ID,
    groupNumber: GROUP_NUMBER + "-SG",
    employerName: SPONSOR2_NAME,
    taxId: "74-6543210",
    address: "500 Main Plaza",
    city: "San Antonio",
    state: "TX",
    zipCode: "78205",
    contactName: "Robert Tran",
    contactPhone: "2108765432",
    contactEmail: "rtran@sponsor2.example.com",
    effectiveDate: new Date("2025-07-01"),
    terminationDate: null,
    status: "Active",
    lineOfBusiness: "Commercial",
    billingInfo: {
      premiumAmount: 24000.00,
      frequency: "Monthly",
      billingDay: 15,
      billingAccountNumber: GROUP_NUMBER + "-SG-BA-001",
      paymentMethod: "ACH",
      gracePeriodDays: 30
    },
    benefitPlanIds: [makeId("plan", 2)],
    totalMembers: 2,
    totalDependents: 0,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  }
];

choDb.Sponsors.insertMany(sponsors);
print("✓ Inserted " + sponsors.length + " sponsors");

// =========================================================================
// 3. PROVIDERS (8)
// =========================================================================
const providers = [
  {
    _id: makeId("prov", 1),
    tenantId: TENANT_ID,
    npi: "1234567890",
    providerType: "Individual",
    firstName: "Maria",
    lastName: "Santos",
    middleName: "L",
    credentials: "MD",
    organizationName: null,
    primarySpecialty: "Family Medicine",
    taxonomyCode: "207Q00000X",
    secondarySpecialties: [],
    address: "4521 Medical Dr, Suite 110",
    city: "Austin",
    state: "TX",
    zipCode: "78756",
    phone: "5129876543",
    fax: "5129876544",
    email: "msantos@austinfamilymed.example.com",
    networkParticipations: [
      {
        planId: makeId("plan", 1),
        lineOfBusiness: "Commercial",
        networkTier: "Tier1",
        effectiveDate: new Date("2025-01-01"),
        terminationDate: null,
        acceptingNewPatients: true
      }
    ],
    credentialingStatus: "Approved",
    credentialingDate: new Date("2024-06-15"),
    recredentialingDueDate: new Date("2027-06-15"),
    boardCertifications: [
      { boardName: "ABFM", certificationDate: new Date("2018-08-01"), expirationDate: new Date("2028-08-01") }
    ],
    hospitalAffiliations: [],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: ["English", "Spanish"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 2),
    tenantId: TENANT_ID,
    npi: "2345678901",
    providerType: "Individual",
    firstName: "James",
    lastName: "Chen",
    middleName: "W",
    credentials: "DO",
    organizationName: null,
    primarySpecialty: "Internal Medicine",
    taxonomyCode: "207R00000X",
    secondarySpecialties: [],
    address: "800 W 38th St, Suite 200",
    city: "Austin",
    state: "TX",
    zipCode: "78705",
    phone: "5124567890",
    fax: "5124567891",
    email: "jchen@atxinternal.example.com",
    networkParticipations: [
      {
        planId: makeId("plan", 1),
        lineOfBusiness: "Commercial",
        networkTier: "Tier1",
        effectiveDate: new Date("2025-01-01"),
        terminationDate: null,
        acceptingNewPatients: true
      }
    ],
    credentialingStatus: "Approved",
    credentialingDate: new Date("2024-03-10"),
    recredentialingDueDate: new Date("2027-03-10"),
    boardCertifications: [],
    hospitalAffiliations: [],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: ["English", "Mandarin"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 3),
    tenantId: TENANT_ID,
    npi: "3456789012",
    providerType: "Individual",
    firstName: "Rebecca",
    lastName: "Okafor",
    middleName: "A",
    credentials: "MD",
    organizationName: null,
    primarySpecialty: "Orthopedics",
    taxonomyCode: "207X00000X",
    secondarySpecialties: ["Sports Medicine"],
    address: "1200 S Lamar Blvd, Suite 300",
    city: "Austin",
    state: "TX",
    zipCode: "78704",
    phone: "5123456789",
    fax: "5123456780",
    email: "rokafor@lonestarortho.example.com",
    networkParticipations: [
      {
        planId: makeId("plan", 1),
        lineOfBusiness: "Commercial",
        networkTier: "Tier1",
        effectiveDate: new Date("2025-03-01"),
        terminationDate: null,
        acceptingNewPatients: true
      }
    ],
    credentialingStatus: "Approved",
    credentialingDate: new Date("2025-01-20"),
    recredentialingDueDate: new Date("2028-01-20"),
    boardCertifications: [
      { boardName: "ABOS", certificationDate: new Date("2019-05-01"), expirationDate: new Date("2029-05-01") }
    ],
    hospitalAffiliations: [
      { hospitalName: "St. David's Medical Center", npi: "1111111111", privilegeStatus: "Active" }
    ],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: ["English"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 4),
    tenantId: TENANT_ID,
    npi: "4567890123",
    providerType: "Individual",
    firstName: "David",
    lastName: "Patel",
    middleName: "R",
    credentials: "MD",
    organizationName: null,
    primarySpecialty: "Radiology",
    taxonomyCode: "2085R0202X",
    secondarySpecialties: ["Diagnostic Radiology"],
    address: "6800 N MoPac Expy, Suite 150",
    city: "Austin",
    state: "TX",
    zipCode: "78731",
    phone: "5127654321",
    fax: "5127654322",
    email: "dpatel@atximaging.example.com",
    networkParticipations: [
      {
        planId: makeId("plan", 1),
        lineOfBusiness: "Commercial",
        networkTier: "Tier1",
        effectiveDate: new Date("2025-01-01"),
        terminationDate: null,
        acceptingNewPatients: true
      }
    ],
    credentialingStatus: "Approved",
    credentialingDate: new Date("2024-09-01"),
    recredentialingDueDate: new Date("2027-09-01"),
    boardCertifications: [
      { boardName: "ABR", certificationDate: new Date("2016-11-01"), expirationDate: new Date("2026-11-01") }
    ],
    hospitalAffiliations: [],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: ["English", "Hindi"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 5),
    tenantId: TENANT_ID,
    npi: "5678901234",
    providerType: "Individual",
    firstName: "Karen",
    lastName: "Mitchell",
    middleName: "S",
    credentials: "MD",
    organizationName: null,
    primarySpecialty: "Emergency Medicine",
    taxonomyCode: "207P00000X",
    secondarySpecialties: [],
    address: "3501 Mills Ave",
    city: "Austin",
    state: "TX",
    zipCode: "78731",
    phone: "5129991234",
    fax: "5129991235",
    email: "kmitchell@stemed.example.com",
    networkParticipations: [
      {
        planId: makeId("plan", 1),
        lineOfBusiness: "Commercial",
        networkTier: "Tier1",
        effectiveDate: new Date("2025-01-01"),
        terminationDate: null,
        acceptingNewPatients: false
      }
    ],
    credentialingStatus: "Approved",
    credentialingDate: new Date("2024-07-01"),
    recredentialingDueDate: new Date("2027-07-01"),
    boardCertifications: [],
    hospitalAffiliations: [
      { hospitalName: "Dell Seton Medical Center", npi: "2222222222", privilegeStatus: "Active" }
    ],
    acceptingNewPatients: false,
    handicapAccessible: true,
    languagesSpoken: ["English"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 6),
    tenantId: TENANT_ID,
    npi: "6789012345",
    providerType: "Individual",
    firstName: "Linda",
    lastName: "Nguyen",
    middleName: "T",
    credentials: "DPT",
    organizationName: null,
    primarySpecialty: "Physical Therapy",
    taxonomyCode: "225100000X",
    secondarySpecialties: [],
    address: "2901 S 1st St, Suite 100",
    city: "Austin",
    state: "TX",
    zipCode: "78704",
    phone: "5128887766",
    fax: "5128887767",
    email: "lnguyen@atxpt.example.com",
    networkParticipations: [
      {
        planId: makeId("plan", 1),
        lineOfBusiness: "Commercial",
        networkTier: "Tier1",
        effectiveDate: new Date("2025-04-01"),
        terminationDate: null,
        acceptingNewPatients: true
      }
    ],
    credentialingStatus: "Approved",
    credentialingDate: new Date("2025-02-15"),
    recredentialingDueDate: new Date("2028-02-15"),
    boardCertifications: [],
    hospitalAffiliations: [],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: ["English", "Vietnamese"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 7),
    tenantId: TENANT_ID,
    npi: "7890123456",
    providerType: "Organization",
    firstName: null,
    lastName: null,
    credentials: null,
    organizationName: "Hill Country Orthopedic Associates",
    dbaName: "Hill Country Ortho",
    primarySpecialty: "Orthopedics",
    taxonomyCode: "207X00000X",
    secondarySpecialties: ["Physical Medicine"],
    address: "14000 N US Hwy 183, Suite 250",
    city: "Austin",
    state: "TX",
    zipCode: "78717",
    phone: "5126661234",
    fax: "5126661235",
    email: "info@hillcountryortho.example.com",
    networkParticipations: [],
    credentialingStatus: "Pending",
    credentialingDate: null,
    recredentialingDueDate: null,
    boardCertifications: [],
    hospitalAffiliations: [],
    acceptingNewPatients: true,
    handicapAccessible: true,
    languagesSpoken: ["English", "Spanish"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("prov", 8),
    tenantId: TENANT_ID,
    npi: "8901234567",
    providerType: "Organization",
    firstName: null,
    lastName: null,
    credentials: null,
    organizationName: "Lone Star Radiology Group",
    dbaName: "Lone Star Radiology",
    primarySpecialty: "Radiology",
    taxonomyCode: "2085R0202X",
    secondarySpecialties: [],
    address: "7700 Cat Hollow Dr, Suite 200",
    city: "Round Rock",
    state: "TX",
    zipCode: "78681",
    phone: "5125554321",
    fax: "5125554322",
    email: "referrals@lonestarrad.example.com",
    networkParticipations: [],
    credentialingStatus: "Expired",
    credentialingDate: new Date("2022-01-10"),
    recredentialingDueDate: new Date("2025-01-10"),
    boardCertifications: [],
    hospitalAffiliations: [],
    acceptingNewPatients: false,
    handicapAccessible: false,
    languagesSpoken: ["English"],
    status: "Active",
    terminationDate: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  }
];

choDb.Providers.insertMany(providers);
print("✓ Inserted " + providers.length + " providers");

// Map NPI → provider name for use in claims/auths
const providerNameByNpi = {};
providers.forEach(function (p) {
  providerNameByNpi[p.npi] = p.organizationName
    ? p.organizationName
    : (p.firstName + " " + p.lastName + ", " + p.credentials);
});

// =========================================================================
// 4. MEMBERS (10)
// =========================================================================
const memberData = [
  { first: "Carlos",   last: "Ramirez",   gender: "M", dob: "1968-03-14", city: "Austin",      zip: "78745", status: "Active",     plan: 1 },
  { first: "Angela",   last: "Washington", gender: "F", dob: "1975-09-22", city: "Dallas",      zip: "75201", status: "Active",     plan: 1 },
  { first: "Michael",  last: "O'Brien",    gender: "M", dob: "1990-01-08", city: "Houston",     zip: "77002", status: "Active",     plan: 2 },
  { first: "Priya",    last: "Sharma",     gender: "F", dob: "1985-06-30", city: "San Antonio", zip: "78205", status: "Active",     plan: 2 },
  { first: "William",  last: "Henderson",  gender: "M", dob: "1951-12-05", city: "Fort Worth",  zip: "76102", status: "Active",     plan: 1 },
  { first: "Thanh",    last: "Le",         gender: "F", dob: "1998-04-17", city: "Austin",      zip: "78702", status: "Active",     plan: 3 },
  { first: "Robert",   last: "Johnson",    gender: "M", dob: "1960-08-25", city: "El Paso",     zip: "79901", status: "Active",     plan: 1 },
  { first: "Sophia",   last: "Martinez",   gender: "F", dob: "1982-11-03", city: "Plano",       zip: "75024", status: "Active",     plan: 2 },
  { first: "David",    last: "Kim",        gender: "M", dob: "1972-07-19", city: "Austin",      zip: "78759", status: "COBRA",      plan: 1 },
  { first: "Margaret", last: "Thompson",   gender: "F", dob: "1955-02-28", city: "Arlington",   zip: "76010", status: "Terminated", plan: 3 }
];

const addresses = [
  "1204 Barton Springs Rd",
  "3300 Oak Lawn Ave, Apt 12B",
  "5010 Westheimer Rd",
  "742 Alamo Plaza",
  "801 Commerce St",
  "2200 S Congress Ave, Unit 4",
  "900 Montana Ave",
  "6700 Legacy Dr, Suite 101",
  "11400 Jollyville Rd",
  "1500 E Randol Mill Rd"
];

const members = memberData.map(function (m, i) {
  const idx = i + 1;
  const memberId = makeId("mbr", idx);
  const subscriberId = "SUB" + String(100000 + idx);
  return {
    _id: memberId,
    tenantId: TENANT_ID,
    memberId: memberId,
    subscriberId: subscriberId,
    ssn: "***-**-" + String(1000 + idx),
    groupNumber: (i < 8) ? GROUP_NUMBER : (GROUP_NUMBER + "-SG"),
    isSubscriber: true,
    subscriberMemberId: null,
    relationshipCode: "18",
    firstName: m.first,
    lastName: m.last,
    middleName: null,
    dateOfBirth: new Date(m.dob),
    gender: m.gender,
    address: addresses[i],
    city: m.city,
    state: "TX",
    zipCode: m.zip,
    phone: "512" + String(2000000 + idx * 1111),
    email: (m.first.toLowerCase() + "." + m.last.toLowerCase() + "@example.com").replace(/'/g, ""),
    effectiveDate: new Date("2025-01-01"),
    terminationDate: m.status === "Terminated" ? new Date("2026-02-28") : null,
    status: m.status,
    lineOfBusiness: "Commercial",
    benefitPlanId: makeId("plan", m.plan),
    employmentStatus: m.status === "Terminated" ? "Terminated" : (m.status === "COBRA" ? "Terminated" : "FullTime"),
    tobaccoUser: false,
    isStudent: false,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  };
});

choDb.Members.insertMany(members);
print("✓ Inserted " + members.length + " members");

// =========================================================================
// 5. CLAIMS (25)
// =========================================================================
const cptCodes = ["99213", "99214", "99215", "99283", "97110", "80053", "85025", "71046", "73721"];
const diagCodes = [
  { code: "M54.5", desc: "Low back pain" },
  { code: "Z00.00", desc: "General adult medical examination" },
  { code: "I10",   desc: "Essential hypertension" },
  { code: "E11.9", desc: "Type 2 diabetes mellitus without complications" },
  { code: "J06.9", desc: "Acute upper respiratory infection, unspecified" }
];

const placeOfServiceByCpt = {
  "99213": "11", "99214": "11", "99215": "11",
  "99283": "23", "97110": "11", "80053": "81",
  "85025": "81", "71046": "22", "73721": "22"
};

// Charge amount ranges by CPT
const chargeByCpt = {
  "99213": [125, 175],   "99214": [200, 275],   "99215": [300, 400],
  "99283": [800, 2500],  "97110": [75, 150],    "80053": [85, 200],
  "85025": [90, 180],    "71046": [350, 750],   "73721": [1500, 15000]
};

// Provider NPIs to rotate through (in-network individual providers)
const claimProviderNpis = [
  "1234567890", "2345678901", "3456789012", "4567890123",
  "5678901234", "6789012345"
];

const claimStatusDistribution = [];
for (let s = 0; s < 15; s++) claimStatusDistribution.push("Approved");
for (let s = 0; s < 5; s++)  claimStatusDistribution.push("Denied");
for (let s = 0; s < 5; s++)  claimStatusDistribution.push("Pending");

const claims = [];
for (let c = 0; c < 25; c++) {
  const claimIdx = c + 1;
  const memberIdx = (c % 10);
  const member = members[memberIdx];
  const cpt = cptCodes[c % cptCodes.length];
  const diag = diagCodes[c % diagCodes.length];
  const status = claimStatusDistribution[c];
  const provNpi = claimProviderNpis[c % claimProviderNpis.length];
  const provName = providerNameByNpi[provNpi];
  const pos = placeOfServiceByCpt[cpt] || "11";

  const chargeRange = chargeByCpt[cpt];
  const chargedAmount = Math.round(
    (chargeRange[0] + Math.random() * (chargeRange[1] - chargeRange[0])) * 100
  ) / 100;

  const serviceDate = recentDate(90);
  const claimId = makeId("clm", claimIdx);

  const claim = {
    _id: claimId,
    tenantId: TENANT_ID,
    claimNumber: "CLM-2026-" + String(claimIdx).padStart(5, "0"),
    memberId: member.memberId,
    subscriberId: member.subscriberId,
    benefitPlanId: member.benefitPlanId,
    coverageId: null,
    subscriberFirstName: member.firstName,
    subscriberLastName: member.lastName,
    patientFirstName: member.firstName,
    patientLastName: member.lastName,
    patientRelationship: "18",
    lineOfBusiness: "Commercial",
    billingProviderNPI: provNpi,
    billingProviderName: provName,
    renderingProviderNPI: provNpi,
    renderingProviderName: provName,
    facilityNPI: null,
    facilityName: null,
    placeOfServiceCode: pos,
    serviceDateFrom: serviceDate,
    serviceDateTo: serviceDate,
    receivedDate: new Date(serviceDate.getTime() + 3 * 86400000),
    status: status,
    claimType: "Professional",
    totalChargedAmount: chargedAmount,
    diagnosisCodes: [
      { code: diag.code, codeQualifier: "BK", description: diag.desc }
    ],
    lineItems: [
      {
        lineNumber: 1,
        procedureCode: cpt,
        modifiers: [],
        chargedAmount: chargedAmount,
        units: 1,
        placeOfServiceCode: pos,
        serviceDateFrom: serviceDate,
        serviceDateTo: serviceDate,
        diagnosisCodePointers: [1]
      }
    ],
    adjudication: null,
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  };

  // Add adjudication for Approved claims
  if (status === "Approved") {
    const allowedAmount = Math.round(chargedAmount * 0.80 * 100) / 100;
    const planPaid = Math.round(allowedAmount * 0.80 * 100) / 100;
    const memberResponsibility = Math.round((allowedAmount - planPaid) * 100) / 100;
    claim.adjudication = {
      adjudicatedDate: new Date(serviceDate.getTime() + 14 * 86400000),
      allowedAmount: allowedAmount,
      planPaid: planPaid,
      memberResponsibility: memberResponsibility,
      copayAmount: 30,
      coinsuranceAmount: memberResponsibility - 30 > 0 ? Math.round((memberResponsibility - 30) * 100) / 100 : 0,
      deductibleAmount: 0,
      adjustmentReasonCode: "CO-45",
      adjustmentAmount: Math.round((chargedAmount - allowedAmount) * 100) / 100,
      checkNumber: "EFT-" + String(900000 + claimIdx),
      paidDate: new Date(serviceDate.getTime() + 21 * 86400000)
    };
  }

  // Add denial reason for Denied claims
  if (status === "Denied") {
    claim.denialReasonCode = "CO-16";
    claim.denialReason = "Claim/service lacks information needed for adjudication";
  }

  claims.push(claim);
}

choDb.Claims.insertMany(claims);
print("✓ Inserted " + claims.length + " claims");

// =========================================================================
// 6. AUTHORIZATIONS (8)
// =========================================================================
const authorizations = [
  // 4 Approved
  {
    _id: makeId("auth", 1),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00001",
    memberId: members[0].memberId,
    coverageId: null,
    patientFirstName: members[0].firstName,
    patientLastName: members[0].lastName,
    patientDateOfBirth: members[0].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "3456789012",
    requestingProviderName: providerNameByNpi["3456789012"],
    servicingProviderNPI: "3456789012",
    servicingProviderName: providerNameByNpi["3456789012"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "PreAuthorization",
    certificationTypeCode: "I",
    serviceTypeCode: "02",
    levelOfService: "E",
    requestedServiceDateFrom: recentDate(60),
    requestedServiceDateTo: recentDate(30),
    diagnosisCodes: [
      { code: "M54.5", codeQualifier: "BK", description: "Low back pain" }
    ],
    requestedServices: [
      {
        procedureCode: "73721",
        procedureDescription: "MRI lower extremity joint without contrast",
        modifiers: [],
        requestedUnits: 1,
        approvedUnits: 1,
        unitType: "Visit",
        placeOfServiceCode: "22",
        serviceStatus: "Approved"
      }
    ],
    status: "Approved",
    reviewDecision: "A1",
    approvedUnits: 1,
    approvedServiceDateFrom: recentDate(60),
    approvedServiceDateTo: recentDate(10),
    denialReasonCode: null,
    denialReason: null,
    reviewerName: "Dr. Sarah Williams",
    submittedDate: recentDate(65),
    reviewedDate: recentDate(62),
    expirationDate: recentDate(-30),
    notes: "MRI approved based on clinical documentation of persistent low back pain.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("auth", 2),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00002",
    memberId: members[1].memberId,
    coverageId: null,
    patientFirstName: members[1].firstName,
    patientLastName: members[1].lastName,
    patientDateOfBirth: members[1].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "6789012345",
    requestingProviderName: providerNameByNpi["6789012345"],
    servicingProviderNPI: "6789012345",
    servicingProviderName: providerNameByNpi["6789012345"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "PreAuthorization",
    certificationTypeCode: "S",
    serviceTypeCode: "BZ",
    levelOfService: "U",
    requestedServiceDateFrom: recentDate(45),
    requestedServiceDateTo: recentDate(0),
    diagnosisCodes: [
      { code: "M54.5", codeQualifier: "BK", description: "Low back pain" }
    ],
    requestedServices: [
      {
        procedureCode: "97110",
        procedureDescription: "Therapeutic exercises",
        modifiers: [],
        requestedUnits: 12,
        approvedUnits: 12,
        unitType: "Visit",
        placeOfServiceCode: "11",
        serviceStatus: "Approved"
      }
    ],
    status: "Approved",
    reviewDecision: "A1",
    approvedUnits: 12,
    approvedServiceDateFrom: recentDate(45),
    approvedServiceDateTo: recentDate(-15),
    denialReasonCode: null,
    denialReason: null,
    reviewerName: "Dr. Sarah Williams",
    submittedDate: recentDate(50),
    reviewedDate: recentDate(47),
    expirationDate: recentDate(-15),
    notes: "Physical therapy authorized — 12 visits over 6 weeks.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("auth", 3),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00003",
    memberId: members[4].memberId,
    coverageId: null,
    patientFirstName: members[4].firstName,
    patientLastName: members[4].lastName,
    patientDateOfBirth: members[4].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "2345678901",
    requestingProviderName: providerNameByNpi["2345678901"],
    servicingProviderNPI: "5678901234",
    servicingProviderName: providerNameByNpi["5678901234"],
    facilityNPI: null,
    facilityName: "Dell Seton Medical Center",
    authorizationType: "PreAuthorization",
    certificationTypeCode: "I",
    serviceTypeCode: "01",
    levelOfService: "E",
    requestedServiceDateFrom: recentDate(30),
    requestedServiceDateTo: recentDate(25),
    diagnosisCodes: [
      { code: "I10", codeQualifier: "BK", description: "Essential hypertension" }
    ],
    requestedServices: [
      {
        procedureCode: "99223",
        procedureDescription: "Initial hospital care, high complexity",
        modifiers: [],
        requestedUnits: 3,
        approvedUnits: 3,
        unitType: "Day",
        placeOfServiceCode: "21",
        serviceStatus: "Approved"
      }
    ],
    status: "Approved",
    reviewDecision: "A1",
    approvedUnits: 3,
    approvedServiceDateFrom: recentDate(30),
    approvedServiceDateTo: recentDate(25),
    denialReasonCode: null,
    denialReason: null,
    reviewerName: "Dr. Mark Torres",
    submittedDate: recentDate(32),
    reviewedDate: recentDate(31),
    expirationDate: recentDate(-5),
    notes: "Inpatient admission approved for 3-day stay — hypertensive urgency evaluation.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("auth", 4),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00004",
    memberId: members[2].memberId,
    coverageId: null,
    patientFirstName: members[2].firstName,
    patientLastName: members[2].lastName,
    patientDateOfBirth: members[2].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "1234567890",
    requestingProviderName: providerNameByNpi["1234567890"],
    servicingProviderNPI: "2345678901",
    servicingProviderName: providerNameByNpi["2345678901"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "Referral",
    certificationTypeCode: "R",
    serviceTypeCode: "03",
    levelOfService: "U",
    requestedServiceDateFrom: recentDate(20),
    requestedServiceDateTo: recentDate(-10),
    diagnosisCodes: [
      { code: "E11.9", codeQualifier: "BK", description: "Type 2 diabetes mellitus without complications" }
    ],
    requestedServices: [
      {
        procedureCode: "99214",
        procedureDescription: "Office visit, moderate complexity — specialist referral",
        modifiers: [],
        requestedUnits: 2,
        approvedUnits: 2,
        unitType: "Visit",
        placeOfServiceCode: "11",
        serviceStatus: "Approved"
      }
    ],
    status: "Approved",
    reviewDecision: "A1",
    approvedUnits: 2,
    approvedServiceDateFrom: recentDate(20),
    approvedServiceDateTo: recentDate(-10),
    denialReasonCode: null,
    denialReason: null,
    reviewerName: "Dr. Sarah Williams",
    submittedDate: recentDate(22),
    reviewedDate: recentDate(21),
    expirationDate: recentDate(-10),
    notes: "Specialist referral approved — Internal Medicine consult for diabetes management.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  // 2 Pending Review
  {
    _id: makeId("auth", 5),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00005",
    memberId: members[3].memberId,
    coverageId: null,
    patientFirstName: members[3].firstName,
    patientLastName: members[3].lastName,
    patientDateOfBirth: members[3].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "3456789012",
    requestingProviderName: providerNameByNpi["3456789012"],
    servicingProviderNPI: "4567890123",
    servicingProviderName: providerNameByNpi["4567890123"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "PreAuthorization",
    certificationTypeCode: "S",
    serviceTypeCode: "73",
    levelOfService: "U",
    requestedServiceDateFrom: recentDate(5),
    requestedServiceDateTo: recentDate(-25),
    diagnosisCodes: [
      { code: "M54.5", codeQualifier: "BK", description: "Low back pain" }
    ],
    requestedServices: [
      {
        procedureCode: "73721",
        procedureDescription: "MRI lower extremity joint without contrast",
        modifiers: [],
        requestedUnits: 1,
        approvedUnits: null,
        unitType: "Visit",
        placeOfServiceCode: "22",
        serviceStatus: "Pending"
      }
    ],
    status: "InReview",
    reviewDecision: null,
    approvedUnits: null,
    approvedServiceDateFrom: null,
    approvedServiceDateTo: null,
    denialReasonCode: null,
    denialReason: null,
    reviewerName: null,
    submittedDate: recentDate(5),
    reviewedDate: null,
    expirationDate: null,
    notes: "Awaiting clinical review — MRI request for knee pain.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  {
    _id: makeId("auth", 6),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00006",
    memberId: members[6].memberId,
    coverageId: null,
    patientFirstName: members[6].firstName,
    patientLastName: members[6].lastName,
    patientDateOfBirth: members[6].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "1234567890",
    requestingProviderName: providerNameByNpi["1234567890"],
    servicingProviderNPI: "6789012345",
    servicingProviderName: providerNameByNpi["6789012345"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "PreAuthorization",
    certificationTypeCode: "S",
    serviceTypeCode: "BZ",
    levelOfService: "U",
    requestedServiceDateFrom: recentDate(3),
    requestedServiceDateTo: recentDate(-27),
    diagnosisCodes: [
      { code: "M54.5", codeQualifier: "BK", description: "Low back pain" }
    ],
    requestedServices: [
      {
        procedureCode: "97110",
        procedureDescription: "Therapeutic exercises",
        modifiers: [],
        requestedUnits: 8,
        approvedUnits: null,
        unitType: "Visit",
        placeOfServiceCode: "11",
        serviceStatus: "Pending"
      }
    ],
    status: "InReview",
    reviewDecision: null,
    approvedUnits: null,
    approvedServiceDateFrom: null,
    approvedServiceDateTo: null,
    denialReasonCode: null,
    denialReason: null,
    reviewerName: null,
    submittedDate: recentDate(3),
    reviewedDate: null,
    expirationDate: null,
    notes: "Physical therapy request submitted — pending review.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  // 1 Denied
  {
    _id: makeId("auth", 7),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00007",
    memberId: members[5].memberId,
    coverageId: null,
    patientFirstName: members[5].firstName,
    patientLastName: members[5].lastName,
    patientDateOfBirth: members[5].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "3456789012",
    requestingProviderName: providerNameByNpi["3456789012"],
    servicingProviderNPI: "4567890123",
    servicingProviderName: providerNameByNpi["4567890123"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "PreAuthorization",
    certificationTypeCode: "S",
    serviceTypeCode: "73",
    levelOfService: "U",
    requestedServiceDateFrom: recentDate(40),
    requestedServiceDateTo: recentDate(35),
    diagnosisCodes: [
      { code: "Z00.00", codeQualifier: "BK", description: "General adult medical examination" }
    ],
    requestedServices: [
      {
        procedureCode: "73721",
        procedureDescription: "MRI lower extremity joint without contrast",
        modifiers: [],
        requestedUnits: 1,
        approvedUnits: 0,
        unitType: "Visit",
        placeOfServiceCode: "22",
        serviceStatus: "Denied"
      }
    ],
    status: "Denied",
    reviewDecision: "A4",
    approvedUnits: 0,
    approvedServiceDateFrom: null,
    approvedServiceDateTo: null,
    denialReasonCode: "NOT_MEDICALLY_NECESSARY",
    denialReason: "MRI not medically necessary — clinical documentation does not support imaging for routine exam.",
    reviewerName: "Dr. Mark Torres",
    submittedDate: recentDate(45),
    reviewedDate: recentDate(42),
    expirationDate: null,
    notes: "Denied — insufficient clinical justification for MRI during routine physical.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  },
  // 1 Partially Approved
  {
    _id: makeId("auth", 8),
    tenantId: TENANT_ID,
    authorizationNumber: "AUTH-2026-00008",
    memberId: members[7].memberId,
    coverageId: null,
    patientFirstName: members[7].firstName,
    patientLastName: members[7].lastName,
    patientDateOfBirth: members[7].dateOfBirth,
    lineOfBusiness: "Commercial",
    requestingProviderNPI: "6789012345",
    requestingProviderName: providerNameByNpi["6789012345"],
    servicingProviderNPI: "6789012345",
    servicingProviderName: providerNameByNpi["6789012345"],
    facilityNPI: null,
    facilityName: null,
    authorizationType: "PreAuthorization",
    certificationTypeCode: "S",
    serviceTypeCode: "BZ",
    levelOfService: "U",
    requestedServiceDateFrom: recentDate(30),
    requestedServiceDateTo: recentDate(0),
    diagnosisCodes: [
      { code: "M54.5", codeQualifier: "BK", description: "Low back pain" }
    ],
    requestedServices: [
      {
        procedureCode: "97110",
        procedureDescription: "Therapeutic exercises",
        modifiers: [],
        requestedUnits: 20,
        approvedUnits: 12,
        unitType: "Visit",
        placeOfServiceCode: "11",
        serviceStatus: "Approved"
      }
    ],
    status: "Modified",
    reviewDecision: "A2",
    approvedUnits: 12,
    approvedServiceDateFrom: recentDate(30),
    approvedServiceDateTo: recentDate(-10),
    denialReasonCode: null,
    denialReason: null,
    reviewerName: "Dr. Sarah Williams",
    submittedDate: recentDate(35),
    reviewedDate: recentDate(32),
    expirationDate: recentDate(-10),
    notes: "Partially approved — 12 of 20 requested PT visits authorized. Reassess after 12 visits.",
    createdDate: now,
    lastUpdatedDate: now,
    createdBy: "seed-script",
    lastUpdatedBy: "seed-script"
  }
];

choDb.Authorizations.insertMany(authorizations);
print("✓ Inserted " + authorizations.length + " authorizations");

// =========================================================================
// Summary
// =========================================================================
print("\n=== Seed complete for tenant: " + TENANT_ID + " ===");
print("  BenefitPlans:    " + benefitPlans.length);
print("  Sponsors:        " + sponsors.length);
print("  Providers:       " + providers.length);
print("  Members:         " + members.length);
print("  Claims:          " + claims.length);
print("  Authorizations:  " + authorizations.length);
print("\nTotal documents:   " + (benefitPlans.length + sponsors.length + providers.length + members.length + claims.length + authorizations.length));
