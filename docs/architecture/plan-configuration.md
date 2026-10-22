# Plan configuration: settings that cascade, with overrides

**Status:** Draft for review (2026-10-22).
**Scope:** One way for every CHO service to define a business setting, set it at the right level of the plan's structure, override it further down, and explain where an effective value came from.

**Related:**
- [enrollment-operations.md](enrollment-operations.md): the first settings to use this (retro limits, waiting periods, orphan grace, full-replace guard).
- [appeals-case-model.md](appeals-case-model.md): state rule packs, which are regulatory data rather than settings (§8).
- [edi-operations.md](edi-operations.md): partner settings, which stay in trading-partner-service (§8).

---

## 1. The problem

Enrollment alone already needs these settings, each resolved per member:
- retro add and term limits;
- waiting periods;
- the orphan grace period;
- the full-replace guard.

Premium billing needs grace periods. Claims, appeals and correspondence will need their own. Each one has a sensible default per line of business, and specific plans, sponsors or contracts override it.

**What exists today:**
- **Line of business is only an enum.** It sits on the sponsor and the benefit plan; nothing above it can carry configuration.
- **There is no sponsor policy or class.** The sponsor is a single record.
- **Settings live wherever they were first needed.** The premium grace period is on the sponsor's billing info. #1500 adds a small settings store inside enrollment-import-service.

Without one model, every service grows its own store, its own hierarchy logic and its own admin screen. Nobody can answer "why is this member's retro limit 90 days?" from one place.

## 2. How QNXT and Facets do it

**QNXT:**
- A **carrier** has one or more **programs**; a program is a line of business.
- Carrier settings are overridden at the program level.
- A **sponsor** has **sponsor policies**, and each policy turns settings on or off and sets their values.

**Facets:**
- The enrollment structure is **group → subgroup → class → plan**.
- Eligibility rules, waiting periods and billing hang off the subgroup and class.

**What both show:**
1. **Line of business is a thing, not a label.** One carrier runs several Medicaid programs (in Texas: STAR, STAR Kids, CHIP), and each has its own rules.
2. **The sponsor side and the product side are different branches.** A sponsor can buy plans from more than one program. A plan is sold to many sponsors.
3. **The contract is the most specific level.** That is the sponsor's policy for one plan, and a class within it.

## 3. The levels

Two branches meet at the sponsor policy:

```
Platform default (in code)
  └─ Tenant (the health plan organization)
       └─ Carrier (legal entity; optional, one by default)
            └─ Program (a line of business in a state: "TX STAR", "AZ Commercial Large Group")
                 └─ Benefit plan
                                                       Sponsor
                                                         └─ Sponsor policy  (sponsor × benefit plan × dates)
                                                              └─ Class      (e.g. full-time hourly)
```

**Resolution order** for a member covered under a class of a sponsor policy, most specific first: class → sponsor policy → sponsor → benefit plan → program → carrier → tenant → platform default. The first level that sets a value wins.

**Why sponsor sits above benefit plan in precedence:** a sponsor's negotiated term (for example, a longer retro window for the whole group) applies across every plan it buys, and it is more specific to the member than the plan's default. Settings that a sponsor must not change are protected by locks and floors (§5), not by their position in the order.

**Both sponsor and sponsor policy?** Yes, because they hold different kinds of settings:

| Level | Holds | Examples |
|---|---|---|
| **Sponsor** | How the sponsor operates, across all its policies | File cadence, orphan grace period, full-replace guard, correction channel and contacts |
| **Sponsor policy** | The contract for one plan | Retro add and term limits, termination timing (end of month or date of event), premium grace period, dependent age rules |
| **Class** | Eligibility within a contract | Waiting period, eligibility rule (hours per week, pay type) |

Each setting declares which levels it may be set at (§4). This keeps the choice of level deliberate. A full-replace guard on one policy would be meaningless, because the sponsor's file covers all its policies. So that setting simply can't be set at policy level.

**Carrier** is optional and defaults to one per tenant. It matters when an organization runs several legal entities, for example an HMO and an insurance company, with different filings.

## 4. Setting definitions

A setting is defined in code by the service that owns it, and registered in a shared catalog:

| Field | Purpose |
|---|---|
| Key | `enrollment.retro.addDays`, `enrollment.orphan.graceDays`, `premium.gracePeriodDays` |
| Type | Integer, decimal, boolean, enum, duration, date rule, or a small structured value |
| Default | The platform default, optionally per line-of-business type (Commercial 7, Medicaid 0) |
| Levels | Where it may be set (for example program, sponsor, sponsor policy) |
| Constraints | Bounds and rules checked on every write and every resolution (§5) |
| Owner and description | The owning service, and plain-language help shown in the admin screen |
| Change control | `None`, `ReasonRequired` or `TwoPersonApproval` |
| Effective-dated | Whether the value follows the transaction's date (most do) |

Definitions are versioned with the code. A value can only be stored for a registered key at an allowed level, and must pass the type and constraint checks.

## 5. Locks and floors

Overrides need two kinds of limits:
- **Floors and ceilings come from law or contract.** They are written as constraints on the definition, scoped by line-of-business type or program. Example: the commercial ACA rescission rule, under which a retro term may go back only to the premium paid-through date. A value that breaks a floor is refused when it is written. Resolution clamps to the floor as a second guard and reports that it did.
- **Locks come from the plan.** An administrator can lock a value at a level, and nothing below can override it. Example: a Medicaid program locks retro limits to "the state's dates", so no sponsor or policy can change them. A lock shows in the admin screen with who set it and why.

## 6. Effective dating

Values carry `effectiveFrom` and an optional `effectiveTo`. A change for next plan year is entered ahead of time and takes effect on its date.

Resolution is **as of a date**: the date of the transaction being processed, not today. A retro change on 2026-03-01 is judged by the settings in force on 2026-03-01.

Past periods are never edited in place. A correction adds a new version with a reason, and history keeps every version.

## 7. Resolving and explaining

```
resolve(key, scope, asOf) → { value, sourceLevel, sourceId, locked, clampedBy?, trace[] }
```

- **Scope** is the member's position: tenant, and where known carrier, program, plan, sponsor, policy and class. The caller passes the ids it has. A shared client fills in what can be derived, for example a policy's plan and a plan's program.
- **Trace** lists every level checked and what it held. The answer to "why is this 90 days?" is one call, and the admin screen shows it as an "inherited from Sponsor Acme" badge.
- **"Where is this overridden?"** lists every override of a key below a node. Auditors and implementation teams ask this constantly.

## 8. What is not a setting

- **Regulatory rule packs**, such as the state appeals clocks, stay as versioned, cited data packs ([appeals-case-model.md](appeals-case-model.md)). They describe the law, not the plan's choices. Where a plan may tighten a rule-pack value, that tightening is a setting whose floor is the pack.
- **Partner technical settings** (sender IDs, file schedules) stay in trading-partner-service.
- **Benefit design** (copays, accumulators) stays in the benefit model.
- **Infrastructure configuration** (URLs, secrets) stays in app settings and Key Vault.

## 9. Where it lives

**A configuration service** owns the store, the catalog, the admin API and the audit history:
- **Services resolve through a shared client with a local cache.** Entries are keyed by tenant, key and scope.
- **Cache invalidation:** a `configuration.changed.v1` event invalidates entries, with a short expiry as the fallback for deployments without Kafka. The in-memory bus is process-local, the same constraint work-management handles with reconcile.
- **Definitions** live in a shared package, and each owning service registers its own at startup.
- **Hierarchy records stay where they belong.** Carrier and program go in tenant-service; benefit plan stays in benefit-plan-service; sponsor policy and class go in sponsor-service. The configuration service stores ids, not copies.

The alternative, a store inside each service, is what #1500 does today. It is fine for one setting. It won't scale to a single admin screen, a single audit history or a single "where is this overridden" answer.

## 10. Moving between environments

Implementation teams build configuration in a test environment and promote it to production. Settings export and import as a versioned JSON bundle, per tenant or per subtree (for example one sponsor and its policies). Ids map by business key: program code, plan code, group number and policy number.

An import is a preview first: what will change, and what each change overrides. It then applies as one audited change set. This addresses a long-standing pain with core-admin configuration migration.

## 11. Admin screen

**Admin › Plan configuration** has two trees:
- **Products:** carrier → program → plan.
- **Sponsors:** sponsor → policy → class.

**Each node shows every setting allowed at that level:**
- the effective value as of a chosen date;
- a source badge saying where the value comes from;
- actions to override, clear, lock or schedule a future value;
- the setting's history.

**Further views:**
- **"Explain for a member"** takes a member and date and shows the full trace for every setting.
- **"Where overridden"** lists every override of one key.

Changes needing a reason or a second approver use the same pattern as two-person retro approval.

## 12. Build order

1. **Catalog, store, resolver.**
   - Definitions, values with effective dating, locks and constraints.
   - The resolve and explain API, the cache client and change events.
   - Levels: tenant, line-of-business type and sponsor.
   - Move #1500's enrollment settings into it.
2. **Program and carrier records** in tenant-service.
   - Benefit plans and sponsors gain a program.
   - Line-of-business defaults move from the enum to programs (a program carries its line-of-business type).
3. **Sponsor policy and class** in sponsor-service (enrollment step 4), and their levels in the resolver.
4. **First consumers:**
   - enrollment retro limits, waiting periods, orphan grace and the full-replace guard;
   - the premium grace period, moved off the sponsor's billing info.
5. **Admin screen** with explain and where-overridden.
6. **Export and import** between environments.

## 13. Open questions

1. **Service:** a new configuration service, or a configuration module inside tenant-service? It is tenant-scoped and administrative, which suits tenant-service; a separate service keeps tenant-service small.
2. **Carrier now or later:** build the carrier level now (one per tenant by default), or add it when a multi-entity plan needs it?
3. **Who may override at sponsor and policy levels:** enrollment supervisors, account management, or tenant admins only? The permission could be per level, for example `config:sponsor`, `config:program`.
4. **#1500:** merge it as is and migrate its store in step 1, or hold it and build step 1 first?
