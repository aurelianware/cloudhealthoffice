// report-plans-missing-service-categories.js
// READ-ONLY rollout report for the corrected professional place-of-service
// fallback (ServiceCategoryResolver.ProfessionalPlaceOfServiceMap) and the
// system-defaults v3 bundle. Lists active benefit plans that have no benefit
// category for a category the engine can now resolve a line to, and what
// adjudication does for such a line:
//
//   - "fallback -> X": paid under plan category X through the rollout
//     fallback chain (ServiceCategoryNames.RolloutFallbackFor), logged at
//     Warning by BenefitRuleGate. Author the specific category to stop it.
//   - "DENY CARC 96": no category and no fallback; the line denies.
//
// A plan matches a category by its name or by one of its X12 service type
// codes (BenefitPlanConfig.LookupCategories), case-insensitive, and this
// report applies the same rule. Nothing is written.
//
// Usage:
//   mongosh <connection-string> scripts/migration/report-plans-missing-service-categories.js
//   mongosh <connection-string> scripts/migration/report-plans-missing-service-categories.js \
//     --eval 'var tenantId="dev-tenant"'
//
// Optional variables (pass with --eval before the script):
//   tenantId  restrict to one tenant
//   dbName    database (default "cloudhealthoffice")
//   collName  benefit plan collection (default "BenefitPlans")
//
// Documents are read in the C# driver's PascalCase shape (TenantId, PlanId,
// Benefits[].ServiceCategory, ...). For the Cosmos backend (camelCase JSON),
// the equivalent query in the Data Explorer is:
//   SELECT c.tenantId, c.planId, c.planName,
//          ARRAY(SELECT VALUE b.serviceCategory FROM b IN c.benefits) AS categories
//   FROM c WHERE c.isActive = true AND (NOT IS_DEFINED(c.versionState) OR c.versionState = 1)
// and the categories are checked against the table below by hand.

// Keep in sync with ServiceCategoryNames (Table and RolloutFallbackByName).
const CHECKS = [
  { name: "Urgent Care",        x12: ["UC"], fallback: "Office Visit",        fallbackX12: ["98"] },
  { name: "Outpatient Surgery", x12: ["13"], fallback: "Outpatient Hospital", fallbackX12: ["50"] },
  { name: "Laboratory",         x12: ["5"],  fallback: "Outpatient Hospital", fallbackX12: ["50"] },
  { name: "Physical Therapy",   x12: ["PT"], fallback: null,                  fallbackX12: [] },
  { name: "Hospice",            x12: ["45"], fallback: "Home Health",         fallbackX12: ["42"] },
  { name: "Skilled Nursing",    x12: ["AG"], fallback: null,                  fallbackX12: [] },
];

const DB_NAME = typeof dbName === "undefined" ? "cloudhealthoffice" : dbName;
const COLL_NAME = typeof collName === "undefined" ? "BenefitPlans" : collName;
const TENANT = typeof tenantId === "undefined" ? null : tenantId;

const now = new Date();
const filter = {
  IsActive: { $ne: false },
  // Published (1) or legacy rows without a version state.
  $and: [
    { $or: [{ VersionState: { $exists: false } }, { VersionState: 1 }, { VersionState: "Published" }] },
    { $or: [{ TerminationDate: null }, { TerminationDate: { $gte: now } }] },
  ],
};
if (TENANT !== null) filter.TenantId = TENANT;

const plans = db.getSiblingDB(DB_NAME)[COLL_NAME]
  .find(filter, { TenantId: 1, PlanId: 1, PlanName: 1, VersionNumber: 1, "Benefits.ServiceCategory": 1 })
  .sort({ TenantId: 1, PlanId: 1, VersionNumber: -1 })
  .toArray();

function has(categories, name, codes) {
  if (name !== null && categories.has(name.toLowerCase())) return true;
  return codes.some(function (c) { return categories.has(c.toLowerCase()); });
}

let affected = 0;
let denying = 0;
print("tenant\tplanId\tplanName\tmissing category\teffect");
plans.forEach(function (p) {
  const categories = new Set((p.Benefits || [])
    .map(function (b) { return (b.ServiceCategory || "").trim().toLowerCase(); })
    .filter(function (c) { return c.length > 0; }));

  const rows = [];
  CHECKS.forEach(function (check) {
    if (has(categories, check.name, check.x12)) return;
    const effect = check.fallback !== null && has(categories, check.fallback, check.fallbackX12)
      ? "fallback -> " + check.fallback
      : "DENY CARC 96";
    if (effect === "DENY CARC 96") denying++;
    rows.push(check.name + "\t" + effect);
  });

  if (rows.length === 0) return;
  affected++;
  rows.forEach(function (r) {
    print(p.TenantId + "\t" + p.PlanId + "\t" + (p.PlanName || "") + "\t" + r);
  });
});

print("");
print("Active plan versions scanned: " + plans.length +
  "; plans missing at least one category: " + affected +
  "; category gaps that deny: " + denying);
