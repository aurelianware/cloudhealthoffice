# Plan configuration: settings that cascade, with overrides

**Status:** Agreed in review (2026-10-22); decisions in §13. Steps 1 and 2 of the build order are built (§§12.1, 12.2), and the first three parts of step 2b (§§12.3–12.5).
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

**A configuration module in tenant-service** (decided, §13) owns the store, the catalog, the admin API and the audit history. It has its own collections, its own `api/v1/configuration/...` routes, its own permissions and its own change events, so it can move to a service of its own later without callers changing:
- **Services resolve through a shared client with a local cache.** Entries are keyed by tenant, key and scope.
- **Cache invalidation:** a `configuration.changed.v1` event invalidates entries, with a short expiry as the fallback for deployments without Kafka. The in-memory bus is process-local, the same constraint work-management handles with reconcile.
- **Definitions** live in a shared package, and each owning service registers its own at startup.
- **Hierarchy records stay where they belong.** Carrier and program go in tenant-service; benefit plan stays in benefit-plan-service; sponsor policy and class go in sponsor-service. The configuration service stores ids, not copies.

The alternative, a store inside each service, is what #1500 does today (its settings store and `enrollment:configure` are interim and are replaced in step 1). It is fine for one setting. It won't scale to a single admin screen, a single audit history or a single "where is this overridden" answer.

## 9a. Who changes what

Settings changes are driven by permissions, assigned by job function:

| Permission | Allows |
|---|---|
| `config:read` | See effective settings and explain them for a member |
| `config:sponsor` | Change sponsor, sponsor-contract, policy and class settings, within the product's negotiable ranges |
| `config:product` | Change program and plan settings, including which sponsor settings are negotiable and their ranges |
| `config:enterprise` | Change tenant and carrier settings; set and remove locks; promote change sets to production |
| `config:approve` | Approve a two-person or out-of-range change, never one's own |

| Role | Holds | Works |
|---|---|---|
| **AccountManager** | `config:read`, `config:sponsor` | Enters a group's terms as a draft on the sponsor contract while working the new business or renewal |
| **Underwriter** | `config:read`, `config:approve` | Approves terms outside the negotiable ranges |
| **ConfigurationAnalyst** | `config:read`, `config:product`, `config:sponsor` | Builds programs and plans and sets the negotiable ranges |
| **ConfigurationManager** | all `config:*` | Locks, tenant and carrier settings, approvals, promotion |
| Operations (enrollment, claims, member services) | `config:read` | View and explain; questions about a sponsor's terms go to its account manager |

**Negotiable ranges.** For each sponsor-side setting the product team marks it fixed or negotiable on the program or plan, and gives a range (for example retro add 30–90 days, waiting period 0–90 days). An account manager's value inside the range activates with the contract; outside it, the change waits for an underwriter. A fixed setting is read-only to account managers. Locks and legal floors apply regardless.

**Drafts.** Sponsor-side changes are drafted against a sponsor contract term (§9b) and take effect when the term is activated, on its start date, as one audited change set naming the account manager and the contract.

## 9b. Sponsor contracts

The group agreement between the sponsor and the plan, per contract term (usually a plan year), is where sponsor-side configuration lives. It is a record in sponsor-service with the signed document in the document store:

| Section | Holds | Read by |
|---|---|---|
| Term | Contract period, renewal date, status (draft, in underwriting, signed, active, ended), the signed document | — |
| Plans offered | One **sponsor policy** per benefit plan | Enrollment, ID cards, claims eligibility |
| Tiers and rates | Tier structure (2-, 3- or 4-tier) and the rate for each tier | Premium billing, AR |
| Contribution | Employer and employee shares (optional; needed when CHO bills members directly: COBRA, retirees, direct bill) | Premium billing |
| Billing terms | List bill or self-bill, frequency, due day, grace period | Premium billing, AR (replaces the sponsor's flat billing info) |
| Eligibility by **class** | Hours, employee types, dependent rules (age limit, domestic partners) | Enrollment |
| Waiting period and effective-date rule | E.g. first of the month after 60 days | Enrollment |
| Termination and retro rules | End of month or date of event; retro windows | Enrollment |
| Life events | Allowed qualifying events, reporting windows, coverage start | Enrollment, member services |
| Documentation requirements | Proof required per event or dependent | Enrollment exceptions, correspondence |
| Underwriting conditions | Minimum participation and employer contribution | Renewal review |

**Numbers and what lasts across renewals:**
- The **group number** stays on the sponsor (`Sponsor.GroupNumber`): it identifies the account and does not change at renewal.
- Each **sponsor policy** has its own **policy (sub-group) number** under the group, e.g. 123456-0001 for the PPO and 123456-0002 for the HMO. This is the number often printed on ID cards and sent as the group number on the 834 (`REF*1L`) and the 837 (`SBR03`).
- A sponsor policy is a lasting record that continues across renewals, with its terms **versioned per contract term**. Renewal does not create new policies, so coverage, ID cards and claims never need re-pointing.
- The contract term holds the dates, status and signed document, and covers the policy terms for that year.
- **When a policy continues and when it is replaced.**
  - **Same plan, new terms** (rates, waiting period, retro window, life events): the policy continues and gains a new version of its terms, effective on the renewal date. Anything resolved for a date uses the version in force then.
  - **Different plan** (a new benefit plan, a different program or carrier, or a required change of policy number): the old policy ends the day before renewal, a new policy starts on the renewal date, and members move through the renewal's enrollment.
  - **Plan-year benefit changes** (deductibles, copays) are versions of the benefit plan itself, where accumulators reset; the sponsor policy points at the plan version in force.
  - Ending and recreating policies at every renewal is avoided: it forces every member to be termed and re-added, creates a coverage-break risk, pends claims around the boundary, reissues ID cards and splits history.
- The 834 import resolves `REF*1L` against policy numbers as well as group numbers (policy → sponsor). Today it, and the orphan retry, assume `REF*1L` is the group number.

```
Sponsor (group number)
  └─ Sponsor policy (policy / sub-group number; lasts across renewals)
       ├─ Classes
       └─ Terms per contract year (rates, eligibility, settings)
Contract term (dates, status, signed document) ── covers ──> that year's policy terms
```

**Legal floors on these** (to be verified against the regulation text when built): group waiting periods of at most 90 days; dependent coverage to age 26; special enrollment windows of at least 30 days for group plans, and 60 days after loss of Medicaid or CHIP.

**Flow:** a new group or renewal opens a draft term → the account manager enters plans, tiers, rates, eligibility and settings → anything out of range goes to underwriting → on signature the document is attached and the term activates on its start date → enrollment and billing resolve from it as of each member's dates. A detailed design follows at enrollment step 4.

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

1. **Catalog, store, resolver** (in tenant-service). *Built 2026-10-22 (§12.1); the tenant controls moved to step 2b.*
   - Definitions, values with effective dating, locks and constraints.
   - The resolve and explain API, the cache client and change events.
   - Levels: tenant, line-of-business type and sponsor.
   - Move #1500's enrollment settings into it; retire its interim store, endpoints and `enrollment:configure`.
   - The `config:*` permissions and the roles in §9a.
2. **Program and carrier records** in tenant-service. *Built 2026-10-23 (§12.2).*
   - Carrier and program records, one carrier per tenant created automatically.
   - Benefit plans and sponsors gain a program.
   - Carrier, program and benefit-plan levels in the resolver; naming a program resolves the levels above it.
   - `CrdContextResolver` reads the member's benefit plan → program.
   - **Still to do, as step 2b.** Each one is a consumer change rather than a new record, so they move one at a time behind the fields step 2 added:
     - Legal-entity details move onto the carrier: NACHA origination (premium-billing, capitation, payment) and the 835 payer, *built 2026-10-23 (§12.3)*; the name and address used on letters and ID cards (correspondence, idcard), *built 2026-10-23 (§12.4)*.
     - Tenant controls become catalog settings at tenant level: `payment.enforceSeparationOfDuties` (capitation-service), `personalRepresentative.requireSecondUserActivation` (personal-representative-service), *built 2026-10-23 (§12.5)*. Technical and integration configuration (modules, region, SFTP, clearinghouse, platform adapters) stays tenant configuration.
     - The `overrides` endpoint narrows by hierarchy: a carrier's programs, a program's plans, a sponsor's policies.
     - The engine's `ResolvedRuleSetKey` becomes a program id rather than a string concatenation of state, line of business and program code.
3. **Sponsor contracts** (§9b) in sponsor-service, with sponsor policies and classes, draft terms and underwriting approval (enrollment step 4), and their levels in the resolver.
4. **First consumers:**
   - enrollment retro limits, waiting periods, orphan grace and the full-replace guard;
   - the premium grace period, moved off the sponsor's billing info.
5. **Admin screen** with explain and where-overridden.
6. **Export and import** between environments.

### 12.1 What step 1 built

- **Shared package `CloudHealthOffice.PlanConfiguration`** (src/services/shared):
  - **Definitions** (`SettingDefinition`): key, type (integer, decimal, boolean, enum, duration, JSON), owner, description, unit, default and defaults per line-of-business type, allowed levels, constraints (range, allowed values, floors scoped by line of business with a citation), change control and whether the setting is effective-dated.
  - **The catalog** ships in code (`PlanSettings`, `PlanSettingCatalog`): a service adds its definition there and tenant-service accepts values for it with no other change. `PlanSettingCatalog.Require(key)` fails fast on an undefined key.
  - **The resolution rules** (`SettingResolver`, `SettingValidator`), shared by tenant-service and the in-process client so both answer the same way.
  - **Levels** are an ordered enum with room between them (tenant 100, carrier 200, line of business 300, program 400, benefit plan 500, sponsor 600, sponsor policy 700, class 800). Adding a level is a new definition entry and a scope field, not a resolver change. Step 1 held values at **tenant, line-of-business type and sponsor** (scope id: group number); step 2 added **carrier** (scope id: carrier id), **program** (program id) and **benefit plan** (the plan id a coverage carries).
  - **The client** (`IPlanSettings`, `AddPlanSettingsClient`): resolves through tenant-service, caches per tenant, key, scope and date for `PlanSettings:CacheSeconds` (60), and evicts a key's answers on `configuration.changed.v1` when Kafka is configured (each process listens in its own consumer group). It never throws for an unreachable tenant-service: it answers with the definition's default for the line of business, marked `IsFallback`, logs a warning and does not cache the fallback. `InMemoryPlanSettings` applies the same rules for tests.
- **Configuration module in tenant-service** (collections `configuration-values`, `configuration-pending`, `configuration-changes`; routes `api/v1/configuration/...`, all needing `config:read`):
  - `GET catalog`;
  - `GET resolve?key&asOf&lineOfBusiness&sponsor` (and `POST resolve` for up to 100): value, source level and node, lock, clamp and the trace of every level checked;
  - `GET overrides?key&level&scopeId`: every override at the node's level and all more specific levels, with the value in force and the next scheduled change (narrowing by hierarchy arrives with programs, plans and policies);
  - `GET values/{level}/{scopeId}/{key}/history`, `GET changes?key`;
  - `PUT values/{level}/{scopeId}/{key}` `{value, effectiveFrom, effectiveTo, locked, reason}` and `DELETE` (clear from a date). Nodes: `tenant/_`, `lineOfBusiness/Commercial`, `sponsor/{groupNumber}`. Each write needs the level's permission, is checked for level, type, range, floors (refused at line-of-business level; clamped at resolution elsewhere) and a reason where required, and adds a version — values are never edited in place;
  - two-person settings answer 202 with a pending change; `POST pending/{id}/approve|reject` needs `config:approve`, never by the requester, decided once (compare-and-swap on its status);
  - every applied change is recorded (who, when, why, before, after, version) and published on `configuration.changed.v1`, best-effort: the change is stored first, and caches expire on their own if an event is lost.
- **Effective dating:** at a date a node's value is its version in force then with the latest start, the highest version breaking ties; a cleared version means the node sets nothing from that date; an end date lets an earlier version apply again.
- **Locks:** a locked value at a general level stops every more specific level, and the trace shows the ignored overrides.
- **Permissions and roles:** `config:read`, `config:sponsor`, `config:product`, `config:enterprise`, `config:approve` on `ChoRolePermissions`; roles AccountManager, Underwriter, ConfigurationAnalyst, ConfigurationManager (and the tenant-service role catalogue and portal role names); `config:read` added to EnrollmentSpecialist, ClaimsSupervisor, MemberServices and ComplianceViewer (ComplianceOfficer has it through `*:read`). Until programs bring negotiable ranges, `config:sponsor` writes are bounded by each definition's constraints.
- **First setting moved:** `enrollment.orphan.graceDays`. enrollment-import resolves it through the client; on a fallback it opens the orphan rather than hold it on a guessed default. The interim `enrollment-settings` store, `api/v1/enrollment/settings` and `enrollment:configure` are removed; stored interim values were not migrated (no tenant had set any) and the benefit-plan level returns with step 2.
- **Deferred to step 2b:** the tenant controls (`payment.enforceSeparationOfDuties`, `personalRepresentative.requireSecondUserActivation`) became catalog settings together with their consumers in §12.5.

### 12.2 What step 2 built

- **Carrier and program records** (tenant-service `Hierarchy/`; collections `carriers`, `programs`):
  - A **carrier** is a legal entity of the tenant: code (upper case, unique in the tenant), name, legal name, tax ID, NAIC code, payer IDs per transaction, NACHA immediate origin and company identification, and the name and address used on letters and ID cards. One per tenant, code `DEFAULT`, created from the tenant's own record the first time carriers are read — so no caller has to handle "this tenant has no carrier yet", and two concurrent readers cannot create two (the id is `{tenant}|{code}`, so the loser of the insert reads the winner's). A tenant must keep one active carrier.
  - A **program** is a line of business in a state under a carrier: code, name, line-of-business type (a canonical `LinesOfBusiness` name), two-letter state, regulatory contract number, effective dates, active. A program's code, carrier, state and line of business are **fixed once it exists**: every rule set, setting and filing keyed on the program would move with them, so a change means a new program and retiring the old one. Its name, contract number, dates and status can change.
  - Routes `api/v1/carriers` and `api/v1/programs` (list, read, create, patch), all needing `config:read`; a carrier write needs `config:enterprise` and a program write `config:product`, the same split `SettingValidator.WritePermissionFor` applies to settings at those levels. Both are revision-guarded, as the tenant record is.
- **`GET api/v1/programs/by-benefit-plan/{planId}`** follows the plan → program link that benefit-plan-service holds, so every consumer asks one service one question (§7) rather than joining two records itself. Its **404 means the plan carries no program**; a **503 means benefit-plan-service could not be asked**. A caller that could not tell those apart would read an outage as a member with no program.
- **Benefit plans and sponsors carry a program.** `BenefitPlan.ProgramId` and `Sponsor.ProgramId`, both nullable: every record written before programs existed carries none, and a reader must treat null as "not stated" rather than guessing from the line-of-business enum, which carries no state and no program code. Where both are set the program is the authority on the line of business — it is the one a contract was written against — but they are not reconciled on write, because a plan can be given a program long after it was created and refusing the link would leave the plan with neither. A sponsor that buys from more than one program leaves its own field null and carries the program on each plan: the two branches meet at the sponsor policy, not at the sponsor (§3).
- **Resolution through the new levels.** `SettingScope` gains `Carrier`, `Program` and `BenefitPlan`, so the chain runs sponsor → benefit plan → program → line of business → carrier → tenant → platform default. **Naming a program is enough to place a member:** `SettingScope.CompletedFrom` reads the program's carrier and line-of-business type off the one record, so a caller holding a single program id resolves the whole branch above it — which is what "line-of-business defaults move from the enum to programs" amounts to in practice. What the caller already stated wins, so a record it did not name cannot contradict it, and a program the tenant does not have is left as given (the resolution then simply finds nothing at that node rather than failing a read of an unrelated setting).
- **`IProgramDirectory`** (shared `CloudHealthOffice.PlanConfiguration`): `GetAsync` and `ForBenefitPlanAsync`, reading tenant-service, cached per tenant for `ProgramDirectory:CacheSeconds` (300 — programs change about once a plan year). It answers null for a program that is not there and **throws when tenant-service could not be asked**, so a caller can tell "no program" from "did not get to look". `InMemoryProgramDirectory` for tests and hosts without tenant-service.
- **First consumer: `CrdContextResolver`** (fhir-service). The prior-authorization rule engine keys its rule sets on state, line of business and program, and a program record carries all three. Where the member's coverage names a benefit plan sold under a program, all three now come from the program: the line of business the contract was written against, and the state the plan operates in, rather than whichever state the member's mailing address is in. Where there is no program, state and line of business come from the address and the coverage's `cho-line-of-business` coding as before, and `Program` stays null — a correct answer, just a less specific one. **If the program could not be looked up, the context is not filled in from the address instead:** the resolution fails with the IG's `technical` reason and the engine is not called. A member on a Texas STAR plan living in Ohio would otherwise get an Ohio rule set during an outage, which is the confident-answer-about-someone-else's-benefits failure the resolver exists to stop. A coverage that names no plan has nothing to look up, so it takes the address path without a call.
- **Deferred to step 2b** (§12): the money files and the 835 payer moved onto the carrier in §12.3, the letterhead in §12.4 and the two tenant controls in §12.5; still to come are hierarchy narrowing in `overrides` and the rule-set key becoming a program id.

### 12.3 Step 2b, first part: the money files and the 835 payer

Built 2026-10-23. Who the bank and the provider are told is paying.

- **What this actually moved.** The step 2 plan called this "move from tenant configuration to the carrier", which overstated where it was. NACHA origination and the payer identity were never tenant configuration: they were deployment `appsettings` — `Nacha:ImmediateOrigin`, `Nacha:CompanyId`, `Nacha:CompanyName`, `Payer:Name`, `Payer:Id` — one identity shared by every tenant in a deployment. A deployment serving two tenants originated both under the same entity, and nothing recorded that this was wrong. So this is a promotion rather than a move: the identity becomes per-carrier, which is per-tenant by default and per-legal-entity where a tenant runs two.
- **`ICarrierDirectory`** (shared `CloudHealthOffice.PlanConfiguration`), beside `IProgramDirectory` and built the same way: `DefaultAsync` and `GetAsync`, reading `api/v1/carriers`, cached per tenant for `CarrierDirectory:CacheSeconds` (300). It reads the **list**, because listing is what materializes a tenant's default carrier, so a tenant that has never had one written still gets an answer. `Carrier.ToIdentity()` is the contract, and it copies the payer ids rather than sharing them, since the record is mutable and the directory caches what it is given.
- **`CarrierIdentity.LetterheadName`** is the correspondence name, else the legal name, else **null** — deliberately never `Name`, which is what staff call the carrier and is filled in for every carrier including the default one, where it is the tenant's own organization name. A consumer falling back on it would have started putting that name on files the day carriers existed. Null means the carrier states no letterhead, and the consumer keeps its configured name.
- **Consumers.** premium-billing (sponsor debits), capitation (provider credits) and payment (FFS provider credits, and the 835's `N1*PR` payer name and identifier, in both payment and reversal runs) ask the carrier first and fall back to their configured values field by field. A carrier that states nothing changes nothing, which is what every deployment looks like the day this ships: filling a field in on the carrier is the migration.
- **Fail closed, consistently with §12.2.** An unreachable tenant-service **throws** rather than falling back to configuration. From a consumer's side a carrier that states nothing and a carrier that could not be read look identical, and only the first is safe to originate under the configured entity — the second could draw on a sponsor's account, or pay a provider, in the wrong legal entity's name. The release fails and the drafts or disbursements go back to Pending for the next release; nothing is sent. Same rule as the CRD resolver, same reason: "could not look" must not read as "there is none".
- **One invariant kept.** A provider reassociates an EFT deposit with its 835 on the originating company id, which is the remittance's TRN03. payment-service already refused a `Nacha:CompanyId` that differed from `Era:OriginatingCompanyId`; the check now covers a company id stated by the carrier too, and names which carrier stated it. The file-ID-modifier allocation probe is built from the same carrier, so a carrier with its own origin is correctly a distinct originator at the bank.
- **Not in this part:** correspondence-service has no return address and the ID card renders only the plan name, so the letter and ID-card issuer block is new rendering rather than a moved read. It follows in §12.4.

### 12.4 Step 2b, second part: letters and ID cards name their issuer

Built 2026-10-23. Who the member is told the document is from.

- **What this actually moved.** Nothing, in the sense step 2 meant. correspondence-service had **no sender at all**: a letter carried the recipient's address, the letter's own merge data, and nothing saying who sent it. The ID card was headed by the **sponsor** — the employer group — which is not what a member ID card is conventionally headed by, and was that way not by decision but because CHO had no issuer to put there. So this part adds rendering rather than redirecting a read, which is why it is separate from §12.3.
- **Letters, two ways.** A new `TemplateBlockKind.ReturnAddressBlock` left blank renders the carrier's name and address, exactly as a blank `AddressBlock` renders the recipient's; and a template that lays out its own letterhead reaches the same values through `{{ carrier.name }}`, `{{ carrier.city }}` and so on. Only **stated** values are offered, so a template naming `{{ carrier.name }}` where the carrier states none leaves the path unresolved, which fails the `MergeFieldsResolved` check. Either way the letter is **not sent**: a notice of appeal rights headed by a blank line, or by whatever the deployment happens to be called, is worse than one that fails its checks. Every template that exists today has neither, so none of them changes.
- **The written record.** A letter is stored write-once and its data snapshot is the audit trail. It has always recorded who the letter went *to*; it now also records which legal entity it went out *under* (`carrier.id` and the letterhead name as rendered). A letter composed with no carrier records `carrier: null` rather than an invented one. The **preview** uses the same carrier, so a reviewer approving a template sees what will be on the page.
- **ID cards.** `CardBindings` gains `IssuerName` and `IssuerAddress`. The card is headed by the issuer and carries its address on the back; the SVG layout gains `{{IssuerName}}`, `{{IssuerAddress}}` and `{{CardTitle}}`, where **`CardTitle` is the one that falls back** to the sponsor. A tenant whose carrier states no letterhead name therefore gets exactly the card it got before, and the employer group stays in the Group field where it belongs. A carrier with no address puts no blank line on the card.
- **Fail closed, as in §12.3.** An unreachable tenant-service throws: composing a letter or issuing a card fails rather than producing one that names the wrong issuer, or none. In idcard-service the carrier is read in the same parallel wave as the sponsor and plan, so it costs no extra round trip.

### 12.5 Step 2b, third part: the two tenant controls become settings

Built 2026-10-23. Two switches that decide whether one person can act alone.

- **What moved.** `payment.enforceSeparationOfDuties` (capitation-service: who may release a payment run) and `personalRepresentative.requireSecondUserActivation` (personal-representative-service: who may activate a representative) were read straight out of the tenant document, each by a small cache and HTTP client of its own that duplicated the other almost line for line. Both are now catalog settings, resolved through `IPlanSettings`, so they get effective dating, history, a reason on every change, the `config:*` permissions, the admin screen and change-event eviction — none of which the tenant document gave them.
- **Tenant level only, and two-person approval to change.** Neither is something a sponsor or a program negotiates: they are internal controls over the tenant's own staff. Both default to `true`, and **turning either off needs a second person with `config:approve`** — `ChangeControl.TwoPersonApproval`. A maker-checker rule that one person can remove single-handedly is not a maker-checker rule, and the configuration module already had the mechanism.
- **Fail closed, for free.** The client answers the definition's default and marks it `IsFallback` when tenant-service cannot be reached, and the default is the enforced one, so an outage cannot turn either control off. The consumers do not read the tenant document in that case: the service that just failed is the same one that holds it, and a 404 or an empty body there would read as "no override" — the "could not look" / "there is none" confusion §12.2 exists to prevent.
- **The one tenant whose behaviour must not change.** Where **nothing is set** in plan configuration, both consumers still read the tenant document, so a tenant that turned a control off before plan configuration existed keeps the behaviour it configured rather than having a two-person rule silently switched back on. That read logs a warning naming the tenant and the setting to move the value to, and it goes away once the values are moved. Where a value **is** set, plan configuration is the authority even where the two disagree, and the tenant document is not read at all.
- **Audited the same way.** personal-representative-service already wrote an audit warning and a `secondPersonOverride` flag on the activation event whenever the creator activated alone. Which of the two places the override came from does not change what is being recorded — that one person acted alone — so the audit trail is untouched.

## 13. Decisions (2026-10-22)

1. **Where it lives:** a module in tenant-service with a clean boundary (§9). Carrier and program live there too, and a new service would be operational cost without a need yet.
2. **Carrier:** built now, one per tenant by default. Legal-entity details move onto it; tenant controls become settings; technical configuration stays (step 2).
3. **Who changes what:** permission-driven by job function (§9a). Account managers configure sponsor terms as drafts on the contract, within negotiable ranges; underwriters approve out-of-range terms; configuration analysts own products; operations staff read and explain.
4. **#1500:** merged as is; its store is interim and moves into the catalog in step 1.
5. **Sponsor contracts** are the home of sponsor-side configuration (§9b).
6. **Change requests from operations** are tracked in CHO. "Request a change" on any setting raises a configuration work item (work type `ConfigurationChange`) in work-management, carrying the setting, the node, the effective value with its source, and the member or exception that prompted it. Routing: sponsor, policy and class settings to the sponsor's account manager; program and plan settings to a configuration queue; tenant and carrier settings to the configuration manager. The request links to the resulting change set and any approval. An optional external ticket reference holds a ServiceNow or Jira number; a two-way connector waits for a pilot that needs it.
7. **CRM:** CHO is the system of record for the signed contract terms that enrollment, claims and billing execute against. The CRM keeps the sales pipeline (prospects, quotes, brokers, renewal opportunities). The contract term stores the CRM opportunity id and link, and CHO exposes "create draft contract term" so a won opportunity can open the draft; a Salesforce connector waits for a pilot that uses it.
