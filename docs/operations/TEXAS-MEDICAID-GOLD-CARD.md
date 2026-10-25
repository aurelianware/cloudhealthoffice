# Texas Medicaid statutory gold-card exclusion

The `TxGoldCardExemption` evaluator now returns no decision whenever either the
request or stored rule identifies Medicaid. This continues normal PA evaluation;
it neither denies care nor guarantees approval by another rule. It protects old
persisted platform rules and tenant overrides even if enabled after deployment.
The three Medicaid seed IDs are retained but disabled for new installations.
Existing databases are not automatically rewritten, and historical decisions are
not changed. Non-Medicaid behavior is preserved, not certified as a complete
current-law commercial exemption implementation.

Authority reviewed October 24, 2026: [TDI applicability FAQ](https://www.tdi.texas.gov/health/hb3459-faq.html),
Insurance Code §4201.652. The statute is HB 3459, amended by HB 3812; the earlier
HB 3229 label was incorrect. Including Medicaid requests in an issuer's evaluation
history does not make Medicaid coverage eligible for the exemption.

## Rollout and reconciliation

Deploy all consumers of the shared engine, including FHIR/PAS and authorization
hosts; old binaries can still apply the old rule. Inventory persisted platform
and tenant `TxGoldCardExemption` documents for Medicaid and retire them through
normal rule administration/cache invalidation. The runtime guard does not depend
on that cleanup. Do not change provider approval history or disable unrelated PA
rules to perform cleanup.

UM/compliance must identify historical decisions with the three retired seed IDs
and any tenant rule of this type, assess affected request/member/provider scope,
and decide remediation and communications. No historical approval is automatically
revoked and no notice or clinical decision is issued by this change.

## Remaining work

Commercial eligibility, actual exemption grants/notices, service-specific issuer
history and current evaluation/rescission periods require a separate qualified
implementation. A voluntary Medicaid arrangement needs its own reviewed authority
and rule type; do not repurpose this statutory exemption or bypass this guard.
Program-specific PA clocks remain open. Atomic PCP capacity and California
acceptance are the subsequent workstreams requested by the plan.

## Validation

The rule-engine test project and dependencies built on SDK 8.0.426. Seventeen
focused gold-card and engine cases passed using the installed xUnit AssemblyRunner
with real discovery/theories; zero failed/skipped. VSTest 17.11.1 crashes locally
before execution, so standard CI remains required. The E2E seed scenario was
updated to reject the former gold-card firing rule but was not executed locally.
No database migration, deployment, historical decision change or notification ran.
