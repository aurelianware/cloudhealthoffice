# ADR 027: Reference backend and engagement-specific core connectors

**Status:** accepted product/architecture intent, confirmed by the owner October 22, 2026.

## Decision

CHO is the **reference backend implementation** behind its domain adapter
contracts. QNXT, Facets and HealthEdge HealthRules Payer use different connector
implementations of those contracts. Vendor connector implementation is
**anticipated engagement integration work**, scoped to the payer, rather than a
commitment to ship one universal production connector for every vendor.

The same platform name does not identify one integration: version/release/patch,
product modules and licensed interfaces, payer configuration/customizations,
solution architecture, hosting/BPaaS responsibilities, network/authentication,
data ownership and latency differ. Connector methods are intentionally left
unimplemented until an engagement establishes the supported access contract.
They must fail explicitly if selected before implementation, never return
synthetic success or silently fall back to the reference backend.

Use the [core administration adapter QRE](../features/questionnaires/qre-core-admin-adapter-questions.md)
and [example answers](../features/questionnaires/examples/qre-core-admin-adapter-example.md)
as the discovery and scope contract. The implementation-discovery analogy is a
clearinghouse onboarding/QRE engagement, such as an Availity implementation;
this is not a statement of partnership, certification, compatibility or equal
implementation effort.

## Classification for assessments

| Question | Correct treatment |
| --- | --- |
| Does CHO implement a workflow natively? | Inspect the reference backend's actual code, registration, tests and deployment evidence. A vendor stub cannot answer this question. |
| Why does a vendor connector throw NotImplementedException? | Engagement-scoped interface/template, awaiting the payer's access/mapping contract. Not automatically a CHO-native product gap. |
| Is the vendor connector ready for a particular payer? | Unverified/unimplemented for that engagement until the connector is built and accepted. A named class is not working connectivity. |
| Is the native workflow itself incomplete? | Record the specific product gap with source evidence. This ADR does not excuse missing native functionality, incorrect behavior or unsupported deployment claims. |
| Is Augment/shadow mode usable with this payer? | Requires the agreed connector or comparison extract. Native reference behavior alone is not incumbent comparison evidence. |

Report these axes separately: **native reference capability**, **connector
contract/interface**, **payer-specific connector implementation**, and **deployed
acceptance**. Do not count a connector interface as either a shipped vendor
integration or a missing native business capability.

Use the [onboarding and acceptance template](../implementation/CORE-CONNECTOR-ONBOARDING.md) to turn QRE answers into a traceable implementation contract. The [implementer guide](../integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md)
and its conformance suites cover what each connector method must do.

## Engagement acceptance

1. Complete QRE discovery by environment, version, licensed module/interface,
   hosting responsibility and source-of-truth domain.
2. Agree the connector mechanism (supported API, approved replica/view,
   extract/EDI, events or payer middleware), credentials and permissions.
3. Define identity, field/code mappings, effective dating, history, change
   detection, idempotency, failures, retry and reconciliation semantics.
4. Implement the vendor/environment-specific connector; test representative
   scenarios and negative tenant/member boundaries in the payer test environment.
5. Reconcile results and operational recovery, record acceptance evidence and
   own support/upgrade responsibilities before production cutover.

This decision is linked from the README, architecture map, capability ledger,
QRE and vendor connector source comments so code-only assessments can find it.
