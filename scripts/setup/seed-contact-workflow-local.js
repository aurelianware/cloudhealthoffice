// Synthetic fixtures for the isolated cho-contact-local deployment only.
// Insert once; never replace an operator's existing users or claims.
const localDb = db.getSiblingDB("ContactWorkflowLocal");
localDb.TenantUsers.updateOne({_id: "local-demo-admin"}, {$setOnInsert: {
  tenantId: "demo", email: "local-demo-admin@example.test",
  emailNormalized: "local-demo-admin@example.test", displayName: "Local Demo Admin",
  roles: ["MemberServices"], status: "Active", createdAt: new Date(), updatedAt: new Date()
}}, {upsert: true});
localDb.Claims.updateOne({_id: "synthetic-contact-link-20261025"}, {$setOnInsert: {
  TenantId: "demo", ClaimNumber: "SYNTHETIC-CONTACT-20261025",
  MemberId: "synthetic-member", BillingProviderNPI: "9999999999",
  ServiceDateFrom: new Date(), ServiceDateTo: new Date(), CreatedDate: new Date(),
  UpdatedDate: new Date(), Status: 0, ClaimLines: []
}}, {upsert: true});
print("Local synthetic directory and claim fixtures available.");
