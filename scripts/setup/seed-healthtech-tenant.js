// seed-healthtech-tenant.js
// MongoDB seed script for HTR tenant provisioning.
// Run with: mongosh <connection-string> seed-healthtech-tenant.js
//
// Prerequisites:
//   - MongoDB connection string with write access to CloudHealthOffice database
//   - Replace the azureTenantId placeholder with the tenant's actual Azure AD tenant ID
//     (found in Azure Portal > Azure Active Directory > Overview > Tenant ID)

const db = db.getSiblingDB("CloudHealthOffice");

const now = new Date();

const tenant = {
  _id: "healthtech-solutions",
  tenantId: "healthtech-solutions",
  // TODO: Replace with HTR's actual Azure AD tenant ID (GUID).
  // Get it from the tenant admin, or look it up in Azure Portal:
  //   Azure Active Directory > Overview > Tenant ID
  azureTenantId: "REPLACE_WITH_HTR_AZURE_AD_TENANT_ID",
  organizationName: "HealthTech Solutions",
  subscriptionStatus: "Active",
  tier: "enterprise",
  isDemo: false,
  stripeCustomerId: null,
  stripeSubscriptionId: null,
  trialEndsAt: null,
  createdAt: now,
  updatedAt: now,
  // TODO: Confirm the admin's email address before running in production.
  adminEmails: [
    "jcooper@healthtechsolutions.com"
  ]
};

// Upsert: insert if not exists, update if already present.
const result = db.Tenants.replaceOne(
  { tenantId: "healthtech-solutions" },
  tenant,
  { upsert: true }
);

if (result.upsertedCount === 1) {
  print("✓ Inserted new tenant: HTR");
} else if (result.modifiedCount === 1) {
  print("✓ Updated existing tenant: HTR");
} else {
  print("⚠ No changes made (tenant already matches)");
}

// Verify the insert
const saved = db.Tenants.findOne({ tenantId: "healthtech-solutions" });
print("\nSaved document:");
printjson(saved);
