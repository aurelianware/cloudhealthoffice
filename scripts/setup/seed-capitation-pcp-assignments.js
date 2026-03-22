// seed-capitation-pcp-assignments.js
// Updates Coverage records with PCP assignments for capitation demo data.
// Run AFTER seed-demo-data.js and BEFORE seed-capitation.sh (or as part of it).
//
// Usage:
//   mongosh <connection-string> scripts/setup/seed-capitation-pcp-assignments.js \
//     --eval 'var tenantId="dev-tenant"'

if (typeof tenantId === "undefined") {
  print("ERROR: Missing tenantId parameter.");
  print("Usage:");
  print('  mongosh <conn-string> seed-capitation-pcp-assignments.js --eval \'var tenantId="dev-tenant"\'');
  quit(1);
}

const TENANT_ID = tenantId;
const choDb = db.getSiblingDB("cloudhealthoffice");
const now = new Date();
const TENANT_PREFIX = TENANT_ID.substring(0, 8);

function makeId(prefix, n) {
  return prefix + "-" + TENANT_PREFIX + "-" + String(n).padStart(4, "0");
}

print("");
print("═══════════════════════════════════════════════════════════════");
print("  Capitation PCP Assignments — Tenant: " + TENANT_ID);
print("═══════════════════════════════════════════════════════════════");
print("");

// ═══════════════════════════════════════════════════════════════════════════
// PCP ASSIGNMENTS
// Assigns existing members to 3 capitated providers via Coverage.PcpNpi
// ═══════════════════════════════════════════════════════════════════════════

const assignments = [
  // Dr. Sarah Chen (NPI: 1234567890) — 8 Commercial members
  { memberId: makeId("mbr", 1),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "MemberSelected" },
  { memberId: makeId("mbr", 2),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "MemberSelected" },
  { memberId: makeId("mbr", 3),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "AutoAssigned" },
  { memberId: makeId("mbr", 4),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "AutoAssigned" },
  { memberId: makeId("mbr", 5),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "MemberSelected" },
  { memberId: makeId("mbr", 6),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "PlanDefault" },
  { memberId: makeId("mbr", 7),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "AutoAssigned" },
  { memberId: makeId("mbr", 8),  pcpNpi: "1234567890", pcpName: "Dr. Sarah Chen, MD",     method: "MemberSelected" },

  // Valley Medical Group (NPI: 9876543210) — 7 Medicaid members
  { memberId: makeId("mbr", 9),  pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "PlanDefault" },
  { memberId: makeId("mbr", 10), pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "AutoAssigned" },
  { memberId: makeId("mbr", 11), pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "AutoAssigned" },
  { memberId: makeId("mbr", 12), pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "MemberSelected" },
  { memberId: makeId("mbr", 13), pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "PlanDefault" },
  { memberId: makeId("mbr", 14), pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "AutoAssigned" },
  { memberId: makeId("mbr", 15), pcpNpi: "9876543210", pcpName: "Valley Medical Group",   method: "AutoAssigned" },

  // Dr. James Park (NPI: 5551234567) — 5 BH members
  { memberId: makeId("mbr", 16), pcpNpi: "5551234567", pcpName: "Dr. James Park, PsyD",   method: "MemberSelected" },
  { memberId: makeId("mbr", 17), pcpNpi: "5551234567", pcpName: "Dr. James Park, PsyD",   method: "MemberSelected" },
  { memberId: makeId("mbr", 18), pcpNpi: "5551234567", pcpName: "Dr. James Park, PsyD",   method: "AutoAssigned" },
  { memberId: makeId("mbr", 19), pcpNpi: "5551234567", pcpName: "Dr. James Park, PsyD",   method: "AutoAssigned" },
  { memberId: makeId("mbr", 20), pcpNpi: "5551234567", pcpName: "Dr. James Park, PsyD",   method: "PlanDefault" }
];

let updated = 0;
let notFound = 0;

assignments.forEach(function (a) {
  // Update Coverage collection where memberId matches and tenant matches
  const result = choDb.Coverage.updateMany(
    {
      tenantId: TENANT_ID,
      memberId: a.memberId,
      status: { $in: ["Active", 1] }
    },
    {
      $set: {
        pcpNpi: a.pcpNpi,
        pcpName: a.pcpName,
        pcpAssignmentDate: now,
        pcpAssignmentMethod: a.method,
        previousPcpNpi: null,
        lastUpdatedDate: now,
        lastUpdatedBy: "seed-capitation"
      }
    }
  );

  if (result.modifiedCount > 0) {
    updated += result.modifiedCount;
  } else {
    // If no Coverage record, try updating the Members collection pcpProviderId
    // (fallback for tenants that seed members but not coverage separately)
    const memberResult = choDb.Members.updateOne(
      { tenantId: TENANT_ID, memberId: a.memberId },
      {
        $set: {
          pcpProviderNpi: a.pcpNpi,
          pcpProviderName: a.pcpName,
          pcpAssignedDate: now,
          lastUpdatedDate: now,
          lastUpdatedBy: "seed-capitation"
        }
      }
    );
    if (memberResult.modifiedCount > 0) {
      updated++;
    } else {
      notFound++;
    }
  }
});

print("✓ " + updated + " coverage/member records updated with PCP assignments");
if (notFound > 0) {
  print("⚠ " + notFound + " members not found (run seed-demo-data.js first)");
}

// Print summary by provider
print("");
print("  PCP Assignment Summary:");
print("  ─────────────────────────────────────────────────");
print("  Dr. Sarah Chen (1234567890)    — 8 members (Commercial)");
print("  Valley Medical Group (9876543210) — 7 members (Medicaid)");
print("  Dr. James Park (5551234567)    — 5 members (BH/Commercial)");
print("  ─────────────────────────────────────────────────");
print("  Total: 20 members assigned");
print("");
