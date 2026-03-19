// seed-tenant.js
// MongoDB seed script for provisioning a new tenant.
//
// Usage:
//   mongosh <connection-string> seed-tenant.js \
//     --eval 'var tenantId="my-org", azureTenantId="AZURE-AD-GUID", orgName="My Org", adminEmail="admin@example.com"'
//
// All four variables are required.

// Validate required parameters
if (typeof tenantId === "undefined" || typeof azureTenantId === "undefined" ||
    typeof orgName === "undefined" || typeof adminEmail === "undefined") {
  print("ERROR: Missing required parameters.");
  print("");
  print("Usage:");
  print('  mongosh <connection-string> seed-tenant.js \\');
  print('    --eval \'var tenantId="my-org", azureTenantId="AZURE-AD-GUID", orgName="My Org", adminEmail="admin@example.com"\'');
  quit(1);
}

const choDb = db.getSiblingDB("cloudhealthoffice");

const now = new Date();

const tenant = {
  _id: tenantId,
  tenantId: tenantId,
  azureTenantId: azureTenantId,
  organizationName: orgName,
  subscriptionStatus: "Active",
  tier: "enterprise",
  isDemo: false,
  stripeCustomerId: null,
  stripeSubscriptionId: null,
  trialEndsAt: null,
  createdAt: now,
  updatedAt: now,
  adminEmails: [adminEmail]
};

// Upsert: insert if not exists, update if already present.
const result = choDb.Tenants.replaceOne(
  { tenantId: tenantId },
  tenant,
  { upsert: true }
);

if (result.upsertedCount === 1) {
  print("✓ Inserted new tenant: " + orgName);
} else if (result.modifiedCount === 1) {
  print("✓ Updated existing tenant: " + orgName);
} else {
  print("⚠ No changes made (tenant already matches)");
}

// Verify the insert
const saved = choDb.Tenants.findOne({ tenantId: tenantId });
print("\nSaved document:");
printjson(saved);
