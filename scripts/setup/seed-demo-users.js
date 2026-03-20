// seed-demo-users.js
// MongoDB seed script that populates realistic operations staff user accounts
// for a given tenant in the CloudHealthOffice database.
//
// WARNING: This script REPLACES any existing TenantUsers data for the specified
// tenant. Back up first if you need to preserve data.
//
// Usage:
//   mongosh <connection-string> seed-demo-users.js \
//     --eval 'var tenantId="healthtech-solutions"'
//
// Required --eval variables:
//   tenantId — tenant identifier written to every document

// Validate required parameters
if (typeof tenantId === "undefined") {
  print("ERROR: Missing required parameter: tenantId");
  print("");
  print("Usage:");
  print('  mongosh <connection-string> seed-demo-users.js \\');
  print('    --eval \'var tenantId="healthtech-solutions"\'');
  quit(1);
}

const TENANT_ID = tenantId;
const choDb = db.getSiblingDB("cloudhealthoffice");
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
// Clean existing tenant users
// ---------------------------------------------------------------------------
const deleted = choDb.TenantUsers.deleteMany({ tenantId: TENANT_ID });
print("Cleared " + deleted.deletedCount + " existing doc(s) from TenantUsers");

// ---------------------------------------------------------------------------
// Define users
// ---------------------------------------------------------------------------
const sarahId = makeId("usr", 1);

const usersData = [
  // Claims Department
  {
    idx: 1,
    firstName: "Sarah",
    lastName: "Johnson",
    email: "sarah.johnson@demo.test",
    role: "ClaimsSupervisor",
    department: "Claims",
    supervisorId: null,
    recentLogin: true
  },
  {
    idx: 2,
    firstName: "Michael",
    lastName: "Chen",
    email: "michael.chen@demo.test",
    role: "ClaimsExaminer",
    department: "Claims",
    supervisorId: sarahId,
    recentLogin: true
  },
  {
    idx: 3,
    firstName: "Emily",
    lastName: "Rodriguez",
    email: "emily.rodriguez@demo.test",
    role: "ClaimsExaminer",
    department: "Claims",
    supervisorId: sarahId,
    recentLogin: true
  },

  // Member Services
  {
    idx: 4,
    firstName: "Jennifer",
    lastName: "Williams",
    email: "jennifer.williams@demo.test",
    role: "MemberServices",
    department: "Member Services",
    supervisorId: null,
    recentLogin: true
  },
  {
    idx: 5,
    firstName: "Robert",
    lastName: "Garcia",
    email: "robert.garcia@demo.test",
    role: "MemberServices",
    department: "Member Services",
    supervisorId: null,
    recentLogin: true
  },

  // Utilization Management
  {
    idx: 6,
    firstName: "Lisa",
    lastName: "Martinez",
    email: "lisa.martinez@demo.test",
    role: "UMCoordinator",
    department: "UM",
    supervisorId: null,
    recentLogin: true
  },
  {
    idx: 7,
    firstName: "David",
    lastName: "Thompson",
    email: "david.thompson@demo.test",
    role: "UMCoordinator",
    department: "UM",
    supervisorId: null,
    recentLogin: true
  },

  // Provider Relations
  {
    idx: 8,
    firstName: "Amanda",
    lastName: "Foster",
    email: "amanda.foster@demo.test",
    role: "ProviderRelations",
    department: "Provider Network",
    supervisorId: null,
    recentLogin: true
  },

  // Finance
  {
    idx: 9,
    firstName: "James",
    lastName: "Anderson",
    email: "james.anderson@demo.test",
    role: "Finance",
    department: "Finance",
    supervisorId: null,
    recentLogin: false
  },

  // Administration
  {
    idx: 10,
    firstName: "Admin",
    lastName: "User",
    email: "admin@demo.test",
    role: "TenantAdmin",
    department: "IT",
    supervisorId: null,
    recentLogin: true
  },
  {
    idx: 11,
    firstName: "Compliance",
    lastName: "Lead",
    email: "compliance@demo.test",
    role: "ComplianceOfficer",
    department: "Compliance",
    supervisorId: null,
    recentLogin: false
  }
];

// ---------------------------------------------------------------------------
// Build and insert documents
// ---------------------------------------------------------------------------
const users = usersData.map(function (u) {
  const userId = makeId("usr", u.idx);
  return {
    _id: userId,
    tenantId: TENANT_ID,
    userId: userId,
    firstName: u.firstName,
    lastName: u.lastName,
    displayName: u.firstName + " " + u.lastName,
    email: u.email,
    role: u.role,
    department: u.department,
    supervisorId: u.supervisorId,
    isActive: true,
    lastLoginAt: u.recentLogin ? recentDate(7) : recentDate(30),
    createdAt: new Date("2025-06-01"),
    updatedAt: now,
    createdBy: "seed-script",
    updatedBy: "seed-script"
  };
});

choDb.TenantUsers.insertMany(users);
print("✓ Inserted " + users.length + " tenant users");

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------
print("\n=== Seed complete for tenant: " + TENANT_ID + " ===");
print("  TenantUsers: " + users.length);
print("");
print("  Claims:            3 (1 supervisor, 2 examiners)");
print("  Member Services:   2");
print("  UM:                2");
print("  Provider Network:  1");
print("  Finance:           1");
print("  IT (Admin):        1");
print("  Compliance:        1");
